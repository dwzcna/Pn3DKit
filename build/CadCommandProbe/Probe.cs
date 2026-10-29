using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text.Json;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Pn3DKit.Models;
using Pn3DKit.Services;
using Pn3DKit.Utils;
using AcadApp=Autodesk.AutoCAD.ApplicationServices.Core.Application;

namespace Pn3DKit.CadCommandProbe;

/// <summary>在专用副本中对照旧LISP与CommandAsync，不保存或修改用户原图。</summary>
public static class Probe
{
    /// <summary>由Session入口等待全部命令和收尾；状态未知时保留关闭变量和现场。</summary>
    /// <param name="document">配置登记且当前活动的工具副本。</param>
    /// <param name="product">本轮候选产品程序集路径，仅用于证据。</param>
    /// <param name="output">本次独占验证目录。</param>
    /// <param name="ensure">CAD主线程内完整数据库身份检查。</param>
    /// <returns>API原型结论和独立的完整验收标记，包含实际取消是否已验证。</returns>
    public static async Task<string> RunAsync(Document document,string product,string output,Action ensure)
    {
        int thread=Environment.CurrentManagedThreadId;
        bool known=true,success=false,cancellationVerified=false;
        int? originalAutoCheck=null;
        var checks=new List<object>();var original=new Dictionary<string,object>();var restoreErrors=new List<string>();
        var files=new RunLogFiles();
        using var log=new StreamWriter(new FileStream(Path.Combine(output,"prototype.log"),FileMode.CreateNew,FileAccess.Write,FileShare.Read)){AutoFlush=true};
        void CheckThread(){if(Environment.CurrentManagedThreadId!=thread)throw new InvalidOperationException("SDK续体离开主线程。");ensure();}
        void Passed(string name,object? details=null){checks.Add(new{Name=name,Passed=true,Details=details});log.WriteLine("通过："+name);}
        try
        {
            CheckThread();
            foreach(string name in new[]{"LOGFILEMODE","LOGFILEPATH","FILEDIA","CMDDIA","PROXYNOTICE"})
                original[name]=AcadApp.GetSystemVariable(name);
            using(var environmentRecord=new StreamWriter(new FileStream(Path.Combine(output,"original-environment.json"),FileMode.CreateNew)))
                environmentRecord.Write(JsonSerializer.Serialize(new{Variables=original,AutoCheck=NativeEnvironment.ReadRaw()},new JsonSerializerOptions{WriteIndented=true,IncludeFields=true}));
            AcadApp.SetSystemVariable("LOGFILEPATH",output);
            AcadApp.SetSystemVariable("LOGFILEMODE",1);
            log.WriteLine("CAD原始命令日志："+AcadApp.GetSystemVariable("LOGFILENAME"));
            // 只允许登记到工具复制的示例项目，确保导出命令面对真实的项目图纸。
            var plantType=Type.GetType("Autodesk.ProcessPower.PlantInstance.PlantApplication, PnPProjectManagerMgd",true)!;
            dynamic project=plantType.GetProperty("CurrentProject")!.GetValue(null)
                ?? throw new InvalidOperationException("专用Plant项目未打开。");
            string projectRoot=Path.GetFullPath((string)project.ProjectFolderPath);
            if(!projectRoot.Contains(Path.DirectorySeparatorChar+"maintenance-47-",StringComparison.OrdinalIgnoreCase)
                || !Path.GetFileName(projectRoot).Equals("PlantProjectFixture",StringComparison.OrdinalIgnoreCase)
                || !ExportPolicy.Inside(Path.GetFullPath(document.Database.Filename),projectRoot))
                throw new InvalidOperationException("当前项目或测试图纸不在工具拥有的项目副本中。");
            dynamic part=project.ProjectParts["PnId"];
            var drawings=(System.Collections.IEnumerable)part.GetPnPDrawingFiles();
            bool registered=false;
            foreach(dynamic file in drawings)
                if(string.Equals((string)file.ResolvedFilePath,document.Database.Filename,StringComparison.OrdinalIgnoreCase))registered=true;
            if(!registered)
            {
                bool alreadyExists=false,illegalXrefs=false;
                string guid=part.AddPnPDrawingFile(document.Database.Filename,"","Pn3DKit Command Fixture",ref alreadyExists,ref illegalXrefs);
                if(string.IsNullOrEmpty(guid)||illegalXrefs)throw new InvalidOperationException("测试图纸登记失败："+guid+"；已有="+alreadyExists+"；非法外参="+illegalXrefs);
                log.WriteLine("工具测试图纸已登记："+guid);
            }
            log.WriteLine("自动检查原始配置："+NativeEnvironment.ReadRaw());
            originalAutoCheck=NativeEnvironment.Read();
            // 两种原始有效值均在真实宿主验证关闭、回读和恢复，再回到本次原值。
            var scopeType=typeof(ExportPolicy).Assembly.GetType("Pn3DKit.Services.ExportAutoCheckScope",true)!;
            foreach(int seed in new[]{0,1})
            {
                NativeEnvironment.Write(seed);
                object scope=Activator.CreateInstance(scopeType,BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic,null,
                    new object[]{(Func<object?>)(()=>NativeEnvironment.Read()),(Action<object>)(value=>NativeEnvironment.Write(Convert.ToInt32(value)))},null)!;
                log.WriteLine(scopeType.GetMethod("Disable",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(scope,null));
                if(NativeEnvironment.Read()!=0)throw new InvalidOperationException("原生配置保护未保持关闭。");
                object outcome=scopeType.GetMethod("Finish",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(scope,new object?[]{null})!;
                if((bool)outcome.GetType().GetProperty("HasIssue")!.GetValue(outcome)!||NativeEnvironment.Read()!=seed)
                    throw new InvalidOperationException("原生配置保护恢复失败。");
                Passed("真实自动检查关闭与恢复："+seed);
            }
            NativeEnvironment.Write(0);
            if(NativeEnvironment.Read()!=0)throw new InvalidOperationException("自动检查未关闭。");
            foreach(string name in new[]{"FILEDIA","CMDDIA","PROXYNOTICE"})
            {
                AcadApp.SetSystemVariable(name,0);
                if(Convert.ToInt32(AcadApp.GetSystemVariable(name))!=0)throw new InvalidOperationException("无法关闭："+name);
            }
            if(document.IsReadOnly)throw new InvalidOperationException("测试副本只读，未写入或调用QSAVE。");
            // 仅重建已验证路径下的可丢弃Fixture内容，原活动用户图纸不在此代码范围。
            using(document.LockDocument())
            using(var transaction=document.Database.TransactionManager.StartTransaction())
            {
                var table=(BlockTable)transaction.GetObject(document.Database.BlockTableId,OpenMode.ForRead);
                var model=(BlockTableRecord)transaction.GetObject(table[BlockTableRecord.ModelSpace],OpenMode.ForWrite);
                foreach(ObjectId id in model)transaction.GetObject(id,OpenMode.ForWrite).Erase();
                var line=new Line(new Point3d(0,0,0),new Point3d(20,10,0)){ColorIndex=1,LineWeight=LineWeight.LineWeight030};
                var circle=new Circle(new Point3d(30,20,0),Vector3d.ZAxis,8){ColorIndex=5};
                var text=new DBText{Position=new Point3d(0,25,0),Height=2.5,TextString="Pn3DKit prototype47"};
                foreach(Entity entity in new Entity[]{line,circle,text}){model.AppendEntity(entity);transaction.AddNewlyCreatedDBObject(entity,true);}
                transaction.Commit();
            }
            var type=typeof(ExportPolicy).Assembly.GetType("Pn3DKit.Interop.AutoCadHost",true)!;
            var legacyHost=(ICadHost)Activator.CreateInstance(type,BindingFlags.Instance|BindingFlags.NonPublic,null,
                new object[]{(Func<bool>)(()=>{CheckThread();return true;}),files},null)!;
            var host=new NativeProbeHost(legacyHost,document,CheckThread,args=>Native(document,args,CheckThread,log));
            var drawing=host.OriginalDrawing!;
            log.WriteLine("旧LISP保存开始。");
            known=false;await legacyHost.SaveAsync(drawing,Path.Combine(output,"old-save.done"));known=true;
            log.WriteLine("旧LISP保存已返回，开始直接核对DBMOD。");
            CheckThread();
            if((await host.ReadStateAsync(drawing)).Modified)throw new InvalidOperationException("旧保存流程返回后仍有修改。");
            Passed("旧LISP保存完成");
            string oldOutput=Path.Combine(output,"old-export.dwg");
            log.WriteLine("旧LISP导出开始。");
            known=false;await legacyHost.ExportAsync(drawing,oldOutput,"2018",Path.Combine(output,"old-export.done"));known=true;
            log.WriteLine("旧LISP导出已返回。");
            bool oldExportReady=File.Exists(oldOutput);
            if(oldExportReady){ExportPolicy.ValidateOutput(oldOutput,"2018");Passed("旧LISP导出完成");}
            else log.WriteLine("旧LISP返回但未生成DWG，继续仅作Native诊断，不判定新旧对照通过。");

            // 再次制造本副本的修改，验证Native QSAVE确实完成而不是只排队。
            using(document.LockDocument())
            using(var transaction=document.Database.TransactionManager.StartTransaction())
            {
                var table=(BlockTable)transaction.GetObject(document.Database.BlockTableId,OpenMode.ForRead);
                var model=(BlockTableRecord)transaction.GetObject(table[BlockTableRecord.ModelSpace],OpenMode.ForWrite);
                var temporary=new DBPoint(Point3d.Origin);
                model.AppendEntity(temporary);transaction.AddNewlyCreatedDBObject(temporary,true);
                temporary.Erase();transaction.Commit();
            }
            if(Convert.ToInt32(AcadApp.GetSystemVariable("DBMOD"))==0)throw new InvalidOperationException("未能建立待保存的副本修改。");
            known=false;await Native(document,new object[]{"_.QSAVE"},CheckThread,log);known=true;
            if(Convert.ToInt32(AcadApp.GetSystemVariable("DBMOD"))!=0)throw new InvalidOperationException("CommandAsync QSAVE后DBMOD不为0。");
            Passed("CommandAsync保存结束及DBMOD");

            string nativeOutput=Path.Combine(output,"native-export.dwg");
            known=false;await Native(document,ExportPolicy.ExportArguments(nativeOutput,"2018"),CheckThread,log);known=true;
            ExportPolicy.ValidateOutput(nativeOutput,"2018");
            Passed("CommandAsync导出文件已生成且格式正确");
            if(!oldExportReady)throw new InvalidOperationException("旧LISP没有可对照输出，原型验收未通过。");
            string[] before=Snapshot(oldOutput),after=Snapshot(nativeOutput);
            if(!before.SequenceEqual(after))throw new InvalidOperationException("新旧导出内容不符："+JsonSerializer.Serialize(new{before,after}));
            Passed("新旧导出格式及普通二维内容对照",before);

            bool unknownRejected=false;
            try{known=false;await Native(document,new object[]{"_.PN3DKIT_NO_SUCH_COMMAND_47"},CheckThread,log);known=true;}
            catch(CadOperationException ex) when(ex.StateKnown){known=true;unknownRejected=true;}
            if(!unknownRejected)throw new InvalidOperationException("未知命令未被明确拒绝。");
            Passed("未知命令失败状态已知");

            using(document.LockDocument())
            using(var transaction=document.Database.TransactionManager.StartTransaction())
            {
                var table=(BlockTable)transaction.GetObject(document.Database.BlockTableId,OpenMode.ForRead);
                var model=(BlockTableRecord)transaction.GetObject(table[BlockTableRecord.ModelSpace],OpenMode.ForWrite);
                var temporary=new DBPoint(Point3d.Origin);model.AppendEntity(temporary);
                transaction.AddNewlyCreatedDBObject(temporary,true);temporary.Erase();transaction.Commit();
            }
            int forbidden=0;
            void ObserveSave(object? sender,CommandEventArgs e)
            {
                string name=e.GlobalCommandName.TrimStart('_','.','-');
                if(name.Equals("QSAVE",StringComparison.OrdinalIgnoreCase)||name.Equals("EXPORTTOAUTOCAD",StringComparison.OrdinalIgnoreCase))forbidden++;
            }
            document.CommandWillStart+=ObserveSave;
            try
            {
                var skipped=await new BatchExporter().RunAsync(new(Path.GetDirectoryName(document.Database.Filename)!,
                    new[]{document.Database.Filename},null),new(ProjectPartKind.PnId,false,"2018",Path.Combine(output,"skip-n")),
                    host,message=>log.WriteLine(message),files:new RunLogFiles());
                CheckThread();
                if(skipped.StopReason!=null||skipped.Items.Count!=1||skipped.Items[0].Status!="Skipped"||forbidden!=0
                    || !(await host.ReadStateAsync(drawing)).Modified)throw new InvalidOperationException("选择N未保持修改或错误执行命令。");
                Passed("真实宿主选择N不保存不导出");
            }
            finally{document.CommandWillStart-=ObserveSave;}
            known=false;await Native(document,new object[]{"_.QSAVE"},CheckThread,log);known=true;

            Document? readOnly=null;
            string readOnlyPath=Path.Combine(output,"ReadOnlyFixture.dwg");
            File.Copy(document.Database.Filename,readOnlyPath,false);
            try
            {
                known=false;
                readOnly=AcadApp.DocumentManager.Open(readOnlyPath,true);
                var readOnlyIdentity=readOnly.Database.BlockTableId;
                void EnsureReadOnly()
                {
                    if(Environment.CurrentManagedThreadId!=thread||readOnly.Database.IsDisposed
                        ||readOnly.Database.BlockTableId!=readOnlyIdentity
                        ||AcadApp.DocumentManager.MdiActiveDocument?.Database.BlockTableId!=readOnlyIdentity)
                        throw new InvalidOperationException("只读测试上下文变化。");
                }
                AcadApp.DocumentManager.MdiActiveDocument=readOnly;
                EnsureReadOnly();known=true;
                var readLegacy=(ICadHost)Activator.CreateInstance(type,BindingFlags.Instance|BindingFlags.NonPublic,null,
                    new object[]{(Func<bool>)(()=>{EnsureReadOnly();return true;}),new RunLogFiles()},null)!;
                var readHost=new NativeProbeHost(readLegacy,readOnly,EnsureReadOnly,args=>Native(readOnly,args,EnsureReadOnly,log));
                var readState=await readHost.ReadStateAsync(readHost.OriginalDrawing!);
                if(!readState.ReadOnly)throw new InvalidOperationException("测试图纸未按只读方式打开。");
                using(readOnly.LockDocument())
                using(var transaction=readOnly.Database.TransactionManager.StartTransaction())
                {
                    var table=(BlockTable)transaction.GetObject(readOnly.Database.BlockTableId,OpenMode.ForRead);
                    var model=(BlockTableRecord)transaction.GetObject(table[BlockTableRecord.ModelSpace],OpenMode.ForWrite);
                    var temporary=new DBPoint(Point3d.Origin);
                    model.AppendEntity(temporary);transaction.AddNewlyCreatedDBObject(temporary,true);
                    temporary.Erase();transaction.Commit();
                }
                if(!(await readHost.ReadStateAsync(readHost.OriginalDrawing!)).Modified)
                    throw new InvalidOperationException("只读副本未产生用于拒绝保存验证的内存修改。");
                forbidden=0;readOnly.CommandWillStart+=ObserveSave;
                try
                {
                    var skipped=await new BatchExporter().RunAsync(new(Path.GetDirectoryName(readOnlyPath)!,new[]{readOnlyPath},null),
                        new(ProjectPartKind.PnId,true,"2018",Path.Combine(output,"skip-readonly")),readHost,
                        message=>log.WriteLine(message),files:new RunLogFiles());
                    EnsureReadOnly();
                    if(skipped.StopReason!=null||skipped.Items.Count!=1||skipped.Items[0].Status!="Failed"||forbidden!=0
                        ||!skipped.Items[0].Message.Contains("只读",StringComparison.Ordinal))
                        throw new InvalidOperationException("有修改的只读图纸未被安全拒绝。");
                    Passed("真实只读图纸有修改时拒绝保存与导出");
                }
                finally{readOnly.CommandWillStart-=ObserveSave;}
            }
            finally
            {
                if(known)
                {
                    AcadApp.DocumentManager.MdiActiveDocument=document;
                    if(readOnly!=null)readOnly.CloseAndDiscard();
                }
            }
            CheckThread();
            using(var config=JsonDocument.Parse(File.ReadAllText(Path.Combine(Path.GetDirectoryName(output)!,"native-command.json"))))
            {
                if(config.RootElement.TryGetProperty("RunCancellation",out var cancellation)&&cancellation.GetBoolean())
                {
                    using(var waiting=new StreamWriter(new FileStream(Path.Combine(output,"awaiting-esc.txt"),FileMode.CreateNew)))
                        waiting.Write("请在原型LINE选点提示处按Esc，勿指定点。");
                    document.Editor.WriteMessage("\n原型取消验收：请在接下来的LINE选点提示按Esc，勿指定点。");
                    bool cancelled=false;
                    try{known=false;await Native(document,new object[]{"_.LINE",Autodesk.AutoCAD.EditorInput.Editor.PauseToken,""},CheckThread,log);known=true;}
                    catch(CadOperationException ex) when(ex.StateKnown&&ex.Canceled){known=true;cancelled=true;}
                    if(!cancelled)throw new InvalidOperationException("未观察到实际Esc取消。");
                    cancellationVerified=true;Passed("真实Esc取消及命令收尾");
                }
                else checks.Add(new{Name="实际Esc留待单独运行",Pending=true});
            }
            success=true;
        }
        catch(System.Exception ex){if(ex is CadOperationException {StateKnown:true})known=true;success=false;log.WriteLine(ex.ToString());checks.Add(new{Name="原型错误",Passed=false,Error=ex.ToString()});}
        finally
        {
            if(known)
                foreach(var pair in original)
                    try{AcadApp.SetSystemVariable(pair.Key,pair.Value);if(!Equals(AcadApp.GetSystemVariable(pair.Key),pair.Value))throw new InvalidOperationException("回读不符");}
                    catch(System.Exception ex){restoreErrors.Add(pair.Key+"："+ex.Message);}
            else restoreErrors.Add("原生状态未知，未恢复变量或继续图纸操作，请CAD空闲后核对。");
            if(known&&originalAutoCheck is {} savedAutoCheck)
                try{NativeEnvironment.Write(savedAutoCheck);if(NativeEnvironment.Read()!=savedAutoCheck)throw new InvalidOperationException("自动检查恢复回读不符");}
                catch(System.Exception ex){restoreErrors.Add(NativeEnvironment.Name+"："+ex.Message);}
            files.Seal();
        }
        return JsonSerializer.Serialize(new{Success=success&&known&&restoreErrors.Count==0,
            AcceptanceComplete=success&&known&&restoreErrors.Count==0&&cancellationVerified,CancellationVerified=cancellationVerified,StateKnown=known,
            Product=product,Checks=checks,RestoreErrors=restoreErrors,
            Evidence="真实CAD命令原型；普通二维、选择N和只读副本对照不替代真实Plant项目导出准确性验收；Esc以独立字段为准。"},
            new JsonSerializerOptions{WriteIndented=true});
    }

    /// <summary>命令回调内部截获异常，等待命令上下文结束后核对空闲及失败事件。</summary>
    private static async Task Native(Document doc,object[] args,Action ensure,StreamWriter log)
    {
        bool failed=false,cancelled=false,unknown=false;
        int started=0,ended=0;
        string target=Convert.ToString(args[0])!.TrimStart('_','.','-');
        System.Exception? failure=null,primary=null;
        void Started(object? sender,CommandEventArgs e)
        {if(e.GlobalCommandName.TrimStart('_','.','-').Equals(target,StringComparison.OrdinalIgnoreCase))started++;}
        void Ended(object? sender,CommandEventArgs e)
        {if(e.GlobalCommandName.TrimStart('_','.','-').Equals(target,StringComparison.OrdinalIgnoreCase))ended++;}
        void Failed(object? sender,CommandEventArgs e)=>failed=true;
        void Cancelled(object? sender,CommandEventArgs e)=>cancelled=true;
        void Unknown(object? sender,EventArgs e)=>unknown=true;
        doc.CommandWillStart+=Started;doc.CommandEnded+=Ended;
        doc.CommandFailed+=Failed;doc.CommandCancelled+=Cancelled;doc.UnknownCommand+=Unknown;
        try
        {
            ensure();
            if(NativeEnvironment.Read()!=0)throw new InvalidOperationException("原生命令前自动检查未关闭。");
            log.WriteLine("CommandAsync开始："+Convert.ToString(args[0]));
            await AcadApp.DocumentManager.ExecuteInCommandContextAsync(async _=>
            {
                try{ensure();log.WriteLine("已进入命令上下文："+target);await doc.Editor.CommandAsync(args);log.WriteLine("CommandAsync已返回："+target);ensure();}
                catch(System.Exception ex){failure=ex;}
            },null);
            log.WriteLine("命令上下文已返回："+target+"；开始核对空闲。");
            ensure();await Idle(doc,ensure);
            if(failure!=null||failed||cancelled||unknown)
                throw new CadOperationException("原生命令失败；cancel="+cancelled+"；unknown="+unknown,true,true,cancelled,failure);
            if(started!=1||ended!=1)throw new CadOperationException($"原生命令次数不符：start={started},end={ended}",true);
            if(NativeEnvironment.Read()!=0)throw new InvalidOperationException("原生命令后自动检查未保持关闭。");
            log.WriteLine("CommandAsync完成："+Convert.ToString(args[0])+"；开始/结束="+started+"/"+ended);
        }
        catch(System.Exception ex){primary=ex;throw;}
        finally
        {
            var errors=new List<System.Exception>();
            try{doc.CommandWillStart-=Started;}catch(System.Exception ex){errors.Add(ex);}
            try{doc.CommandEnded-=Ended;}catch(System.Exception ex){errors.Add(ex);}
            try{doc.CommandFailed-=Failed;}catch(System.Exception ex){errors.Add(ex);}
            try{doc.CommandCancelled-=Cancelled;}catch(System.Exception ex){errors.Add(ex);}
            try{doc.UnknownCommand-=Unknown;}catch(System.Exception ex){errors.Add(ex);}
            if(errors.Count!=0)throw new CadOperationException("原型命令监听清理失败。",inner:
                new AggregateException(primary==null?errors:new[]{primary}.Concat(errors)));
        }
    }

    /// <summary>原生调用完成后在Idle检查上下文，最多等待十秒，不把排队当完成。</summary>
    private static Task Idle(Document doc,Action ensure)
    {
        ensure();
        if(doc.Editor.IsQuiescent)return Task.CompletedTask;
        var completion=new TaskCompletionSource<bool>();var watch=System.Diagnostics.Stopwatch.StartNew();
        var timer=new System.Windows.Threading.DispatcherTimer{Interval=TimeSpan.FromMilliseconds(100)};
        timer.Tick+=Check;
        void End(System.Exception? error)
        {
            timer.Stop();timer.Tick-=Check;
            try{AcadApp.Idle-=Check;}
            catch(System.Exception cleanup){error=error==null?cleanup:new AggregateException(error,cleanup);}
            if(error==null)completion.TrySetResult(true);else completion.TrySetException(error);
        }
        void Check(object? sender,EventArgs e)
        {
            try
            {
                ensure();
                if(doc.Editor.IsQuiescent)End(null);
                else if(watch.Elapsed>TimeSpan.FromSeconds(10))End(new InvalidOperationException("原生命令结束后仍未空闲。"));
            }
            catch(System.Exception ex){End(ex);}
        }
        AcadApp.Idle+=Check;timer.Start();return completion.Task;
    }

    /// <summary>只读回新旧输出中的本原型已知二维实体，忽略身份；未知类型不宣称等价。</summary>
    private static string[] Snapshot(string path)
    {
        using var database=new Database(false,true);
        database.ReadDwgFile(path,FileOpenMode.OpenForReadAndAllShare,true,"");
        using var transaction=database.TransactionManager.StartOpenCloseTransaction();
        var table=(BlockTable)transaction.GetObject(database.BlockTableId,OpenMode.ForRead);
        var model=(BlockTableRecord)transaction.GetObject(table[BlockTableRecord.ModelSpace],OpenMode.ForRead);
        var rows=new List<string>();
        string Point(Point3d p)=>p.X.ToString("R",CultureInfo.InvariantCulture)+"/"+p.Y.ToString("R",CultureInfo.InvariantCulture)+"/"+p.Z.ToString("R",CultureInfo.InvariantCulture);
        foreach(ObjectId id in model)
        {
            var entity=(Entity)transaction.GetObject(id,OpenMode.ForRead);
            string value=entity switch
            {
                Line line=>"Line:"+Point(line.StartPoint)+":"+Point(line.EndPoint),
                Circle circle=>"Circle:"+Point(circle.Center)+":"+circle.Radius.ToString("R",CultureInfo.InvariantCulture),
                DBText text=>"Text:"+text.TextString+":"+Point(text.Position)+":"+text.Height.ToString("R",CultureInfo.InvariantCulture),
                _=>throw new InvalidOperationException("原型输出含未对照类型："+entity.GetType().FullName)
            };
            rows.Add(value+":"+entity.ColorIndex+":"+(int)entity.LineWeight+":"+entity.Linetype);
        }
        return rows.OrderBy(x=>x,StringComparer.Ordinal).ToArray();
    }
}
