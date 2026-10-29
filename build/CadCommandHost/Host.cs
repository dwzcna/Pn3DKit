using System.IO;
using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Runtime;
using AcadApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;

[assembly: CommandClass(typeof(Pn3DKit.CadCommandHost.Host))]
namespace Pn3DKit.CadCommandHost;

/// <summary>仅在工具创建的测试副本中运行Session原型；不改CAD安全设置，不进入产品载荷。</summary>
public sealed class Host
{
    private const string Root = @"D:\src\P3DBatchExport\artifacts\validation";
    private static int _running;

    /// <summary>等待原型完整结束后才释放隔离上下文；所有异常在命令入口记录。</summary>
    [CommandMethod("PNCOMMANDTEST47", CommandFlags.Session)]
    public async void Run()
    {
        if (Interlocked.Exchange(ref _running,1)!=0) return;
        var doc=AcadApp.DocumentManager.MdiActiveDocument;
        ProbeContext? context=null;
        string? output=null;
        try
        {
            string path=doc?.Database.Filename ?? "";
            string testRoot=Path.Combine(Root,@"0.1.34\261027003013");
            if(doc==null || !Path.GetFullPath(path).StartsWith(testRoot+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase)
                || !Path.GetFullPath(path).Contains(Path.DirectorySeparatorChar+"maintenance-",StringComparison.OrdinalIgnoreCase)
                || !Path.GetFileName(path).EndsWith("Fixture.dwg",StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("原型仅接受工具生成的maintenance-*测试副本。");
            var identity=doc.Database.BlockTableId;
            void Ensure()
            {
                if(doc.Database.IsDisposed || !identity.IsValid || identity.IsErased
                    || doc.Database.BlockTableId!=identity || AcadApp.DocumentManager.MdiActiveDocument?.Database.BlockTableId!=identity)
                    throw new InvalidOperationException("原型文档已切换或失效。");
            }
            Ensure();
            string root47=Path.Combine(Root,"0.1.47");
            Directory.CreateDirectory(root47);
            output=Path.Combine(root47,"native-"+DateTime.Now.ToString("yyyyMMddHHmmssfff")+"-"+Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(output);
            using var config=JsonDocument.Parse(File.ReadAllText(Path.Combine(root47,"native-command.json")));
            string expected=Path.GetFullPath(config.RootElement.GetProperty("SourceFixture").GetString()!);
            if(!string.Equals(Path.GetFullPath(path),expected,StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("活动图纸不是本轮配置明确登记的工具副本。");
            string product=Allowed(config.RootElement.GetProperty("ProductAssembly").GetString()!);
            string probe=Allowed(config.RootElement.GetProperty("ProbeAssembly").GetString()!);
            context=new ProbeContext(product);
            context.LoadFromAssemblyPath(product);
            var assembly=context.LoadFromAssemblyPath(probe);
            var method=assembly.GetType("Pn3DKit.CadCommandProbe.Probe",true)!.GetMethod("RunAsync",BindingFlags.Public|BindingFlags.Static)!;
            var task=(Task<string>)method.Invoke(null,[doc,product,output,(Action)Ensure])!;
            string result=await task;
            using(var writer=new StreamWriter(new FileStream(Path.Combine(output,"host-result.json"),FileMode.CreateNew)))
                writer.Write(result);
            using var parsed=JsonDocument.Parse(result);
            doc.Editor.WriteMessage("\n原生命令原型："+output+"；API验证成功="+parsed.RootElement.GetProperty("Success").GetBoolean()
                +"；含Esc验收完成="+parsed.RootElement.GetProperty("AcceptanceComplete").GetBoolean());
        }
        catch(System.Exception ex)
        {
            while(ex is TargetInvocationException {InnerException:{} inner}) ex=inner;
            if(output!=null)
                using(var writer=new StreamWriter(new FileStream(Path.Combine(output,"host-error.log"),FileMode.CreateNew)))
                    writer.Write(ex.ToString());
            try { doc?.Editor.WriteMessage("\n原型未完成："+ex.Message); } catch { }
        }
        finally
        {
            try { context?.Unload(); } catch(System.Exception ex) { System.Diagnostics.Trace.WriteLine(ex); }
            Interlocked.Exchange(ref _running,0);
        }
    }

    /// <summary>只允许本次仓库验证目录中的已有本地DLL，不加载用户任意外部路径。</summary>
    private static string Allowed(string path)
    {
        path=Path.GetFullPath(path);
        if(!path.StartsWith(Root+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase)
            || !File.Exists(path) || !path.EndsWith(".dll",StringComparison.OrdinalIgnoreCase)
            || new FileInfo(path).LinkTarget!=null) throw new InvalidOperationException("验证程序集不在允许范围："+path);
        return path;
    }

    /// <summary>测试与产品处于同一隔离上下文，Autodesk及框架复用宿主程序集。</summary>
    private sealed class ProbeContext(string product):AssemblyLoadContext(isCollectible:true)
    {
        protected override Assembly? Load(AssemblyName name)
        {
            if(name.Name=="Pn3DKit")return LoadFromAssemblyPath(product);
            return Default.Assemblies.FirstOrDefault(a=>string.Equals(a.GetName().Name,name.Name,StringComparison.OrdinalIgnoreCase));
        }
    }
}
