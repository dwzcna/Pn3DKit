using System.IO;
using System.Text.Json;
using System.Windows.Threading;
using Autodesk.AutoCAD.ApplicationServices;
using Pn3DKit.Services;
using AcadApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;

namespace Pn3DKit.CadCommandProbe;

public static partial class Probe
{
    /// <summary>在工具副本中区分异步宿主、保存与文档切换的收尾；后置计时器只记录状态并自行解除。</summary>
    private static async Task<string> Lifecycle(Document document, string product, string output, Action ensure, StreamWriter log, string mode)
    {
        int thread = Environment.CurrentManagedThreadId;
        var originalIdentity = document.Database.BlockTableId;
        var allowed = new HashSet<Autodesk.AutoCAD.DatabaseServices.ObjectId> { originalIdentity };
        object State(string stage)
        {
            var active = AcadApp.DocumentManager.MdiActiveDocument;
            if (document.Database.IsDisposed || document.Database.BlockTableId != originalIdentity
                || active == null || !allowed.Contains(active.Database.BlockTableId))
                throw new InvalidOperationException("诊断文档身份变化。");
            if (Environment.CurrentManagedThreadId != thread)
                throw new InvalidOperationException("验收续体离开CAD主线程。");
            return new
            {
                Stage = stage, Thread = Environment.CurrentManagedThreadId,
                Context = SynchronizationContext.Current?.GetType().FullName,
                ApplicationContext = AcadApp.DocumentManager.IsApplicationContext,
                Document = active.Database.Filename,
                Command = active.CommandInProgress,
                Active = AcadApp.GetSystemVariable("CMDACTIVE"),
                Names = AcadApp.GetSystemVariable("CMDNAMES"),
                Quiescent = document.Editor.IsQuiescent,
                ApplicationQuiescent = AcadApp.IsQuiescent
            };
        }
        var states = new List<object> { State("entry") };
        await Task.Delay(150);
        states.Add(State("after-delay"));
        var files = new RunLogFiles();
        var host = NewHost(() => true, files);
        if (mode == "ActivationLifecycle")
        {
            await host.ActivateAsync(host.OriginalDrawing!);
            states.Add(State("after-reactivating-current-document"));
        }
        if (mode == "SaveLifecycle")
        {
            await host.SaveAsync(host.OriginalDrawing!);
            states.Add(State("after-save"));
        }
        if (mode.StartsWith("DocumentLifecycle", StringComparison.Ordinal))
        {
            string path = Path.Combine(output, "LifecycleFixture.dwg");
            File.Copy(document.Database.Filename, path, false);
            Pn3DKit.Models.ICadDrawing opened;
            if (mode == "DocumentLifecycleAppOpen")
            {
                AcadApp.DocumentManager.AppContextOpenDocument(path);
                opened = host.FindOpen(path) ?? throw new InvalidOperationException("同步应用上下文没有打开副本。");
            }
            else opened = await host.OpenAsync(path);
            var openedDocument = AcadApp.DocumentManager.MdiActiveDocument;
            allowed.Add(openedDocument.Database.BlockTableId);
            states.Add(State("after-open"));
            await host.ActivateAsync(opened);
            states.Add(State("after-activate"));
            if ((await host.ReadStateAsync(opened)).Modified)
                throw new InvalidOperationException("诊断副本意外修改，保留现场。");
            states.Add(State("after-short-lock"));
            if (mode is "DocumentLifecycleBoundary" or "DocumentLifecycleRedraw")
            {
                await AcadApp.DocumentManager.ExecuteInCommandContextAsync(async _ =>
                {
                    if (mode == "DocumentLifecycleRedraw")
                        await openedDocument.Editor.CommandAsync("_.REDRAW");
                }, null);
                states.Add(State("after-command-boundary"));
            }
            if (mode == "DocumentLifecycleInactive")
            {
                await host.ActivateAsync(host.OriginalDrawing!);
                states.Add(State("before-inactive-close"));
                openedDocument.CloseAndDiscard();
            }
            else await host.CloseAsync(opened);
            states.Add(State("after-close"));
            await host.ActivateAsync(host.OriginalDrawing!);
            states.Add(State("after-open-close"));
            ensure();
        }
        log.WriteLine(JsonSerializer.Serialize(states));
        var settled = new List<object>();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        int ticks = 0;
        void Poll(object? sender, EventArgs args)
        {
            try
            {
                settled.Add(State("post-return-" + ++ticks));
                if (!AcadApp.IsQuiescent && document.CommandInProgress.Length == 0
                    && Convert.ToInt32(AcadApp.GetSystemVariable("CMDACTIVE")) == 0)
                {
                    if (ticks == 1)
                    {
                        document.Editor.PostCommandPrompt();
                        settled.Add(State("after-public-post-prompt"));
                    }
                    else if (ticks == 2)
                    {
                        document.SendStringToExecute("\u0003\u0003", true, false, false);
                        settled.Add(new { Recovery = "SDK cancel only idle tool fixture" });
                    }
                }
            }
            catch (Exception ex) { settled.Add(new { Error = ex.ToString() }); ticks = 3; }
            if (ticks < 3) return;
            timer.Stop(); timer.Tick -= Poll;
            using var writer = new StreamWriter(new FileStream(Path.Combine(output, "settled.json"), FileMode.CreateNew));
            writer.Write(JsonSerializer.Serialize(settled, new JsonSerializerOptions { WriteIndented = true }));
        }
        timer.Tick += Poll; timer.Start();
        return JsonSerializer.Serialize(new { Success = true, AcceptanceComplete = false, Kind = mode, Product = product, States = states },
            new JsonSerializerOptions { WriteIndented = true });
    }
}
