using System.IO;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.GraphicsSystem;
using Pn3DKit.Models;
using Pn3DKit.Utils;
using Pn3DKit.Services;
using AcadApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;
using CadColor = Autodesk.AutoCAD.Colors.Color;
using ColorMethod = Autodesk.AutoCAD.Colors.ColorMethod;
using GColor = System.Drawing.Color;
using LineWeight = Autodesk.AutoCAD.DatabaseServices.LineWeight;
using View = Autodesk.AutoCAD.GraphicsSystem.View;

namespace Pn3DKit.CadVisualProbe;

/// <summary>真实 CAD 验证产品准备层及离屏绘制层；独立于公开命令注册，所有临时数据均自行释放。</summary>
public static class Probe
{
    private const BindingFlags Hidden = BindingFlags.NonPublic | BindingFlags.Instance;
    /// <summary>由已授权测试入口同步调用，返回可审计 JSON；不对源图保存或写入。</summary>
    public static string Run(Document document, string productFile, string output, Action ensure)
    {
        using var log = new StreamWriter(new FileStream(Path.Combine(output,"probe.log"),FileMode.CreateNew,FileAccess.Write,FileShare.Read))
            {AutoFlush=true};
        using(var config=JsonDocument.Parse(File.ReadAllText(Path.Combine(Path.GetDirectoryName(output)!,"native-host.json"))))
        {
            if(config.RootElement.TryGetProperty("Diagnostics",out var diagnostic) && diagnostic.GetBoolean())
                return Diagnostics(document,productFile,output,ensure,log);
            if(config.RootElement.TryGetProperty("RealCatalog",out var catalog) && catalog.GetBoolean())
                return RealCatalog(document,productFile,output,ensure,log);
        }
        log.WriteLine("GS模型工厂："+string.Join(" | ",typeof(Model).GetMethods(BindingFlags.Public|BindingFlags.Static).Select(m=>m.ToString())));
        log.WriteLine("GS宿主工厂："+string.Join(" | ",typeof(Manager).GetMethods(BindingFlags.Public|BindingFlags.Instance|BindingFlags.Static)
            .Where(m=>m.Name.Contains("Create")||m.Name.Contains("DBModel")||m.Name.Contains("SetViewFrom")).Select(m=>m.ToString())));
        var assembly = typeof(VisualFrame).Assembly;
        Type preparation = assembly.GetType("Pn3DKit.Interop.XrefBlockVisualPreparation",true)!;
        Type rendering = assembly.GetType("Pn3DKit.Interop.XrefBlockVisualSession",true)!;
        object? session = null;
        var results = new List<object>();
        bool success = true;
        int captures = 0;
        object? Property(object instance,string name) => instance.GetType().GetProperty(name,Hidden)!.GetValue(instance);
        object? Call(object instance,string name, params object?[] args)
        {
            try { return instance.GetType().GetMethod(name,Hidden)!.Invoke(instance,args); }
            catch(TargetInvocationException ex) { ExceptionDispatchInfo.Capture(ex.InnerException ?? ex).Throw(); throw; }
        }
        object Prepare(Fixture fixture) => Activator.CreateInstance(preparation,Hidden,null,
            [fixture.Database,fixture.Root,Convert.ToInt32(AcadApp.GetSystemVariable("LWDEFAULT")),ensure],null)!;
        VisualSample Capture(object scene, int no, VisualFrame frame, IReadOnlyDictionary<int,uint> palette)
        {
            Call(session!,"EnsureDevice");
            byte[] light = (byte[])Call(session!,"Draw",scene,frame,GColor.White)!;
            byte[] dark = (byte[])Call(session!,"Draw",scene,frame,GColor.FromArgb(24,32,40))!;
            uint[] colors = (uint[])Property(scene,"Colors")!;
            int[] weights = (int[])Property(scene,"Weights")!;
            byte[]? code = null;
            if(weights.Length > 1) { Call(scene,"EncodeWeights",palette); code=(byte[])Call(session!,"Draw",scene,frame,GColor.Black)!; }
            captures++;
            return new(no,frame,light,dark,colors,weights,true,true,code,true,null);
        }
        void Test(string name, Func<Fixture> left, Func<Fixture> right, VisualState expected)
        {
            ensure(); document.Editor.WriteMessage("\n原生视觉验证：" + name); log.WriteLine("开始：" + name);
            using var a = left(); using var b = right();
            object? pa=null,pb=null;
            try
            {
                pa=Prepare(a); pb=Prepare(b);
                if(name is "线型差异" or "绘制顺序" or "遮罩顺序")
                {
                    void Describe(Database db,ObjectId root,string label)
                    {
                        using var tx=db.TransactionManager.StartOpenCloseTransaction();
                        var block=(BlockTableRecord)tx.GetObject(root,OpenMode.ForRead);
                        var rows=new List<string>();
                        foreach(ObjectId id in block)
                        {
                            var ent=(Entity)tx.GetObject(id,OpenMode.ForRead);
                            var lt=(LinetypeTableRecord)tx.GetObject(ent.LinetypeId,OpenMode.ForRead);
                            rows.Add(id.Handle+":"+ent.GetType().Name+":Color="+ent.ColorIndex+":Linetype="+lt.Name+"/"+lt.NumDashes+"/"+lt.PatternLength);
                        }
                        var draw=(DrawOrderTable)tx.GetObject(block.DrawOrderTableId,OpenMode.ForRead);
                        log.WriteLine(label+" entities="+string.Join("|",rows)+" order="+string.Join("/",draw.GetFullDrawOrder(0).Cast<ObjectId>().Select(id=>id.Handle)));
                    }
                    Describe(a.Database,a.Root,"原始A");Describe(b.Database,b.Root,"原始B");
                    using(var clone=new Database(true,true))
                    using(var map=new IdMapping())
                    {
                        b.Database.WblockCloneObjects(new ObjectIdCollection([b.Root]),clone.BlockTableId,map,DuplicateRecordCloning.Replace,false);
                        log.WriteLine("完整映射："+string.Join("|",map.Cast<IdPair>().Select(pair=>pair.Key.Handle+">"+pair.Value.Handle+":"+pair.IsCloned+":"+pair.Key.ObjectClass.Name)));
                    }
                    foreach(var (scene,label) in new[]{(pa,"副本A"),(pb,"副本B")})
                    {
                        var db=(Database)Property(scene,"Database")!;
                        using var tx=db.TransactionManager.StartOpenCloseTransaction();
                        var br=(BlockReference)tx.GetObject((ObjectId)Property(scene,"Reference")!,OpenMode.ForRead);
                        Describe(db,br.BlockTableRecord,label);
                        var table=(BlockTable)tx.GetObject(db.BlockTableId,OpenMode.ForRead);
                        if(table.Has("NativeTest"))Describe(db,table["NativeTest"],label+"原始克隆");
                    }
                }
                string? problemA=(string?)Property(pa,"Problem"), problemB=(string?)Property(pb,"Problem");
                VisualPairResult result;
                bool? samePixels=null, codeDifferent=null;
                if(problemA != null || problemB != null) result=new(VisualState.Unverified,problemA ?? problemB!);
                else
                {
                    var bounds = new List<(double,double,double,double)>();
                    foreach(var scene in new[]{pa,pb})
                        if(Property(scene,"Bounds") is ValueTuple<double,double,double,double> box) bounds.Add(box);
                    var frame=BlockVisualComparison.Frame(bounds);
                    int[] weights=((int[])Property(pa,"Weights")!).Concat((int[])Property(pb,"Weights")!).Distinct().Order().ToArray();
                    var palette=weights.Select((w,i)=>(w,color:(uint)((i+1)*0x9E3779)&0xFFFFFF)).ToDictionary(x=>x.w,x=>x.color);
                    var sa=Capture(pa,1,frame,palette); var sb=Capture(pb,2,frame,palette);
                    result=BlockVisualComparison.Compare(sa,sb);
                    samePixels=sa.LightPixels.AsSpan().SequenceEqual(sb.LightPixels) && sa.DarkPixels.AsSpan().SequenceEqual(sb.DarkPixels);
                    codeDifferent=sa.WeightCodePixels != null && sb.WeightCodePixels != null
                        && !sa.WeightCodePixels.AsSpan().SequenceEqual(sb.WeightCodePixels);
                    if(name is "重复稳定" or "文字内容" or "仅颜色" or "仅线宽" or "线型差异" or "绘制顺序" or "遮罩顺序" or "填充")
                    { Save(sa.LightPixels,frame.Pixels,Path.Combine(output,name+"-A.png")); Save(sb.LightPixels,frame.Pixels,Path.Combine(output,name+"-B.png")); }
                }
                bool passed=result.State==expected; success &= passed;
                results.Add(new {Name=name,Passed=passed,Expected=expected.ToString(),Actual=result.State.ToString(),result.Reason,
                    SameRaster=samePixels,WeightCodeDifferent=codeDifferent});
                log.WriteLine(name+"："+result.State+"；预期="+expected+"；"+result.Reason+"；同图="+samePixels+"；编码差异="+codeDifferent);
            }
            catch(System.Exception ex)
            {
                while(ex is TargetInvocationException {InnerException:{} inner})ex=inner;
                success=false; log.WriteLine(name+"："+ex);
                results.Add(new {Name=name,Passed=false,Error=ex.ToString()});
                if(ex.GetType().Name=="XrefVisualFatalException")
                {
                    if(session != null)
                        log.WriteLine("设备状态："+((Device?)rendering.GetField("_device",Hidden)!.GetValue(session))?.IsValid
                            +"；视图状态："+((View?)rendering.GetField("_view",Hidden)!.GetValue(session))?.IsValid);
                    throw;
                }
            }
            finally { ((IDisposable?)pb)?.Dispose(); ((IDisposable?)pa)?.Dispose(); }
        }
        try
        {
            // 这里只直接调用产品的准备与绘制方法；真实来源目录流程另行验收，不使用 SDK 替身。
            session=Activator.CreateInstance(rendering,Hidden,null,[document,null,ensure,null],null)!;
            Test("重复稳定",()=>Circle(),()=>Circle(),VisualState.Matched);
            Test("仅颜色",()=>Circle(),()=>Circle(aci:5),VisualState.Mismatched);
            Test("仅线宽",()=>Circle(),()=>Circle(weight:30),VisualState.Mismatched);
            Test("公共视窗尺寸",()=>Circle(),()=>Circle(radius:12),VisualState.Mismatched);
            Test("实体表示无关",()=>Linework(false),()=>Linework(true),VisualState.Matched);
            Test("句柄创建顺序无关",()=>Linework(false),()=>Linework(false,true),VisualState.Matched);
            Test("图层名无关",()=>LayerLine("LayerA",false),()=>LayerLine("LayerB",false),VisualState.Matched);
            Test("ByLayer有效值",()=>LayerLine("LayerA",false),()=>LayerLine("LayerB",true),VisualState.Matched);
            Test("ByBlock有效值",()=>Nested(true),()=>Nested(false),VisualState.Matched);
            Test("线宽交换位置",()=>Weights(false),()=>Weights(true),VisualState.Mismatched);
            Test("文字重复稳定",()=>Text("VALVE"),()=>Text("VALVE"),VisualState.Matched);
            Test("文字内容",()=>Text("VALVE"),()=>Text("PUMP"),VisualState.Mismatched);
            Test("属性默认值",()=>Attribute("V-1"),()=>Attribute("V-2"),VisualState.Mismatched);
            Test("填充",()=>Fill(1),()=>Fill(5),VisualState.Mismatched);
            Test("普通嵌套",()=>Nested(false),()=>Nested(false,5),VisualState.Mismatched);
            Test("透明度",()=>Circle(alpha:255),()=>Circle(alpha:128),VisualState.Mismatched);
            Test("顶层实例无关",()=>Linework(false),()=>Linework(false,false,true),VisualState.Matched);
            Test("白色双背景",()=>Circle(aci:7),()=>Circle(aci:7),VisualState.Matched);
            Test("基点影响",()=>BasePoint(0),()=>BasePoint(2),VisualState.Mismatched);
            Test("线型差异",()=>Dashed(false),()=>Dashed(true),VisualState.Unverified);
            Test("绘制顺序",()=>Overlap(false),()=>Overlap(true),VisualState.Unverified);
            Test("遮罩顺序",()=>Mask(false),()=>Mask(true),VisualState.Unverified);
            Test("普通多行文字",()=>MultiText("VALVE"),()=>MultiText("PUMP"),VisualState.Mismatched);
            Test("内嵌文字颜色未验证",()=>MultiText(@"\C1;VALVE"),()=>MultiText("VALVE"),VisualState.Unverified);
            Test("嵌套属性值",()=>NestedAttribute("V-1"),()=>NestedAttribute("V-2"),VisualState.Mismatched);
            Test("标注",()=>Dim(10),()=>Dim(12),VisualState.Mismatched);
            Test("真实空块",()=>Build((_,_,_)=>{}),()=>Build((_,_,_)=>{}),VisualState.Matched);
            Test("缺少字体未验证",()=>Text("VALVE",true),()=>Text("VALVE"),VisualState.Unverified);
            Test("三维未验证",()=>Build((db,tx,root)=>Add(db,tx,root,new Solid3d())),()=>Circle(),VisualState.Unverified);
            using(var a=Circle())using(var b=Circle(aci:5))using(var c=Circle())
            using(var pa=(IDisposable)Prepare(a))using(var pb=(IDisposable)Prepare(b))using(var pc=(IDisposable)Prepare(c))
            {
                var scenes=new object[]{pa,pb,pc};
                var frame=BlockVisualComparison.Frame(scenes.Select(s=>((double,double,double,double))Property(s,"Bounds")!));
                var entries=Enumerable.Range(1,3).Select(n=>new XrefBlockNameEntry("Valve",
                    new("native"+n,new[]{"A.dwg","a1.dwg","a2.dwg"}[n-1],"原生内存验证",n==1,[],DwgNo:n),false)).ToArray();
                var deliveries=scenes.Select((s,i)=>new XrefVisualCapture(entries[i],Capture(s,i+1,frame,new Dictionary<int,uint>()))).ToArray();
                var files=new RunLogFiles();string report=Path.Combine(output,"synthetic-reports");
                using(var journal=new XrefBlockJournal(report,"A.dwg",files))
                {
                    var result=XrefBlockVisualBatch.Group(new("Valve",entries),deliveries,()=>true,ensure,journal.AppendVisualDifference);
                    journal.AppendComparison(result);
                    bool passed=result.ResultText=="不匹配"&&result.Groups.Count==2
                        &&result.Groups[0].Entries.Select(e=>e.Source.DwgNo).SequenceEqual(new[]{1,3});
                    success&=passed;results.Add(new{Name="原生A-a1-a2归组及PNG",Passed=passed,Result=result.ResultText});
                }
                bool png=Directory.EnumerateFiles(report,"VisualDiff__*.png").Count()==1;
                success&=png;log.WriteLine("真实光栅归组及受控PNG="+png);
            }
            foreach(int stride in new[]{12,-12})
            {
                IntPtr memory=Marshal.AllocHGlobal(24);
                try
                {
                    using var bitmap=new System.Drawing.Bitmap(3,2,stride,System.Drawing.Imaging.PixelFormat.Format32bppArgb,
                        stride<0?IntPtr.Add(memory,12):memory);
                    for(int y=0;y<2;y++)for(int x=0;x<3;x++)bitmap.SetPixel(x,y,GColor.FromArgb(255,30+x,90+y,120+x+y));
                    var pixels=(byte[])rendering.GetMethod("Pixels",BindingFlags.Static|BindingFlags.NonPublic)!.Invoke(null,[bitmap])!;
                    bool passed=true;
                    for(int y=0;y<2;y++)for(int x=0;x<3;x++)
                    {
                        var color=bitmap.GetPixel(x,y);int index=(y*3+x)*4;
                        passed&=pixels[index]==color.B&&pixels[index+1]==color.G&&pixels[index+2]==color.R&&pixels[index+3]==color.A;
                    }
                    success&=passed;results.Add(new{Name="原生BGRA行向"+stride,Passed=passed});
                }
                finally{Marshal.FreeHGlobal(memory);}
            }
        }
        catch(System.Exception ex) { success=false;log.WriteLine("停止："+ex); }
        finally
        {
            try { ((IDisposable?)session)?.Dispose(); }
            catch(System.Exception ex) { success=false; log.WriteLine("释放："+ex); results.Add(new {ReleaseError=ex.ToString()}); }
            ensure();
        }
        return JsonSerializer.Serialize(new {Success=success,Tests=results,Captures=captures,
            ProductAssembly=productFile,ProductSHA256=Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(productFile))).ToLowerInvariant(),
            Evidence="真实CAD中的产品准备／绘制方法；不是快照或SDK替身；尚未覆盖完整命令及DWG比较人工对照"},
            new JsonSerializerOptions {WriteIndented=true});
    }


    /// <summary>在已授权真实图纸运行正式来源、统计、视觉批次与报告路径；不进入目录选择和保存接口。</summary>
    private static string RealCatalog(Document document,string productFile,string output,Action ensure,StreamWriter log)
    {
        var assembly=typeof(VisualFrame).Assembly;
        var catalogType=assembly.GetType("Pn3DKit.Interop.XrefBlockCadCatalog",true)!;
        var sessionType=assembly.GetType("Pn3DKit.Interop.XrefBlockVisualSession",true)!;
        object Call(object instance,string method,params object?[] args)
        {
            try{return instance.GetType().GetMethod(method,Hidden)!.Invoke(instance,args)!;}
            catch(TargetInvocationException ex){ExceptionDispatchInfo.Capture(ex.InnerException??ex).Throw();throw;}
        }
        var files=new RunLogFiles();
        using var journal=new XrefBlockJournal(Path.Combine(output,"reports"),document.Database.Filename,files);
        using var catalog=(IDisposable)Activator.CreateInstance(catalogType,Hidden,null,
            [document.Database,document.Database.Filename,ensure,journal],null)!;
        Call(catalog,"Discover",null,
            (Action<int,string>)((n,path)=>log.WriteLine("发现来源 "+n+" "+path)),
            (Action<int,int,string>)((n,total,path)=>{log.WriteLine("扫描文件 "+n+"/"+total+" "+path);document.Editor.WriteMessage("\n扫描文件 "+n+"/"+total+" "+Path.GetFileName(path));}));
        var inventory=(XrefBlockInventory)Call(catalog,"ReadNames");
        var statistics=XrefBlockNames.Summarize(inventory,ensure);journal.WriteStatistics(inventory,statistics);
        var candidates=XrefBlockComparison.Candidates(inventory,statistics);
        var completed=new List<XrefBlockReportResult>();
        var preparationType=assembly.GetType("Pn3DKit.Interop.XrefBlockVisualPreparation",true)!;
        foreach(var entry in candidates.SelectMany(c=>c.Entries).Take(40))
        {
            var located=((Database Database,ObjectId Definition))Call(catalog,"LocateVisual",entry);
            using(var tx=located.Database.TransactionManager.StartOpenCloseTransaction())
            {
                var block=(BlockTableRecord)tx.GetObject(located.Definition,OpenMode.ForRead);
                if(block.IsDynamicBlock)continue;
                log.WriteLine("真实准备诊断："+block.Name+"；依赖="+block.IsDependent+"；基点="+block.Origin);
            }
            try
            {
                using var scene=(IDisposable)Activator.CreateInstance(preparationType,Hidden,null,
                    [located.Database,located.Definition,Convert.ToInt32(AcadApp.GetSystemVariable("LWDEFAULT")),ensure],null)!;
            }
            catch(System.Exception ex)
            {
                while(ex is TargetInvocationException {InnerException:{} inner})ex=inner;
                log.WriteLine("真实准备堆栈："+ex);
                break;
            }
        }
        using var visual=(IDisposable)Activator.CreateInstance(sessionType,Hidden,null,
            [document,catalog,ensure,(Action<string>)(s=>log.WriteLine(s))],null)!;
        var clock=System.Diagnostics.Stopwatch.StartNew();
        XrefBlockVisualBatch.Run(candidates,c=>(IEnumerable<XrefVisualCapture>)Call(visual,"Capture",c),
            ()=> (bool)catalogType.GetProperty("ScopeComplete",Hidden)!.GetValue(catalog)!,ensure,
            r=>{journal.AppendComparison(r);log.WriteLine("块 "+r.Name+" "+r.ResultText);},completed,
            (n,total,name)=>{log.WriteLine("正在对比块 "+n+"/"+total+" "+name);document.Editor.WriteMessage("\n正在对比块 "+n+"/"+total+" "+name);},
            journal.AppendVisualDifference);
        Call(catalog,"VerifySources");
        int Number(object o,string p)=>(int)o.GetType().GetProperty(p,Hidden)!.GetValue(o)!;
        int oldReads=Number(catalog,"RootReads"),prepares=Number(catalog,"VisualPrepares"),devices=Number(visual,"DevicesCreated");
        journal.CompleteVisual(new(inventory,false,null),statistics,completed,prepares,devices);
        return JsonSerializer.Serialize(new{Success=oldReads==0&&completed.Count==candidates.Count,RealCatalog=true,
            Sources=inventory.Sources.Count,Names=inventory.Entries.Count,Candidates=candidates.Count,
            Completed=completed.Count,Results=completed.GroupBy(r=>r.ResultText).ToDictionary(g=>g.Key,g=>g.Count()),
            Problems=completed.SelectMany(r=>r.Problems).GroupBy(x=>x).Select(g=>new{Problem=g.Key,Count=g.Count()}).ToArray(),
            Prepares=prepares,Devices=devices,RasterCalls=Number(visual,"RasterCalls"),LegacySnapshots=oldReads,
            ElapsedSeconds=clock.Elapsed.TotalSeconds,ProductAssembly=productFile},new JsonSerializerOptions{WriteIndented=true});
    }

    /// <summary>只在专用设备中测量 SDK 组合，不把未完成或单张有像素的图像当作可靠比较。</summary>
    private static string Diagnostics(Document document,string productFile,string output,Action ensure,StreamWriter log)
    {
        using var fixture=Text("VALVE");
        Type preparation=typeof(VisualFrame).Assembly.GetType("Pn3DKit.Interop.XrefBlockVisualPreparation",true)!;
        using var owned=(IDisposable)Activator.CreateInstance(preparation,Hidden,null,
            [fixture.Database,fixture.Root,Convert.ToInt32(AcadApp.GetSystemVariable("LWDEFAULT")),ensure],null)!;
        var db=(Database)preparation.GetProperty("Database",Hidden)!.GetValue(owned)!;
        var reference=(ObjectId)preparation.GetProperty("Reference",Hidden)!.GetValue(owned)!;
        var rows=new List<object>();
        bool any=false;
        foreach(string requirement in new[]{"3D Drawing"})
        foreach(bool viewport in new[]{false})
        foreach(var mode in new[]{Autodesk.AutoCAD.GraphicsInterface.VisualStyleType.Wireframe2D})
        {
            ensure();
            string label=requirement.Replace(" ","")+"-"+(viewport?"Viewport":"View")+"-"+mode;
            GraphicsKernel? kernel=null;Device? device=null;View? view=null;Model? model=null;
            var previous=HostApplicationServices.WorkingDatabase;
            log.WriteLine("诊断开始："+label);document.Editor.WriteMessage("\n离屏 SDK 诊断："+label);
            try
            {
                HostApplicationServices.WorkingDatabase=db;
                using var descriptor=new KernelDescriptor();
                descriptor.addRequirement(Autodesk.AutoCAD.UniqueString.Intern(requirement));
                kernel=Manager.AcquireGraphicsKernel(descriptor);
                if(kernel==null)throw new InvalidOperationException("未提供该图形内核。");
                device=document.GraphicsManager.CreateAutoCADOffScreenDevice(kernel);
                device.OnSize(new System.Drawing.Size(2048,2048));device.BackgroundColor=GColor.White;
                using var transaction=db.TransactionManager.StartOpenCloseTransaction();
                if(viewport)
                {
                    var vtr=(ViewportTableRecord)transaction.GetObject(db.CurrentViewportTableRecordId,OpenMode.ForWrite);
                    view=document.GraphicsManager.CreateViewport(device,vtr);
                }
                else view=document.GraphicsManager.CreateView(device);
                bool attached=view.Device!=null;
                if(!attached)device.Add(view);
                view.Show();view.EnableFrontClip=false;view.EnableBackClip=false;
                using var style=new Autodesk.AutoCAD.GraphicsInterface.VisualStyle(mode);
                style.SetTrait(Autodesk.AutoCAD.GraphicsInterface.VisualStyleProperty.UseDrawOrder,true,
                    Autodesk.AutoCAD.GraphicsInterface.VisualStyleOperation.Set);
                style.SetTrait(Autodesk.AutoCAD.GraphicsInterface.VisualStyleProperty.ViewportTransparency,true,
                    Autodesk.AutoCAD.GraphicsInterface.VisualStyleOperation.Set);
                view.VisualStyle=style;
                model=document.GraphicsManager.CreateAutoCADModel(kernel);model.LinetypesEnabled=true;model.VisualStyle=style;
                view.SetView(new Point3d(0,0,30),Point3d.Origin,Vector3d.YAxis,30,30,Projection.Parallel);
                view.ViewportExtents=new Extents2d(new Point2d(0,0),new Point2d(1,1));
                view.Add((BlockReference)transaction.GetObject(reference,OpenMode.ForRead),model);
                device.Invalidate();device.Update();
                bool firstValid=device.IsValid;
                view.Update();device.Update();
                bool secondValid=device.IsValid;
                byte[] Image(bool fromDevice=false)
                {
                    using var capture=fromDevice?device.GetSnapshot(new System.Drawing.Rectangle(0,0,2048,2048))
                        :view.GetSnapshot(new System.Drawing.Rectangle(0,0,2048,2048));
                    if(capture==null)throw new InvalidOperationException("截图为空。");
                    using var normalized=new System.Drawing.Bitmap(2048,2048,System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                    using(var graphics=System.Drawing.Graphics.FromImage(normalized))
                    {graphics.CompositingMode=System.Drawing.Drawing2D.CompositingMode.SourceCopy;graphics.DrawImageUnscaled(capture,0,0);}
                    var bits=normalized.LockBits(new System.Drawing.Rectangle(0,0,2048,2048),
                        System.Drawing.Imaging.ImageLockMode.ReadOnly,System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                    var bytes=new byte[2048*2048*4];
                    try{for(int y=0;y<2048;y++)Marshal.Copy(IntPtr.Add(bits.Scan0,y*bits.Stride),bytes,y*2048*4,2048*4);}
                    finally{normalized.UnlockBits(bits);}
                    return bytes;
                }
                var first=Image();bool snapshotValid=device.IsValid;
                var devicePixels=Image(true);bool dualSame=first.AsSpan().SequenceEqual(devicePixels);
                view.BeginInteractivity(30);view.Flush();view.EndInteractivity();device.Update();
                bool interactiveValid=device.IsValid;
                var repeat=Image();
                var repeat2=Image();
                int rgbDifferences=0,alphaDifferences=0,repeatDifferences=0;
                for(int i=0;i<first.Length;i++)
                {
                    if(first[i]!=devicePixels[i]) {if(i%4==3)alphaDifferences++;else rgbDifferences++;}
                    if(first[i]!=repeat[i])repeatDifferences++;
                }
                Save(devicePixels,2048,Path.Combine(output,label+"-device.png"));
                Save(repeat,2048,Path.Combine(output,label+"-repeat.png"));
                log.WriteLine("双路RGB差异="+rgbDifferences+"；Alpha差异="+alphaDifferences+"；重复差异="+repeatDifferences
                    +"；重复两次相同="+repeat.AsSpan().SequenceEqual(repeat2));
                int ink=0;for(int i=0;i<first.Length;i+=4)if(first[i]!=255||first[i+1]!=255||first[i+2]!=255)ink++;
                bool stable=first.AsSpan().SequenceEqual(repeat);
                bool complete=device.IsValid&&ink>0&&stable;
                any|=complete;
                Save(first,2048,Path.Combine(output,label+".png"));
                rows.Add(new{Mode=label,Attached=attached,FirstValid=firstValid,SecondValid=secondValid,SnapshotValid=snapshotValid,
                    InteractiveValid=interactiveValid,FinalValid=device.IsValid,ViewValid=view.IsValid,Ink=ink,Stable=stable,Complete=complete,
                    DualSnapshotSame=dualSame,DeviceHash=Convert.ToHexString(SHA256.HashData(devicePixels)).ToLowerInvariant(),
                    Hash=Convert.ToHexString(SHA256.HashData(first)).ToLowerInvariant()});
                log.WriteLine(label+"：valid="+device.IsValid+"；view="+view.IsValid+"；ink="+ink+"；stable="+stable+"；双路一致="+dualSame);
            }
            catch(System.Exception ex){log.WriteLine(label+"："+ex);rows.Add(new{Mode=label,Error=ex.ToString()});}
            finally
            {
                try{view?.EraseAll();}finally
                {
                    try{model?.Dispose();}finally
                    {
                        try{device?.EraseAll();view?.Dispose();device?.Dispose();}
                        finally{if(kernel!=null)Manager.ReleaseGraphicsKernel(kernel);HostApplicationServices.WorkingDatabase=previous;}
                    }
                }
            }
            ensure();
        }
        return JsonSerializer.Serialize(new{Success=any,DiagnosticOnly=true,Modes=rows,ProductAssembly=productFile},
            new JsonSerializerOptions{WriteIndented=true});
    }

    /// <summary>测试数据库在内存中构造，不成为打开的文档。</summary>
    private sealed class Fixture(Database database,ObjectId root) : IDisposable
    {
        internal Database Database {get;}=database;
        internal ObjectId Root {get;}=root;
        public void Dispose()=>Database.Dispose();
    }

    /// <summary>创建可重复的二维定义，事务及 WorkingDatabase 都在返回前恢复。</summary>
    private static Fixture Build(Action<Database,Transaction,BlockTableRecord> content, bool junk=false, bool insert=false)
    {
        var db=new Database(true,true);
        var previous=HostApplicationServices.WorkingDatabase;
        try
        {
            HostApplicationServices.WorkingDatabase=db;
            using var tx=db.TransactionManager.StartTransaction();
            var table=(BlockTable)tx.GetObject(db.BlockTableId,OpenMode.ForWrite);
            if(junk)
            {
                var extra=new BlockTableRecord {Name="UNUSED_"+Guid.NewGuid().ToString("N")};
                table.Add(extra);tx.AddNewlyCreatedDBObject(extra,true);
                Add(db,tx,extra,new Circle(Point3d.Origin,Vector3d.ZAxis,32));
            }
            var root=new BlockTableRecord {Name="NativeTest"};table.Add(root);tx.AddNewlyCreatedDBObject(root,true);
            content(db,tx,root);
            if(insert)
            {
                var model=(BlockTableRecord)tx.GetObject(table[BlockTableRecord.ModelSpace],OpenMode.ForWrite);
                var instance=new BlockReference(new Point3d(100,200,0),root.ObjectId) {Rotation=1,ScaleFactors=new Scale3d(3)};
                Add(db,tx,model,instance);
            }
            tx.Commit(); return new(db,root.ObjectId);
        }
        catch {db.Dispose();throw;}
        finally {HostApplicationServices.WorkingDatabase=previous;}
    }

    /// <summary>实体默认使用明确颜色与线宽，避免默认创建环境影响预期。</summary>
    private static void Add(Database db,Transaction tx,BlockTableRecord block,Entity entity,short aci=1,int weight=25,byte alpha=255)
    {
        entity.SetDatabaseDefaults(db);entity.ColorIndex=aci;entity.LineWeight=(LineWeight)weight;
        entity.Transparency=new Autodesk.AutoCAD.Colors.Transparency(alpha);
        block.AppendEntity(entity);tx.AddNewlyCreatedDBObject(entity,true);
    }

    /// <summary>圆用于稳定性、颜色、线宽、取景及透明度验证。</summary>
    private static Fixture Circle(double radius=10,short aci=1,int weight=25,byte alpha=255)=>Build((db,tx,root)=>
        Add(db,tx,root,new Circle(Point3d.Origin,Vector3d.ZAxis,radius),aci,weight,alpha));

    /// <summary>相同折线路径可以由直线多重集或一条轻量多段线表示。</summary>
    private static Fixture Linework(bool polyline,bool reverse=false,bool insert=false)=>Build((db,tx,root)=>
    {
        if(polyline)
        {
            var line=new Polyline();line.AddVertexAt(0,new Point2d(0,0),0,0,0);
            line.AddVertexAt(1,new Point2d(10,0),0,0,0);line.AddVertexAt(2,new Point2d(10,10),0,0,0);
            Add(db,tx,root,line);
        }
        else
        {
            var a=new Line(Point3d.Origin,new Point3d(10,0,0));var b=new Line(new Point3d(10,0,0),new Point3d(10,10,0));
            if(reverse){Add(db,tx,root,b);Add(db,tx,root,a);}else{Add(db,tx,root,a);Add(db,tx,root,b);}
        }
    },reverse,insert);

    /// <summary>图层内容相同而名称不同，以及显式外观与 ByLayer 等价。</summary>
    private static Fixture LayerLine(string name,bool explicitValues)=>Build((db,tx,root)=>
    {
        var layers=(LayerTable)tx.GetObject(db.LayerTableId,OpenMode.ForWrite);
        var layer=new LayerTableRecord {Name=name,LineWeight=LineWeight.LineWeight050};
        using(var color=CadColor.FromRgb(23,140,210))layer.Color=color;
        layers.Add(layer);tx.AddNewlyCreatedDBObject(layer,true);
        var line=new Line(Point3d.Origin,new Point3d(10,0,0));Add(db,tx,root,line);
        line.LayerId=layer.ObjectId;line.LineWeight=explicitValues?LineWeight.LineWeight050:LineWeight.ByLayer;
        using var ink=explicitValues?CadColor.FromRgb(23,140,210):CadColor.FromColorIndex(ColorMethod.ByLayer,256);
        line.Color=ink;
    });

    /// <summary>嵌套引用的 ByBlock 上下文正确落实，内部变换保留。</summary>
    private static Fixture Nested(bool byBlock,short aci=1)=>Build((db,tx,root)=>
    {
        var table=(BlockTable)tx.GetObject(db.BlockTableId,OpenMode.ForWrite);
        var child=new BlockTableRecord {Name="Child"};table.Add(child);tx.AddNewlyCreatedDBObject(child,true);
        var line=new Line(Point3d.Origin,new Point3d(10,0,0));Add(db,tx,child,line,aci,50);
        if(byBlock){line.ColorIndex=0;line.LineWeight=LineWeight.ByBlock;}
        var reference=new BlockReference(new Point3d(2,3,0),child.ObjectId) {Rotation=0.25};
        Add(db,tx,root,reference,aci,50);
    });

    /// <summary>多种线宽在相同几何上的位置必须参与比较。</summary>
    private static Fixture Weights(bool swap)=>Build((db,tx,root)=>
    {
        Add(db,tx,root,new Line(new Point3d(0,0,0),new Point3d(10,0,0)),1,swap?50:25);
        Add(db,tx,root,new Line(new Point3d(0,3,0),new Point3d(10,3,0)),1,swap?25:50);
    });

    /// <summary>文字以及不可确认的原字体。</summary>
    private static Fixture Text(string text,bool missing=false)=>Build((db,tx,root)=>
    {
        var value=new DBText {Position=Point3d.Origin,TextString=text,Height=2};
        Add(db,tx,root,value);
        if(missing)
        {
            var styles=(TextStyleTable)tx.GetObject(db.TextStyleTableId,OpenMode.ForWrite);
            var style=new TextStyleTableRecord {Name="MissingFont",FileName="Pn3DKit_AbsentFont_34.shx"};
            styles.Add(style);tx.AddNewlyCreatedDBObject(style,true);value.TextStyleId=style.ObjectId;
        }
    });

    /// <summary>顶层属性使用定义默认显示值，不能采用既有插入实例值。</summary>
    private static Fixture Attribute(string text)=>Build((db,tx,root)=>
    {
        var value=new AttributeDefinition {Tag="TAG",Prompt="TAG",TextString=text,Position=Point3d.Origin,Height=2};
        Add(db,tx,root,value);
    });

    /// <summary>实体填充颜色需要在原生渲染中可见。</summary>
    private static Fixture Fill(short aci)=>Build((db,tx,root)=>
    {
        var hatch=new Hatch();Add(db,tx,root,hatch,aci);
        hatch.SetHatchPattern(HatchPatternType.PreDefined,"SOLID");
        var loop=new Polyline();loop.AddVertexAt(0,new Point2d(0,0),0,0,0);loop.AddVertexAt(1,new Point2d(10,0),0,0,0);
        loop.AddVertexAt(2,new Point2d(10,5),0,0,0);loop.AddVertexAt(3,new Point2d(0,5),0,0,0);loop.Closed=true;
        Add(db,tx,root,loop,aci);
        hatch.AppendLoop(HatchLoopTypes.External,new ObjectIdCollection([loop.ObjectId]));hatch.EvaluateHatch(false);
    });

    /// <summary>基础点改变后，统一插入仍应保留相对定义位置差异。</summary>
    private static Fixture BasePoint(double x)=>Build((db,tx,root)=>
    {root.Origin=new Point3d(x,0,0);Add(db,tx,root,new Circle(Point3d.Origin,Vector3d.ZAxis,10));});

    /// <summary>构造实际虚线资源，不把线型名称当内容。</summary>
    private static Fixture Dashed(bool dash)=>Build((db,tx,root)=>
    {
        var line=new Line(Point3d.Origin,new Point3d(10,0,0));Add(db,tx,root,line);
        if(!dash)return;
        var table=(LinetypeTable)tx.GetObject(db.LinetypeTableId,OpenMode.ForWrite);
        var type=new LinetypeTableRecord{Name="NativeDash",NumDashes=2,PatternLength=2};
        type.SetDashLengthAt(0,1);type.SetDashLengthAt(1,-1);
        table.Add(type);tx.AddNewlyCreatedDBObject(type,true);line.LinetypeId=type.ObjectId;
    });

    /// <summary>重叠填充仅交换显式显示顺序。</summary>
    private static Fixture Overlap(bool swap)=>Build((db,tx,root)=>
    {
        var a=new Solid(new Point3d(0,0,0),new Point3d(10,0,0),new Point3d(0,10,0),new Point3d(10,10,0));
        var b=new Solid(new Point3d(2,2,0),new Point3d(8,2,0),new Point3d(2,8,0),new Point3d(8,8,0));
        Add(db,tx,root,a,1);Add(db,tx,root,b,5);
        var order=(DrawOrderTable)tx.GetObject(root.DrawOrderTableId,OpenMode.ForWrite);
        order.MoveToTop(new ObjectIdCollection([swap?a.ObjectId:b.ObjectId]));
    });

    /// <summary>遮罩前后次序应改变被遮住的内容。</summary>
    private static Fixture Mask(bool behind)=>Build((db,tx,root)=>
    {
        var line=new Line(new Point3d(0,5,0),new Point3d(10,5,0));Add(db,tx,root,line);
        var mask=new Wipeout();mask.SetFrom(new Point2dCollection([new(2,2),new(8,2),new(8,8),new(2,8),new(2,2)]),Vector3d.ZAxis);
        Add(db,tx,root,mask);
        var order=(DrawOrderTable)tx.GetObject(root.DrawOrderTableId,OpenMode.ForWrite);
        order.MoveToTop(new ObjectIdCollection([behind?line.ObjectId:mask.ObjectId]));
    });

    /// <summary>普通多行文字及明确的复合颜色拒绝分支。</summary>
    private static Fixture MultiText(string content)=>Build((db,tx,root)=>
        Add(db,tx,root,new MText{Location=Point3d.Origin,TextHeight=2,Width=20,Contents=content}));

    /// <summary>嵌套属性采用已有引用值。</summary>
    private static Fixture NestedAttribute(string content)=>Build((db,tx,root)=>
    {
        var table=(BlockTable)tx.GetObject(db.BlockTableId,OpenMode.ForWrite);
        var child=new BlockTableRecord{Name="AttributeChild"};table.Add(child);tx.AddNewlyCreatedDBObject(child,true);
        var definition=new AttributeDefinition{Tag="TAG",TextString="DEFAULT",Height=2,Position=Point3d.Origin};
        Add(db,tx,child,definition);
        var reference=new BlockReference(Point3d.Origin,child.ObjectId);Add(db,tx,root,reference);
        var value=new AttributeReference();value.SetAttributeFromBlock(definition,reference.BlockTransform);value.TextString=content;
        reference.AttributeCollection.AppendAttribute(value);tx.AddNewlyCreatedDBObject(value,true);value.AdjustAlignment(db);
    });

    /// <summary>测试内存库中预先生成静态标注缓存；产品不执行该求值方法。</summary>
    private static Fixture Dim(double distance)=>Build((db,tx,root)=>
    {
        var dimension=new RotatedDimension(0,Point3d.Origin,new Point3d(distance,0,0),new Point3d(0,3,0),"",db.Dimstyle);
        Add(db,tx,root,dimension);dimension.RecomputeDimensionBlock(true);
    });

    /// <summary>仅原生验证图像作为证据，不进入产品清理协议。</summary>
    private static void Save(byte[] pixels,int size,string path)
    {
        using var bitmap=new System.Drawing.Bitmap(size,size,System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        var bits=bitmap.LockBits(new System.Drawing.Rectangle(0,0,size,size),System.Drawing.Imaging.ImageLockMode.WriteOnly,
            System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        try {for(int y=0;y<size;y++)Marshal.Copy(pixels,y*size*4,IntPtr.Add(bits.Scan0,y*bits.Stride),size*4);}
        finally {bitmap.UnlockBits(bits);}
        using var file=new FileStream(path,FileMode.CreateNew,FileAccess.Write);
        bitmap.Save(file,System.Drawing.Imaging.ImageFormat.Png);
    }
}
