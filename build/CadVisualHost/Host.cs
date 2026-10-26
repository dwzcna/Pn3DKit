using System.IO;
using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Runtime;
using AcadApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;

[assembly: CommandClass(typeof(Pn3DKit.CadVisualHost.Host))]
namespace Pn3DKit.CadVisualHost;

/// <summary>授权测试专用入口；同一入口复用隔离的托管验证代码，不改变 CAD 安全设置。</summary>
public sealed class Host
{
    private const string Root = @"D:\src\P3DBatchExport\artifacts\validation\0.1.34";
    /// <summary>只加载本轮验证目录的本地构建；CAD 调用仍在当前命令的主线程执行。</summary>
    [CommandMethod("PNVISUALTEST34", CommandFlags.Modal | CommandFlags.UsePickSet)]
    public void Run()
    {
        var doc = AcadApp.DocumentManager.MdiActiveDocument;
        if (doc == null || !Path.GetFullPath(doc.Database.Filename).StartsWith(Root + @"\261027003013\", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("原生验证只允许当前授权目录的测试图纸。");
        var identity = doc.Database.BlockTableId;
        var working = HostApplicationServices.WorkingDatabase;
        var selected = doc.Editor.SelectImplied().Value?.GetObjectIds() ?? [];
        string[] names = ["FILEDIA", "CMDDIA", "DBMOD", "LWDISPLAY", "TRANSPARENCYDISPLAY", "PICKFIRST", "SECURELOAD", "TRUSTEDPATHS"];
        var before = names.ToDictionary(n => n, AcadApp.GetSystemVariable);
        string output = Path.Combine(Root, "native-" + DateTime.Now.ToString("yyyyMMddHHmmssfff"));
        Directory.CreateDirectory(output);
        using var log = new StreamWriter(new FileStream(Path.Combine(output,"host.log"),FileMode.CreateNew,FileAccess.Write,FileShare.Read))
            { AutoFlush = true };
        ProbeContext? context = null;
        string? error = null; bool nativePassed = false;
        void Ensure()
        {
            if (AcadApp.DocumentManager.MdiActiveDocument?.Database.BlockTableId != identity
                || doc.Database.IsDisposed || doc.Database.BlockTableId != identity)
                throw new InvalidOperationException("授权测试文档已经切换或关闭。");
        }
        try
        {
            Ensure();
            using var config = JsonDocument.Parse(File.ReadAllText(Path.Combine(Root,"native-host.json")));
            string probe = Allowed(config.RootElement.GetProperty("ProbeAssembly").GetString()!);
            string product = Allowed(config.RootElement.GetProperty("ProductAssembly").GetString()!);
            log.WriteLine("主图：" + doc.Database.Filename + "；验证：" + probe + "；产品：" + product);
            context = new ProbeContext(product);
            context.LoadFromAssemblyPath(product);
            var assembly = context.LoadFromAssemblyPath(probe);
            var method = assembly.GetType("Pn3DKit.CadVisualProbe.Probe",true)!.GetMethod("Run",BindingFlags.Static|BindingFlags.Public)
                ?? throw new MissingMethodException("缺少原生验证入口。");
            string result = (string)method.Invoke(null,[doc,product,output,(Action)Ensure])!;
            File.WriteAllText(Path.Combine(output,"probe.json"),result);
            using var parsed = JsonDocument.Parse(result);
            nativePassed = parsed.RootElement.GetProperty("Success").GetBoolean();
        }
        catch (System.Exception ex)
        {
            while (ex is TargetInvocationException { InnerException: {} inner }) ex = inner;
            error = ex.ToString(); log.WriteLine(error);
        }
        finally
        {
            try { context?.Unload(); } catch(System.Exception ex) { error = (error ?? "") + ex; }
            try { HostApplicationServices.WorkingDatabase = working; } catch(System.Exception ex) { error = (error ?? "") + ex; }
            var after = names.ToDictionary(n => n,AcadApp.GetSystemVariable);
            bool state = before.All(p => Equals(p.Value,after[p.Key]));
            bool selection = selected.ToHashSet().SetEquals(doc.Editor.SelectImplied().Value?.GetObjectIds() ?? []);
            bool document = AcadApp.DocumentManager.MdiActiveDocument?.Database.BlockTableId == identity;
            var summary = new { Success = nativePassed && error == null && state && selection && document
                && HostApplicationServices.WorkingDatabase == working, NativePassed = nativePassed,
                Host = doc.Database.Filename, Before = before, After = after, StateUnchanged = state,
                SelectionUnchanged = selection, DocumentUnchanged = document, WorkingDatabaseRestored = HostApplicationServices.WorkingDatabase == working, Error = error };
            File.WriteAllText(Path.Combine(output,"host.json"),JsonSerializer.Serialize(summary,new JsonSerializerOptions {WriteIndented=true}));
            log.WriteLine("完成，成功=" + summary.Success);
            doc.Editor.WriteMessage("\nPN原生验证：" + output + "；成功=" + summary.Success);
        }
    }

    /// <summary>验证代码必须来自本轮构建目录，拒绝路径逃逸和符号链接。</summary>
    private static string Allowed(string path)
    {
        path = Path.GetFullPath(path);
        if (!path.StartsWith(Root + Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase)
            || !File.Exists(path) || !string.Equals(Path.GetExtension(path),".dll",StringComparison.OrdinalIgnoreCase)
            || new FileInfo(path).LinkTarget != null)
            throw new InvalidOperationException("不是本轮验证目录的本地 DLL：" + path);
        return path;
    }

    /// <summary>仅隔离本轮托管代码；Autodesk 和框架依赖复用已加载宿主对象身份。</summary>
    private sealed class ProbeContext(string product) : AssemblyLoadContext(isCollectible:true)
    {
        protected override Assembly? Load(AssemblyName name)
        {
            if (name.Name == "Pn3DKit") return LoadFromAssemblyPath(product);
            return Default.Assemblies.FirstOrDefault(a => string.Equals(a.GetName().Name,name.Name,StringComparison.OrdinalIgnoreCase));
        }
    }
}
