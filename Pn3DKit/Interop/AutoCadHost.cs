using System.Runtime.ExceptionServices;
using System.Diagnostics;
using Autodesk.AutoCAD.ApplicationServices;
using Pn3DKit.Models;
using Pn3DKit.Utils;
using AcadApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;

namespace Pn3DKit.Interop;

/// <summary>在 AutoCAD 主线程上安排应用上下文及目标文档命令上下文。</summary>
/// <remarks>不创建 COM 对象；回调内部收集错误，避免异常穿过 CAD 原生回调边界。</remarks>
internal sealed class AutoCadHost : ICadHost
{
    private readonly DocumentCollection _documents = AcadApp.DocumentManager;
    private readonly int _threadId = Environment.CurrentManagedThreadId;
    private readonly Func<bool> _isProjectCurrent;

    /// <summary>记录完整块表标识，避免托管文档包装变化导致身份误判；不拥有数据库。</summary>
    private sealed record Drawing(Document Document) : ICadDrawing
    {
        internal Autodesk.AutoCAD.DatabaseServices.Database Database { get; } = Document.Database;
        internal Autodesk.AutoCAD.DatabaseServices.ObjectId Identity { get; } = Document.Database.BlockTableId;
    }

    /// <summary>核对原数据库有效且仍为活动文档，不使用包装引用或文件名。</summary>
    private static void EnsureActive(ICadDrawing drawing)
    {
        var target = (Drawing)drawing;
        Pn3DKit.Commands.TagCheckCommands.EnsureDocument(target.Database, target.Identity);
    }

    /// <summary>记住原活动文档与项目身份，必须由 CAD 主线程创建。</summary>
    internal AutoCadHost(Func<bool> isProjectCurrent)
    {
        _isProjectCurrent = isProjectCurrent;
        OriginalDrawing = _documents.MdiActiveDocument is { } document ? new Drawing(document) : null;
    }

    /// <inheritdoc />
    public ICadDrawing? OriginalDrawing { get; }

    /// <summary>禁止从线程池调用 CAD API。</summary>
    private void VerifyThread()
    {
        if (Environment.CurrentManagedThreadId != _threadId)
            throw new CadOperationException("CAD 调用离开了宿主主线程，已停止。");
    }

    /// <inheritdoc />
    public void EnsureProject()
    {
        VerifyThread();
        if (!_isProjectCurrent()) throw new CadOperationException("当前 Plant 项目发生变化，已停止批次。");
    }

    /// <summary>在应用上下文执行同步回调，并将失败转换为任务结果。</summary>
    private Task<T> InApplicationAsync<T>(Func<T> action)
    {
        VerifyThread();
        var completion = new TaskCompletionSource<T>();
        void Invoke(object? unused)
        {
            try { VerifyThread(); completion.SetResult(action()); }
            catch (System.Exception ex) { completion.SetException(ex); }
        }
        if (_documents.IsApplicationContext) Invoke(null);
        else
        {
            try { _documents.ExecuteInApplicationContext(Invoke, null); }
            catch (System.Exception ex) { completion.TrySetException(ex); }
        }
        return completion.Task;
    }

    /// <summary>在目标文档命令上下文中运行同步操作；锁与异常均限制在回调内部。</summary>
    internal async Task<T> InDocumentAsync<T>(ICadDrawing drawing, Func<Document, T> action)
    {
        var document = ((Drawing)drawing).Document;
        T result = default!;
        System.Exception? failure = null;
        var execution = await InApplicationAsync(() =>
        {
            EnsureProject();
            EnsureActive(drawing);
            return _documents.ExecuteInCommandContextAsync(_ =>
            {
                try
                {
                    VerifyThread();
                    EnsureProject();
                    EnsureActive(drawing);
                    using (document.LockDocument()) result = action(document);
                }
                catch (System.Exception ex) { failure = ex; }
                return Task.CompletedTask;
            }, null);
        });
        await execution;
        if (failure != null) ExceptionDispatchInfo.Capture(failure).Throw();
        return result;
    }

    /// <inheritdoc />
    public ICadDrawing? FindOpen(string source)
    {
        EnsureProject();
        foreach (Document document in _documents)
        {
            string filename = document.Database.Filename;
            if (!string.IsNullOrWhiteSpace(filename) && Path.IsPathFullyQualified(filename)
                && ExportPolicy.Normalize(filename).Equals(source, StringComparison.OrdinalIgnoreCase))
                return new Drawing(document);
        }
        return null;
    }

    /// <inheritdoc />
    public Task<ICadDrawing> OpenAsync(string source) => InApplicationAsync<ICadDrawing>(() =>
    {
        EnsureProject();
        var original = _documents.MdiActiveDocument;
        var variables = CaptureVariables();
        Document? opened = null;
        try
        {
            SuppressVariables();
            opened = _documents.Open(source, false);
            return new Drawing(opened);
        }
        catch (System.Exception ex)
        {
            throw new CadOperationException("打开图纸失败，文档状态需检查：" + ex.Message, inner: ex);
        }
        finally
        {
            // 打开操作可能切换文档；在新旧文档中恢复原变量，并保持新文档活动。
            try
            {
                var active = _documents.MdiActiveDocument;
                RestoreVariables(variables);
                if (original != null && (active == null || original.Database.BlockTableId != active.Database.BlockTableId))
                {
                    _documents.MdiActiveDocument = original;
                    RestoreVariables(variables);
                    if (active != null) _documents.MdiActiveDocument = active;
                }
            }
            catch (System.Exception ex)
            {
                throw new CadOperationException("打开后的系统变量恢复失败，保留现场：" + ex.Message, inner: ex);
            }
        }
    });

    /// <inheritdoc />
    public Task ActivateAsync(ICadDrawing drawing) => InApplicationAsync(() =>
    {
        EnsureProject();
        var document = ((Drawing)drawing).Document;
        var target = (Drawing)drawing;
        if (target.Database.IsDisposed || !target.Identity.IsValid
            || !_documents.Cast<Document>().Any(d => d.Database.BlockTableId == target.Identity))
            throw new CadOperationException("目标文档已关闭。");
        _documents.MdiActiveDocument = document;
        return true;
    });

    /// <inheritdoc />
    public Task<DrawingState> ReadStateAsync(ICadDrawing drawing) => InDocumentAsync(drawing, document =>
        new DrawingState(Convert.ToInt32(AcadApp.GetSystemVariable("DBMOD")) != 0, document.IsReadOnly));

    /// <inheritdoc />
    public Task SaveAsync(ICadDrawing drawing, string marker) =>
        RunLispAsync(drawing, CadLispCommands.Save(marker), marker);

    /// <inheritdoc />
    public Task ExportAsync(ICadDrawing drawing, string temporary, string version, string marker) =>
        RunLispAsync(drawing, CadLispCommands.Export(temporary, marker, version), marker);

    /// <inheritdoc />
    public async Task CloseAsync(ICadDrawing drawing)
    {
        // 命令上下文检查后，在应用上下文内再次核对 DBMOD，避免切换造成的竞态。
        bool modified = (await ReadStateAsync(drawing)).Modified;
        if (modified) throw new CadOperationException("图纸在关闭前产生新修改，已保留。", true);
        await InApplicationAsync(() =>
        {
            EnsureProject();
            EnsureActive(drawing);
            if (Convert.ToInt32(AcadApp.GetSystemVariable("DBMOD")) != 0)
                throw new CadOperationException("关闭前文档或修改状态改变，已保留图纸。", true);
            ((Drawing)drawing).Document.CloseAndDiscard();
            return true;
        });
    }

    /// <summary>提交独立 LISP，在宿主空闲回调中等待完成标记与 LISP 结束，不阻塞 CAD 消息循环。</summary>
    /// <remarks>600 秒超时在空闲通知中检查；超时不强行中断原生操作，保留图纸及标记。</remarks>
    private async Task RunLispAsync(ICadDrawing drawing, string command, string marker)
    {
        VerifyThread();
        var document = ((Drawing)drawing).Document;
        var completion = new TaskCompletionSource<bool>();
        var watch = new Stopwatch();
        bool ended = false, canceled = false, unknown = false, failed = false, attached = false;
        void LispEnded(object? sender, EventArgs args) => ended = true;
        void LispCanceled(object? sender, EventArgs args) { ended = true; canceled = true; }
        void CommandCanceled(object? sender, EventArgs args) => canceled = true;
        void CommandFailed(object? sender, EventArgs args) => failed = true;
        void Unknown(object? sender, EventArgs args) => unknown = true;

        System.Exception? Detach()
        {
            if (!attached) return null;
            attached = false;
            System.Exception? failure = null;
            void Release(Action action)
            {
                try { action(); }
                catch (System.Exception ex) { failure ??= ex; }
            }
            Release(() => AcadApp.Idle -= Idle);
            Release(() => document.LispEnded -= LispEnded);
            Release(() => document.LispCancelled -= LispCanceled);
            Release(() => document.CommandCancelled -= CommandCanceled);
            Release(() => document.CommandFailed -= CommandFailed);
            Release(() => document.UnknownCommand -= Unknown);
            return failure;
        }

        void Finish(CadOperationException? error)
        {
            var cleanupError = Detach();
            if (cleanupError != null)
                error = new CadOperationException("LISP 等待器清理失败：" + cleanupError.Message, inner: cleanupError);
            if (error == null) completion.TrySetResult(true);
            else completion.TrySetException(error);
        }

        void Idle(object? sender, EventArgs args)
        {
            try
            {
                VerifyThread();
                EnsureProject();
                EnsureActive(drawing);
                if (watch.Elapsed > TimeSpan.FromSeconds(600))
                {
                    Finish(new CadOperationException("等待 LISP 完成超时；已提交命令可能仍在运行，请检查 CAD。"));
                    return;
                }
                if (!ended || !document.Editor.IsQuiescent) return;
                if (canceled) { Finish(new CadOperationException("用户取消了 LISP／CAD 命令。", true, true, true)); return; }
                if (unknown) { Finish(new CadOperationException("宿主没有所需命令，已停止批次。", true)); return; }
                if (!File.Exists(marker)) return;
                string status;
                try { status = File.ReadAllText(marker).Trim(); }
                catch (IOException) { return; }
                if (status.Length == 0) return;
                if (status == "DONE" && !failed) Finish(null);
                else Finish(new CadOperationException("LISP 命令失败：" + status, true, false));
            }
            catch (System.Exception ex)
            {
                Finish(ex as CadOperationException ?? new CadOperationException(
                    "LISP 完成状态检查失败：" + ex.Message, inner: ex));
            }
        }

        try
        {
            await InApplicationAsync(() =>
            {
                EnsureProject();
                EnsureActive(drawing);
                ExportPolicy.ValidateCadPath(marker);
                ExportPolicy.RejectLinks(marker);
                if (File.Exists(marker) || Directory.Exists(marker))
                    throw new IOException("LISP 完成标记已存在，拒绝复用：" + marker);
                string commandPath = Path.ChangeExtension(marker, ".command.lsp");
                using (var writer = new StreamWriter(new FileStream(commandPath, FileMode.CreateNew),
                    new System.Text.UTF8Encoding(false))) writer.WriteLine(command);
                // 先挂接所有监听，再提交表达式；LISP 必须等当前 .NET 回调退出后才能开始。
                attached = true;
                AcadApp.Idle += Idle;
                document.LispEnded += LispEnded;
                document.LispCancelled += LispCanceled;
                document.CommandCancelled += CommandCanceled;
                document.CommandFailed += CommandFailed;
                document.UnknownCommand += Unknown;
                watch.Start();
                document.SendStringToExecute(command + "\n", true, false, false);
                return true;
            });
            await completion.Task;
        }
        finally
        {
            var cleanupError = Detach();
            if (cleanupError != null)
                throw new CadOperationException("LISP 等待器清理失败：" + cleanupError.Message, inner: cleanupError);
        }
    }

    /// <summary>记住即将临时修改的变量，不更改外参加载设置。</summary>
    private static Dictionary<string, object> CaptureVariables() =>
        new[] { "FILEDIA", "CMDDIA", "PROXYNOTICE" }.ToDictionary(n => n, AcadApp.GetSystemVariable);

    /// <summary>抑制打开、保存和导出操作的相关对话框。</summary>
    private static void SuppressVariables()
    {
        foreach (string name in new[] { "FILEDIA", "CMDDIA", "PROXYNOTICE" }) AcadApp.SetSystemVariable(name, 0);
    }

    /// <summary>逐项尝试恢复变量；任意失败均向调度器报告。</summary>
    private static void RestoreVariables(IReadOnlyDictionary<string, object> variables)
    {
        System.Exception? failure = null;
        foreach (var pair in variables)
        {
            try { AcadApp.SetSystemVariable(pair.Key, pair.Value); }
            catch (System.Exception ex) { failure ??= ex; }
        }
        if (failure != null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
