using System.IO;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Pn3DKit.Models;
using Pn3DKit.Services;
using Pn3DKit.Utils;
using AcadApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;

namespace Pn3DKit.CadCommandProbe;

/// <summary>直接验收0.1.47生产适配器，复用已加载的受控Session宿主。</summary>
public static partial class Probe
{
    internal const BindingFlags Hidden = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
    internal static readonly Assembly Product = typeof(ExportPolicy).Assembly;

    /// <summary>只处理配置登记的工具副本；失败时保存完整证据，不操作用户原图。</summary>
    public static async Task<string> RunAsync(Document document, string product, string output, Action ensure)
    {
        using var log = new StreamWriter(new FileStream(Path.Combine(output,"acceptance.log"), FileMode.CreateNew, FileAccess.Write, FileShare.Read)) { AutoFlush = true };
        var checks = new List<string>();
        bool known = true, success = false;
        object? scope = null;
        var files = new RunLogFiles();
        void Passed(string name) { ensure(); checks.Add(name); log.WriteLine("通过：" + name); }
        try
        {
            ensure();
            using var config = JsonDocument.Parse(File.ReadAllText(Path.Combine(Path.GetDirectoryName(output)!, "native-command.json")));
            string phase = config.RootElement.TryGetProperty("AcceptancePhase", out var p) ? p.GetString()! : "Commands";
            if (phase is "Lifecycle" or "SaveLifecycle" or "DocumentLifecycle" or "ActivationLifecycle"
                or "DocumentLifecycleAppOpen" or "DocumentLifecycleInactive"
                or "DocumentLifecycleBoundary" or "DocumentLifecycleRedraw")
                return await Lifecycle(document, product, output, ensure, log, phase);
            if (phase == "Plant")
                return await Plant(document, product, output, ensure, log);
            if (phase == "Folder")
                return Folder(document, product, output, ensure, log);
            if (phase == "Paths")
                return await PathDiagnostics(document, product, output, ensure, log);
            var environment = Product.GetType("Pn3DKit.Interop.PlantAutoCheckEnvironment", true)!;
            object Read() => CallType(environment, "Read")!;
            void Write(object value) => CallType(environment, "Write", value);
            var scopeType = Product.GetType("Pn3DKit.Services.ExportAutoCheckScope", true)!;
            scope = Activator.CreateInstance(scopeType, Hidden, null, new object[] {(Func<object?>)Read, (Action<object>)Write}, null)!;
            log.WriteLine(Call(scope, "Disable"));
            if (Convert.ToInt32(Read()) != 0) throw new InvalidOperationException("生产环境配置未关闭。");
            Passed("真实生产自动检查入口关闭并回读");

            var plantType = Type.GetType("Autodesk.ProcessPower.PlantInstance.PlantApplication, PnPProjectManagerMgd", true)!;
            var projectProperty = plantType.GetProperty("CurrentProject")!;
            object project = projectProperty.GetValue(null) ?? throw new InvalidOperationException("工具测试项目未打开。");
            var host = NewHost(() => ReferenceEquals(project, projectProperty.GetValue(null)), files);
            string BatchDirectory(string label) => Path.Combine(Path.GetDirectoryName(output)!, label + "-" + Guid.NewGuid().ToString("N")[..8]);
            var drawing = host.OriginalDrawing!;
            var before = await host.ReadStateAsync(drawing);
            if (before.ReadOnly) throw new InvalidOperationException("工具副本只读，停止验收。");
            Passed("生产短锁状态读取返回");
            Dirty(document);
            int saves = 0, exports = 0, checkerErrors = 0;
            var commandWatch = System.Diagnostics.Stopwatch.StartNew();
            var watchdog = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            void Guard(object? sender, EventArgs e)
            {
                try
                {
                    ensure();
                    string command = document.CommandInProgress;
                    if (commandWatch.Elapsed > TimeSpan.FromSeconds(10)
                        && (command.Contains("QSAVE", StringComparison.OrdinalIgnoreCase)
                            || command.Contains("EXPORTTOAUTOCAD", StringComparison.OrdinalIgnoreCase)))
                    {
                        log.WriteLine("SDK自动取消未结束的专用验收命令：" + command);
                        document.SendStringToExecute("\u0003\u0003", true, false, false);
                        commandWatch.Restart();
                    }
                }
                catch (System.Exception ex) { log.WriteLine("验收看门狗：" + ex.Message); }
            }
            void Started(object? sender, CommandEventArgs e)
            {
                commandWatch.Restart();
                if (Convert.ToInt32(Read()) != 0) checkerErrors++;
                string name = e.GlobalCommandName.TrimStart('_', '.', '-');
                if (name.Equals("QSAVE", StringComparison.OrdinalIgnoreCase)) saves++;
                if (name.Equals("EXPORTTOAUTOCAD", StringComparison.OrdinalIgnoreCase)) exports++;
            }
            document.CommandWillStart += Started;
            watchdog.Tick += Guard; watchdog.Start();
            try
            {
                known = false;
                var result = await new BatchExporter().RunAsync(new(Path.GetDirectoryName(document.Database.Filename)!,new[]{document.Database.Filename},null),
                    new(ProjectPartKind.PnId,true,"2018",BatchDirectory("batch")),host,
                    message => log.WriteLine(message), files: files);
                known = files.ProtectionReason == null;
                if (!known || result.StopReason != null || result.Items.Count != 1 || result.Items[0].Status != "Exported"
                    || saves != 1 || exports != 1 || checkerErrors != 0 || (await host.ReadStateAsync(drawing)).Modified)
                    throw new InvalidOperationException("生产保存导出生命周期不符：" + JsonSerializer.Serialize(result));
                ExportPolicy.ValidateOutput(result.Items[0].Destination,"2018");
                Passed("生产BatchExporter保存一次、原生导出、格式和DBMOD检查");
                string baseline = config.RootElement.GetProperty("BaselineExport").GetString()!;
                if (!Snapshot(baseline).SequenceEqual(Snapshot(result.Items[0].Destination)))
                    throw new InvalidOperationException("生产输出与已确认旧LISP二维基线不同。");
                Passed("生产导出与旧LISP普通二维内容对照");
                saves = exports = 0;
                Dirty(document);
                var skipped = await new BatchExporter().RunAsync(new(Path.GetDirectoryName(document.Database.Filename)!,new[]{document.Database.Filename},null),
                    new(ProjectPartKind.PnId,false,"2018",BatchDirectory("skip-n")),host, message=>log.WriteLine(message),files:new RunLogFiles());
                if(skipped.StopReason!=null||skipped.Items[0].Status!="Skipped"||saves!=0||exports!=0||!(await host.ReadStateAsync(drawing)).Modified)
                    throw new InvalidOperationException("生产选择N分支错误。");
                Passed("生产选择N保持修改且不保存不导出");
                known=false; await host.SaveAsync(drawing); known=true;
            }
            finally { watchdog.Stop(); watchdog.Tick -= Guard; document.CommandWillStart -= Started; }

            // 直接调用生产私有命令入口，未知命令必须结束并报告已知失败。
            bool rejected = false;
            try
            {
                known = false;
                await (Task)Call(host, "RunCommandAsync", drawing, new object[] { "_.PN3DKIT_UNKNOWN_ACCEPT47" })!;
                known = true;
            }
            catch (CadOperationException ex) when (ex.StateKnown) { known = true; rejected = true; }
            if (!rejected) throw new InvalidOperationException("生产未知命令未拒绝。");
            Passed("生产未知命令返回已知失败并清理监听");

            string openPath = Path.Combine(output, "OpenCloseFixture.dwg");
            File.Copy(document.Database.Filename, openPath, false);
            known=false; var opened = await host.OpenAsync(openPath);
            await host.ActivateAsync(opened);
            var openedState = await host.ReadStateAsync(opened);
            if(openedState.Modified)throw new InvalidOperationException("新打开测试副本意外修改，禁止关闭。");
            await host.CloseAsync(opened); await host.ActivateAsync(drawing); known=true;
            Passed("生产打开、短锁状态检查、关闭及原图恢复");

            Document? readOnly = null;
            string readOnlyPath = Path.Combine(output, "ReadOnlyFixture.dwg");
            File.Copy(document.Database.Filename, readOnlyPath, false);
            try
            {
                known = false;
                readOnly = AcadApp.DocumentManager.Open(readOnlyPath, true);
                AcadApp.DocumentManager.MdiActiveDocument = readOnly;
                await InCommand(() => { });
                var readIdentity = readOnly.Database.BlockTableId;
                void ReadEnsure()
                {
                    if(readOnly.Database.IsDisposed||AcadApp.DocumentManager.MdiActiveDocument?.Database.BlockTableId!=readIdentity)
                        throw new InvalidOperationException("只读测试身份变化。");
                }
                var readFiles = new RunLogFiles();
                var readHost = NewHost(()=>{ReadEnsure();return true;},readFiles);
                Dirty(readOnly);
                var skipped = await new BatchExporter().RunAsync(new(Path.GetDirectoryName(readOnlyPath)!,new[]{readOnlyPath},null),
                    new(ProjectPartKind.PnId,true,"2018",BatchDirectory("read-only")),readHost,message=>log.WriteLine(message),files:readFiles);
                known=readFiles.ProtectionReason==null;
                if(!known||skipped.StopReason!=null||skipped.Items[0].Status!="Failed"
                    ||!skipped.Items[0].Message.Contains("只读",StringComparison.Ordinal))
                    throw new InvalidOperationException("生产只读修改保护错误。");
                log.WriteLine("通过：生产只读修改图纸拒绝保存与导出"); checks.Add("生产只读修改图纸拒绝保存与导出");
            }
            finally
            {
                if(known){AcadApp.DocumentManager.MdiActiveDocument=document;readOnly?.CloseAndDiscard();}
            }
            ensure();
            if(config.RootElement.TryGetProperty("RunCancellation",out var cancel)&&cancel.GetBoolean())
            {
                using(var waiting=new StreamWriter(new FileStream(Path.Combine(output,"awaiting-esc.txt"),FileMode.CreateNew)))
                    waiting.Write("生产适配器取消验收：请在LINE选点时按Esc。");
                document.Editor.WriteMessage("\n生产适配器取消验收：请在接下来的LINE选点时按Esc。");
                bool cancelled=false;
                try
                {
                    known=false;
                    await (Task)Call(host,"RunCommandAsync",drawing,new object[]{"_.LINE",Autodesk.AutoCAD.EditorInput.Editor.PauseToken,""})!;
                    known=true;
                }
                catch(CadOperationException ex) when(ex.StateKnown&&ex.Canceled){known=true;cancelled=true;}
                if(!cancelled)throw new InvalidOperationException("生产取消未获得实际Esc证据。");
                Passed("生产实际Esc取消、命令返回和监听清理");
            }
            if(files.ProtectionReason!=null)throw new InvalidOperationException(files.ProtectionReason);
            known=false;
            await Until(() => AcadApp.IsQuiescent && document.Editor.IsQuiescent
                && document.CommandInProgress.Length==0 && Convert.ToInt32(AcadApp.GetSystemVariable("CMDACTIVE"))==0,ensure);
            known=true;
            Passed("生产命令与全部测试文档退出后全局空闲");
            success=true;
        }
        catch(System.Exception ex)
        {
            if(ex is CadOperationException {StateKnown:true})known=true;
            log.WriteLine(ex.ToString());
        }
        finally
        {
            if(scope!=null)
            {
                var outcome=Call(scope,"Finish",known?files.ProtectionReason:"生产原生状态未知")!;
                log.WriteLine(JsonSerializer.Serialize(outcome));
                if((bool)outcome.GetType().GetProperty("HasIssue")!.GetValue(outcome)!)success=false;
            }
            files.Seal();
        }
        return JsonSerializer.Serialize(new{Success=success&&known,AcceptanceComplete=success&&known,
            Kind="0.1.47生产命令适配器验收",Product=product,Checks=checks,StateKnown=known,Protection=files.ProtectionReason},
            new JsonSerializerOptions{WriteIndented=true});
    }

    /// <summary>内部类型反射只用于验证实际产品实现，不进入产品命令或载荷。</summary>
    internal static object? Call(object instance,string method,params object?[] args)
    {
        try{return instance.GetType().GetMethod(method,Hidden)!.Invoke(instance,args);}
        catch(TargetInvocationException ex){ExceptionDispatchInfo.Capture(ex.InnerException??ex).Throw();throw;}
    }
    internal static object? CallType(Type type,string method,params object?[] args)
    {
        try{return type.GetMethod(method,Hidden)!.Invoke(null,args);}
        catch(TargetInvocationException ex){ExceptionDispatchInfo.Capture(ex.InnerException??ex).Throw();throw;}
    }
    internal static ICadHost NewHost(Func<bool> current,RunLogFiles files) =>
        (ICadHost)Activator.CreateInstance(Product.GetType("Pn3DKit.Interop.AutoCadHost",true)!,Hidden,null,new object[]{current,files},null)!;

    /// <summary>只在工具副本内制造并擦除点，保留既有测试几何用于新旧输出对照。</summary>
    internal static void Dirty(Document document)
    {
        using(document.LockDocument())
        using(var transaction=document.Database.TransactionManager.StartTransaction())
        {
            var table=(BlockTable)transaction.GetObject(document.Database.BlockTableId,OpenMode.ForRead);
            var model=(BlockTableRecord)transaction.GetObject(table[BlockTableRecord.ModelSpace],OpenMode.ForWrite);
            var point=new DBPoint(Point3d.Origin);model.AppendEntity(point);transaction.AddNewlyCreatedDBObject(point,true);
            point.Erase();transaction.Commit();
        }
    }

    /// <summary>原型固定二维夹具的精确字段对照；不能宣称覆盖未知Plant对象。</summary>
    private static string[] Snapshot(string path)
    {
        using var db=new Database(false,true);db.ReadDwgFile(path,FileOpenMode.OpenForReadAndAllShare,true,"");
        using var tx=db.TransactionManager.StartOpenCloseTransaction();
        var table=(BlockTable)tx.GetObject(db.BlockTableId,OpenMode.ForRead);
        var model=(BlockTableRecord)tx.GetObject(table[BlockTableRecord.ModelSpace],OpenMode.ForRead);
        var rows=new List<string>();
        string Point(Point3d p)=>string.Join("/",new[]{p.X,p.Y,p.Z}.Select(v=>v.ToString("R",System.Globalization.CultureInfo.InvariantCulture)));
        foreach(ObjectId id in model)
        {
            var e=(Entity)tx.GetObject(id,OpenMode.ForRead);
            string value=e switch
            {
                Line line=>"Line:"+Point(line.StartPoint)+":"+Point(line.EndPoint),
                Circle circle=>"Circle:"+Point(circle.Center)+":"+circle.Radius.ToString("R",System.Globalization.CultureInfo.InvariantCulture),
                DBText text=>"Text:"+text.TextString+":"+Point(text.Position)+":"+text.Height.ToString("R",System.Globalization.CultureInfo.InvariantCulture),
                _=>throw new InvalidOperationException("固定二维基线含未知类型："+e.GetType().FullName)
            };
            rows.Add(value+":"+e.ColorIndex+":"+(int)e.LineWeight+":"+e.Linetype);
        }
        return rows.OrderBy(x=>x,StringComparer.Ordinal).ToArray();
    }
}
