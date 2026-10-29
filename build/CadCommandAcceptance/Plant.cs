using System.Collections;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows.Threading;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.ProcessPower.DataObjects;
using Autodesk.ProcessPower.PlantInstance;
using Autodesk.ProcessPower.PnIDGUIUtil;
using Autodesk.ProcessPower.PnIDObjects;
using Autodesk.ProcessPower.Styles;
using Pn3DKit.Models;
using Pn3DKit.Services;
using Pn3DKit.Utils;
using AcadApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;

namespace Pn3DKit.CadCommandProbe;

public static partial class Probe
{
    /// <summary>用官方AssetAdder和LineSegmentAdder建立真实对象，直接驱动生产读取、面板提交及关系逆查。</summary>
    private static async Task<string> Plant(Document document,string product,string output,Action ensure,StreamWriter log)
    {
        var checks=new List<string>();var events=new List<string>();
        bool success=false,known=true;object? controller=null;object? scope=null;
        ObjectId[] originalSelection=Selected(document);
        var files=new RunLogFiles();
        try
        {
            ensure();
            var project=PlantApplication.CurrentProject??throw new InvalidOperationException("未打开测试项目。");
            if(!project.ProjectFolderPath.Contains("maintenance-47-",StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("拒绝修改非工具测试项目。");
            var part=project.ProjectParts["PnId"];
            var links=part.DataLinksManager;
            var database=links.GetPnPDatabase();
            scope=Activator.CreateInstance(Product.GetType("Pn3DKit.Services.ExportAutoCheckScope",true)!,Hidden,null,
                new object[]{(Func<object?>)(()=>CallType(Product.GetType("Pn3DKit.Interop.PlantAutoCheckEnvironment",true)!,"Read")),
                    (Action<object>)(v=>CallType(Product.GetType("Pn3DKit.Interop.PlantAutoCheckEnvironment",true)!,"Write",v))},null)!;
            log.WriteLine(Call(scope,"Disable"));
            var ids=new List<ObjectId>();
            int pipe=-1;
            await InCommand(()=>
            {
                ensure();
                using(document.LockDocument())
                {
                    ObjectId style=StyleUtil.GetStyleIdFromName("Gate Valve Style",StyleType.eGraphical);
                    if(style.IsNull)throw new InvalidOperationException("官方测试样式Gate Valve Style不存在。");
                    for(int i=0;i<2;i++)
                    {
                        using var adder=new AssetAdder(document.Database,false);
                        var asset=new Asset();asset.SetDatabaseDefaults(document.Database);asset.StyleId=style;
                        asset.Position=new Point3d(10+i*30,60,0);asset.ScaleFactors=new Scale3d(1);
                        asset.XAxis=Vector3d.XAxis;asset.Normal=Vector3d.ZAxis;
                        int row=adder.Add(asset);
                        if(row<0)throw new InvalidOperationException("真实AssetAdder创建失败。");
                        var related=links.FindAcPpObjectIds(row);
                        ids.Add(links.MakeAcDbObjectId((related.First ?? throw new InvalidOperationException("真实资产没有图形关联。")).Value));
                    }
                    ObjectId lineStyle=StyleUtil.GetStyleIdFromName("New Primary Style",StyleType.eGraphical);
                    if(lineStyle.IsNull)throw new InvalidOperationException("官方测试样式New Primary Style不存在。");
                    using var lines=new LineSegmentAdder(document.Database,false){MergeGroups=false,MergeLines=false};
                    var line=new LineSegment{StyleID=lineStyle};
                    line.AddVertexAt(0,new Point3d(0,80,0));line.AddVertexAt(1,new Point3d(80,80,0));
                    pipe=lines.Add(line,-1);
                    if(pipe<0)throw new InvalidOperationException("真实LineSegmentAdder创建失败。");
                }
            });
            await Until(()=>document.CommandInProgress.Length==0&&document.Editor.IsQuiescent,ensure);
            ensure();checks.Add("官方SDK创建两个真实Plant阀门及管线");log.WriteLine("通过："+checks[^1]);
            int assetRow=links.FindAcPpRowId(ids[0]);
            string className=links.GetObjectClassname(assetRow);
            var table=database.Tables[className];
            const string number="Pn3DKitProbeNumber",text="Pn3DKitProbeText";
            if(!table.AllColumns.Contains(number))table.Columns.Add(new PnPColumn(number,typeof(double),0));
            if(!table.AllColumns.Contains(text))table.Columns.Add(new PnPColumn(text,typeof(string),64));
            database.AcceptSchemaChanges();
            var view=Activator.CreateInstance(Product.GetType("Pn3DKit.UI.FastPropertiesView",true)!,Hidden & ~BindingFlags.Static,null,Array.Empty<object>(),null)!;
            Call(view,"AppendCatalogProperties",(object)new string[]{number,text});
            var access=Activator.CreateInstance(Product.GetType("Pn3DKit.Interop.FastPropertyAccess",true)!,Hidden,null,new object[]{false},null)!;
            controller=Activator.CreateInstance(Product.GetType("Pn3DKit.Interop.FastSelectionController",true)!,Hidden,null,
                new object[]{(Func<bool>)(()=>true),view},null)!;
            document.Editor.SetImpliedSelection(ids.ToArray());
            void Changed(object? sender,ObjectEventArgs e)
            {
                string? key=(string?)Call(access,"RelatedDataKey",e.DBObject.ObjectId);
                events.Add(e.DBObject.GetType().FullName+":"+(key??"(unknown)"));
            }
            document.Database.ObjectModified+=Changed;
            try
            {
                async Task Submit(string property,string value,string expected)
                {
                    Call(controller,"RequestRefresh");
                    var field=controller.GetType().GetField("_tickets",Hidden)!;
                    await Until(()=>
                    {
                        Call(controller,"Idle",null,EventArgs.Empty);
                        return ((IDictionary)field.GetValue(controller)!).Count>0;
                    },ensure);
                    var specs=(IReadOnlyList<PnPropertySpec>)Call(view,"ReadSpecs")!;
                    var spec=specs.Single(s=>s.Input.EndsWith(property,StringComparison.Ordinal));
                    var tickets=(IDictionary)field.GetValue(controller)!;
                    Guid token=Guid.Empty;
                    foreach(DictionaryEntry entry in tickets)
                    {
                        var contexts=entry.Value!.GetType().GetProperty("Contexts")!.GetValue(entry.Value);
                        var first=((IEnumerable)contexts!).Cast<object>().First();
                        if((Guid)first.GetType().GetProperty("RowId")!.GetValue(first)! == spec.Id){token=(Guid)entry.Key;break;}
                    }
                    if(token==Guid.Empty)
                    {
                        using var tx=document.Database.TransactionManager.StartOpenCloseTransaction();
                        var item=(PnObjectProperties)Call(access,"Read",ids[0],document,tx,new PnPropertySpec[]{spec})!;
                        throw new InvalidOperationException("生产未授权属性："+JsonSerializer.Serialize(item));
                    }
                    string path=PnPropertyPolicy.Normalize(spec.Input,out _)!;
                    Call(controller,"QueueEdit",new PnEditRequest(token,spec.Id,spec.Revision,path,null,value));
                    await Until(()=>
                    {
                        Call(controller,"Idle",null,EventArgs.Empty);
                        return !(bool)controller.GetType().GetField("_writing",Hidden)!.GetValue(controller)!
                            && controller.GetType().GetField("_pending",Hidden)!.GetValue(controller)==null;
                    },ensure);
                    string notice=(string)controller.GetType().GetField("_notice",Hidden)!.GetValue(controller)!;
                    log.WriteLine(notice);
                    if(!notice.Contains("已更新 2",StringComparison.Ordinal)||notice.Contains("失败 1",StringComparison.Ordinal))
                        throw new InvalidOperationException("真实生产多选写入未完成："+notice);
                    using var read=document.Database.TransactionManager.StartOpenCloseTransaction();
                    foreach(var id in ids)
                    {
                        var actual=(PnObjectProperties)Call(access,"Read",id,document,read,new PnPropertySpec[]{spec})!;
                        if(actual.Cells[0].Value.Raw!=expected)throw new InvalidOperationException("真实SDK实际回读不符："+JsonSerializer.Serialize(actual));
                    }
                    if(!Selected(document).ToHashSet().SetEquals(ids))throw new InvalidOperationException("生产多选写入改变了选择。");
                    checks.Add("真实生产多选提交及选择保持："+property+"="+value+"→"+expected);
                    log.WriteLine("通过："+checks[^1]);
                }
                await Submit(number,"01","1");
                await Submit(text,"Plant44-精确文本","Plant44-精确文本");
            }
            finally { document.Database.ObjectModified-=Changed; }
            if(events.Count==0)throw new InvalidOperationException("未取得真实数据库事件，事件验收不完整。");
            checks.Add("真实SDK写入事件由生产控制器复核");log.WriteLine("通过："+checks[^1]);
            using(var report=new StreamWriter(new FileStream(Path.Combine(output,"plant-events.json"),FileMode.CreateNew)))
                report.Write(JsonSerializer.Serialize(events,new JsonSerializerOptions{WriteIndented=true}));
            var reverse=links.GetRelatedRowIds("PipeLineGroupRelationship","PipeLine",pipe,"PipeLineGroup").Cast<int>().Distinct().Order().ToArray();
            var baseline=database.Tables["PipeLineGroup"].Select().Select(row=>Convert.ToInt32(row["PnPID"]))
                .Where(row=>links.GetRelatedRowIds("PipeLineGroupRelationship","PipeLineGroup",row,"PipeLine").Cast<int>().Contains(pipe)).Order().ToArray();
            if(reverse.Length==0||!reverse.SequenceEqual(baseline))throw new InvalidOperationException("真实管线组逆查与全量基线不同。");
            using(document.LockDocument())
            {
                var scan=CallType(Product.GetType("Pn3DKit.Interop.PlantTagCatalogAccess",true)!,"Read",document.Database,ensure)!;
                var groups=(IEnumerable)scan.GetType().GetProperty("Groups",Hidden)!.GetValue(scan)!;
                bool found=groups.Cast<object>().SelectMany(group=>((IEnumerable)group.GetType().GetProperty("Targets",Hidden)!.GetValue(group)!).Cast<object>())
                    .Any(target=>
                    {
                        var context=target.GetType().GetProperty("Context",Hidden)!.GetValue(target)!;
                        return reverse.Contains((int)context.GetType().GetProperty("RowId",Hidden)!.GetValue(context)!);
                    });
                if(!found)throw new InvalidOperationException("生产本图扫描未包含逆查管线组。");
            }
            checks.Add("真实DataLinks管线组逆查及生产本图扫描");log.WriteLine("通过："+checks[^1]);
            ((IDisposable)controller).Dispose();controller=null;
            document.Editor.SetImpliedSelection(originalSelection);
            var cad=NewHost(()=>ReferenceEquals(project,PlantApplication.CurrentProject),files);
            known=false;await cad.SaveAsync(cad.OriginalDrawing!);known=true;
            checks.Add("测试Plant图纸原生保存完成");log.WriteLine("通过："+checks[^1]);
            success=true;
        }
        catch(System.Exception ex){log.WriteLine(ex.ToString());if(ex is CadOperationException {StateKnown:true})known=true;}
        finally
        {
            try{(controller as IDisposable)?.Dispose();}catch(System.Exception ex){known=false;log.WriteLine(ex.ToString());}
            if(known)document.Editor.SetImpliedSelection(originalSelection);
            if(scope!=null)
            {
                var outcome=Call(scope,"Finish",known?files.ProtectionReason:"Plant验收原生状态未知")!;
                log.WriteLine(JsonSerializer.Serialize(outcome));
                if((bool)outcome.GetType().GetProperty("HasIssue")!.GetValue(outcome)!)success=false;
            }
        }
        return JsonSerializer.Serialize(new{Success=success&&known,AcceptanceComplete=success&&known,
            Kind="真实Plant属性写入、事件、选择及逆查验收",Product=product,Checks=checks,Events=events.Count},
            new JsonSerializerOptions{WriteIndented=true});
    }

    /// <summary>所有异常收在CAD命令回调内；真实SDK创建仅在受控副本执行。</summary>
    private static async Task InCommand(Action action)
    {
        System.Exception? failure=null;
        await AcadApp.DocumentManager.ExecuteInCommandContextAsync(_=>
        {
            try{action();}catch(System.Exception ex){failure=ex;}return Task.CompletedTask;
        },null);
        if(failure!=null)System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }

    /// <summary>主线程定时器等待已知条件，不阻塞CAD消息循环，不进入线程池访问SDK。</summary>
    internal static Task Until(Func<bool> ready,Action ensure)
    {
        var completion=new TaskCompletionSource<bool>();
        var watch=System.Diagnostics.Stopwatch.StartNew();
        var timer=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(100)};
        void Poll(object? sender,EventArgs args)
        {
            try
            {
                ensure();
                if(ready()){timer.Stop();timer.Tick-=Poll;completion.TrySetResult(true);}
                else if(watch.Elapsed>TimeSpan.FromSeconds(15))throw new TimeoutException("主线程验收检查点等待超时。");
            }
            catch(System.Exception ex){timer.Stop();timer.Tick-=Poll;completion.TrySetException(ex);}
        }
        timer.Tick+=Poll;timer.Start();return completion.Task;
    }
    internal static ObjectId[] Selected(Document document)
    {
        var result=document.Editor.SelectImplied();using var selection=result.Value;
        return result.Status==Autodesk.AutoCAD.EditorInput.PromptStatus.OK&&selection!=null?selection.GetObjectIds():Array.Empty<ObjectId>();
    }
}
