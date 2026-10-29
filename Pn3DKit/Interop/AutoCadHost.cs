using System.Runtime.ExceptionServices;
using System.Diagnostics;
using Autodesk.AutoCAD.ApplicationServices;
using Pn3DKit.Models;
using Pn3DKit.Services;
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
    private readonly RunLogFiles? _logFiles;

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
    internal AutoCadHost(Func<bool> isProjectCurrent, RunLogFiles? logFiles = null)
    {
        _isProjectCurrent = isProjectCurrent;
        _logFiles = logFiles;
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
            catch (System.Exception ex)
            {
                if (ex is CadOperationException { StateKnown: false }) _logFiles?.Protect(ex.Message);
                completion.SetException(ex);
            }
        }
        if (_documents.IsApplicationContext) Invoke(null);
        else
        {
            try { _documents.ExecuteInApplicationContext(Invoke, null); }
            catch (System.Exception ex) { completion.TrySetException(ex); }
        }
        return completion.Task;
    }

    /// <summary>在主线程应用上下文锁定目标文档并执行属性面板操作，保留 PICKFIRST 和面板焦点。</summary>
    /// <remarks>不启动命令；项目和完整数据库身份在锁前后核对，异常作为任务失败返回。</remarks>
    /// <param name="drawing">仍有效且活动的目标图纸。</param>
    /// <param name="action">同步读写回调，不允许等待或切换文档。</param>
    /// <returns>回调结果；上下文或写入失败时返回失败任务。</returns>
    internal Task<T> InLockedDocumentAsync<T>(ICadDrawing drawing, Func<Document, T> action) =>
        InApplicationAsync(() =>
        {
            EnsureProject();
            EnsureActive(drawing);
            using (((Drawing)drawing).Document.LockDocument())
            {
                EnsureProject();
                EnsureActive(drawing);
                return action(((Drawing)drawing).Document);
            }
        });

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
    public async Task<ICadDrawing> OpenAsync(string source)
    {
        var drawing = await OpenCoreAsync(source);
        try
        {
            // OPEN可已返回且Editor空闲，但应用输入状态尚未结束；官方空命令上下文完成该边界。
            await InDocumentAsync(drawing, _ => true);
            EnsureProject();
            EnsureActive(drawing);
            if (!AcadApp.IsQuiescent || !((Drawing)drawing).Document.Editor.IsQuiescent)
                throw new InvalidOperationException("打开后宿主尚未确认全局空闲。");
            return drawing;
        }
        catch (System.Exception ex)
        {
            string message = "打开后的命令上下文收尾失败，保留图纸及环境：" + ex.Message;
            _logFiles?.Protect(message);
            throw new CadOperationException(message, inner: ex);
        }
    }

    /// <summary>同步打开并恢复新旧文档变量；调用方仍须完成命令上下文边界后才处理该图纸。</summary>
    private Task<ICadDrawing> OpenCoreAsync(string source) => InApplicationAsync<ICadDrawing>(() =>
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
    public Task<DrawingState> ReadStateAsync(ICadDrawing drawing) => InLockedDocumentAsync(drawing, document =>
        new DrawingState(Convert.ToInt32(AcadApp.GetSystemVariable("DBMOD")) != 0, document.IsReadOnly));

    /// <inheritdoc />
    public Task SaveAsync(ICadDrawing drawing) => RunCommandAsync(drawing, ["_.QSAVE"]);

    /// <inheritdoc />
    public Task ExportAsync(ICadDrawing drawing, string temporary, string version)
    {
        ExportPolicy.ValidatePlantExportPath(temporary);
        ExportPolicy.RejectLinks(temporary);
        if (File.Exists(temporary) || Directory.Exists(temporary))
            throw new IOException("临时导出目标已存在，拒绝覆盖：" + temporary);
        return RunCommandAsync(drawing, ExportPolicy.ExportArguments(temporary, version));
    }

    /// <inheritdoc />
    public async Task CloseAsync(ICadDrawing drawing)
    {
        // 短锁只读检查后，在应用上下文内再次核对 DBMOD，避免切换造成的竞态。
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

    /// <summary>执行原生命令；变量仅在命令、空闲核对和监听清理全部明确结束后恢复。</summary>
    private async Task RunCommandAsync(ICadDrawing drawing, object[] arguments)
    {
        Dictionary<string, object>? variables = null;
        System.Exception? primary = null;
        bool known = true;
        try
        {
            await InApplicationAsync(() =>
            {
                EnsureProject();
                EnsureActive(drawing);
                variables = CaptureVariables();
                SuppressVariables();
                return true;
            });
            known = false;
            await NativeCommandOperation.RunAsync(new NativeCommandHost(this, drawing, arguments));
            known = true;
        }
        catch (System.Exception ex)
        {
            primary = ex;
            if (ex is CadOperationException cad) known = cad.StateKnown;
        }
        finally
        {
            if (variables != null && known)
            {
                try
                {
                    await InApplicationAsync(() =>
                    {
                        EnsureProject();
                        EnsureActive(drawing);
                        RestoreVariables(variables);
                        return true;
                    });
                }
                catch (System.Exception cleanup)
                {
                    primary = new CadOperationException("原生命令后的变量恢复失败：" + cleanup.Message,
                        inner: primary == null ? cleanup : new AggregateException(primary, cleanup));
                }
            }
            if (!known || primary is CadOperationException { StateKnown: false })
                _logFiles?.Protect(primary?.Message ?? "原生命令尚未确认安全结束。");
        }
        if (primary is CadOperationException operation)
            primary = new CadOperationException(arguments[0] + "：" + operation.Message,
                operation.StateKnown, operation.StopBatch, operation.Canceled, operation);
        if (primary != null) ExceptionDispatchInfo.Capture(primary).Throw();
    }

    /// <summary>将官方命令上下文和主线程检查点提供给离线可测的生命周期策略。</summary>
    private sealed class NativeCommandHost(AutoCadHost owner, ICadDrawing drawing, object[] arguments) : INativeCommandHost
    {
        private readonly Stopwatch _watch = Stopwatch.StartNew();
        private Document Document => ((Drawing)drawing).Document;
        public TimeSpan Elapsed => _watch.Elapsed;
        public bool IsQuiescent => AcadApp.IsQuiescent && Document.Editor.IsQuiescent
            && Document.CommandInProgress.Length == 0
            && Convert.ToInt32(AcadApp.GetSystemVariable("CMDACTIVE")) == 0;
        public void EnsureContext() { owner.EnsureProject(); EnsureActive(drawing); }
        public void Protect(string reason) => owner._logFiles?.Protect(reason);

        /// <summary>异常不跨越原生回调；应用上下文切换和命令Task均完整等待。</summary>
        public async Task ExecuteAsync()
        {
            System.Exception? failure = null;
            var execution = await owner.InApplicationAsync(() =>
            {
                EnsureContext();
                return owner._documents.ExecuteInCommandContextAsync(async _ =>
                {
                    try
                    {
                        EnsureContext();
                        await Document.Editor.CommandAsync(arguments);
                        EnsureContext();
                    }
                    catch (System.Exception ex) { failure = ex; }
                }, null);
            });
            await execution;
            if (failure != null) ExceptionDispatchInfo.Capture(failure).Throw();
        }

        /// <summary>开始前订阅命令事件、Idle及DispatcherTimer；释放时逐项清理并保留所有错误。</summary>
        public IDisposable Observe(Action<NativeCommandEvent> report, Action checkpoint)
        {
            string target = Convert.ToString(arguments[0])!.TrimStart('_', '.', '-');
            bool Matches(CommandEventArgs args) =>
                args.GlobalCommandName.TrimStart('_', '.', '-').Equals(target, StringComparison.OrdinalIgnoreCase);
            void Started(object? sender, CommandEventArgs args) { if (Matches(args)) report(NativeCommandEvent.Started); }
            void Ended(object? sender, CommandEventArgs args) { if (Matches(args)) report(NativeCommandEvent.Ended); }
            void Failed(object? sender, CommandEventArgs args) => report(NativeCommandEvent.Failed);
            void Canceled(object? sender, CommandEventArgs args) => report(NativeCommandEvent.Canceled);
            void Unknown(object? sender, EventArgs args) => report(NativeCommandEvent.Unknown);
            void Check(object? sender, EventArgs args) => checkpoint();
            var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
            var cleanup = new NativeCleanup([
                () => timer.Stop(), () => timer.Tick -= Check, () => AcadApp.Idle -= Check,
                () => Document.CommandWillStart -= Started, () => Document.CommandEnded -= Ended,
                () => Document.CommandFailed -= Failed, () => Document.CommandCancelled -= Canceled,
                () => Document.UnknownCommand -= Unknown
            ]);
            try
            {
                Document.CommandWillStart += Started; Document.CommandEnded += Ended;
                Document.CommandFailed += Failed; Document.CommandCancelled += Canceled;
                Document.UnknownCommand += Unknown;
                AcadApp.Idle += Check; timer.Tick += Check; timer.Start();
                return cleanup;
            }
            catch (System.Exception primary)
            {
                try { cleanup.Dispose(); }
                catch (System.Exception release) { throw new AggregateException(primary, release); }
                throw;
            }
        }
    }

    /// <summary>主线程一次性释放本次监听；某项失败仍清理其余项。</summary>
    private sealed class NativeCleanup(Action[] actions) : IDisposable
    {
        private bool _disposed;
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            var errors = new List<System.Exception>();
            foreach (var action in actions)
                try { action(); } catch (System.Exception ex) { errors.Add(ex); }
            if (errors.Count != 0) throw new AggregateException(errors);
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
