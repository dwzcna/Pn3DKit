using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Pn3DKit.Models;
using Pn3DKit.Services;
using Pn3DKit.UI;
using Pn3DKit.Utils;
using AcadApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;

namespace Pn3DKit.Interop;

/// <summary>主线程协调自定义属性读取和显式单值编辑，所有原生回调内部捕获失败。</summary>
internal sealed class FastSelectionController : IDisposable
{
    private sealed record EditLease(PnEditContext<ObjectId, ObjectId> Context, Database Database, FastPropertyAccess Access);
    private readonly Func<bool> _visible;
    private readonly FastPropertiesView _view;
    private readonly PnPropertyBatch<ObjectId, ObjectId> _batch = new();
    private readonly Dictionary<Guid, EditLease> _tickets = [];
    private Document? _document;
    private Database? _database;
    private ObjectId _identity;
    private FastPropertyAccess? _access;
    private IReadOnlyList<PnPropertySpec> _specs = [];
    private ObjectId[] _targets = [];
    private PnObjectProperties? _single;
    private (PnEditRequest Request, EditLease Lease)? _pending;
    private bool _dirty = true, _paused, _inside, _disposed, _writing;
    private long _epoch;
    private string _notice = "";

    /// <summary>事件只使快照过期，SDK 属性查询统一延迟到 Idle 或受控命令上下文。</summary>
    internal FastSelectionController(Func<bool> visible, FastPropertiesView view)
    {
        _visible = visible; _view = view;
        try
        {
            AcadApp.Idle += Idle;
            AcadApp.DocumentManager.DocumentActivated += DocumentChanged;
            AcadApp.DocumentManager.DocumentToBeDestroyed += DocumentChanged;
            view.RowsChanging += PathsChanging; view.RowsCommitted += PathsCommitted; view.EditRequested += QueueEdit;
        }
        catch { Dispose(); throw; }
    }
    internal void RequestRefresh() => Invalidate(true);
    private void Invalidate(bool read)
    {
        _epoch++; _dirty = read; _batch.Invalidate(); _access = null; _tickets.Clear(); _single = null;
    }
    private void PathsChanging() => Invalidate(false);
    private void PathsCommitted() => RequestRefresh();
    private void Changed(object? sender, EventArgs args) => RequestRefresh();
    private void DocumentChanged(object? sender, DocumentCollectionEventArgs args) => RequestRefresh();
    private void CommandChanged(object? sender, CommandEventArgs args) => RequestRefresh();
    private void ObjectChanged(object? sender, ObjectEventArgs args) => RequestRefresh();
    private void ObjectErased(object? sender, ObjectErasedEventArgs args) => RequestRefresh();

    /// <summary>UI 只排队显式提交，空闲时重新验证；无效票据不会延期自动写入。</summary>
    private void QueueEdit(PnEditRequest request)
    {
        try
        {
            if (_writing || _pending != null || !_tickets.TryGetValue(request.Token, out var lease))
                throw new InvalidOperationException("编辑已过期或正在提交，请刷新后重试。");
            var expected = lease.Context;
            if (request.RowId != expected.RowId || request.RowRevision != expected.RowRevision
                || request.Path != expected.Path || request.Original != expected.Original)
                throw new InvalidOperationException("属性路径或原值与读取快照不一致。");
            _pending = (request, lease); _tickets.Clear();
        }
        catch (System.Exception ex) { _notice = "未写入：" + ex.Message; RequestRefresh(); _view.ResumeInputFocus(); _view.ShowStatus(_notice); }
    }

    /// <summary>只在 CAD 主线程空闲、面板展开且无批次时读取，完整选择按 32 个对象分批。</summary>
    private void Idle(object? sender, EventArgs args)
    {
        if (_disposed || _inside || _writing) return;
        _inside = true;
        try
        {
            if (!_visible())
            {
                if (_document != null) { Unbind(); RequestRefresh(); _view.SuspendEditing(); }
                _view.ClearInputFocus();
                _pending = null; return;
            }
            var document = AcadApp.DocumentManager.MdiActiveDocument;
            if (document == null) { Clear("当前没有活动图纸。"); return; }
            var database = document.Database;
            if (database.IsDisposed) { Clear("图纸数据库已失效或关闭。"); return; }
            var identity = database.BlockTableId;
            if (identity.IsNull || !identity.IsValid || identity.IsErased) { Clear("图纸数据库已失效或关闭。"); return; }
            if (_identity != identity || _database == null || _database.IsDisposed) Bind(document, database, identity);

            // 在读锁外固定状态；面板输入本身不会被误判为 CAD 正在运行命令。
            bool busy = PnPropertyPolicy.IsBusy(BatchRunGate.IsRunning, !string.IsNullOrEmpty(document.CommandInProgress),
                document.Editor.IsQuiescent, _view.HasInputFocus);
            if (busy)
            {
                if (_pending != null) { _pending = null; _notice = "未写入：CAD 或批次忙碌，请空闲后重试。"; _view.ResumeInputFocus(); }
                if (!_paused)
                {
                    _paused = true; RequestRefresh(); _view.SuspendEditing();
                    _view.Show(PnPropertyPolicy.Empty(_view.ReadSpecs(), "CAD 忙碌，空闲后刷新 · " + _notice, "等待刷新"));
                }
                return;
            }
            if (_paused) { _paused = false; RequestRefresh(); }
            if (_pending is { } pending)
            {
                _pending = null; WritePending(pending.Request, pending.Lease); return;
            }
            if (_access != null && !_access.IsCurrent) RequestRefresh();
            if (_dirty)
            {
                long epoch = _epoch;
                var targets = Selection(document);
                if (epoch != _epoch || !SameDatabase(database, identity)) return;
                _specs = _view.ReadSpecs(); _dirty = false; _targets = targets;
                _view.SuspendEditing();
                if (targets.Length == 0) { _view.Show(PnPropertyPolicy.Empty(_specs, "只读 · 未选择对象" + Notice())); return; }
                _access = new FastPropertyAccess(busy);
                if (!_access.IsCurrent) { RequestRefresh(); return; }
                _batch.Begin(identity, targets, _specs);
                _view.Show(PnPropertyPolicy.Empty(_specs, $"已选 {targets.Length} 个对象 · 正在读取", "读取中"));
            }
            if (!_batch.HasWork || _access == null) return;
            var access = _access; long currentEpoch = _epoch;
            PnPanelSnapshot? snapshot;
            using (document.LockDocument())
            using (var transaction = database.TransactionManager.StartOpenCloseTransaction())
            {
                if (!SameDatabase(database, identity) || !access.IsCurrent) { RequestRefresh(); return; }
                snapshot = _batch.Step(identity, id =>
                {
                    var item = access.Read(id, document, transaction, _specs, _targets.Length == 1);
                    if (_targets.Length == 1) _single = item;
                    return item;
                });
            }
            if (currentEpoch != _epoch || !SameDatabase(database, identity) || !access.IsCurrent)
            { RequestRefresh(); return; }
            if (snapshot != null)
            {
                if (snapshot.Count == 1 && _single != null)
                {
                    var cells = snapshot.Cells.Select(cell =>
                    {
                        if (!cell.Permission.Allowed || cell.Value.Kind != FastValueKind.Value) return cell;
                        string? path = PnPropertyPolicy.Normalize(cell.Spec.Input, out _);
                        if (path == null) return cell;
                        Guid token = Guid.NewGuid();
                        var context = new PnEditContext<ObjectId, ObjectId>(identity, _targets[0], _epoch, cell.Spec.Id,
                            cell.Spec.Revision, path, _single.TargetKey, cell.Value.Raw, 1, cell.Permission);
                        _tickets[token] = new(context, database, access);
                        return cell with { EditToken = token };
                    }).ToArray();
                    snapshot = snapshot with { Cells = cells };
                }
                _view.Show(snapshot with { Status = snapshot.Status + Notice() });
            }
            else if (_batch.HasWork)
                _view.ShowStatus($"已选 {_targets.Length} 个对象 · 正在读取 {_batch.Processed}/{_targets.Length}");
        }
        catch (System.Exception ex)
        {
            RequestRefresh(); _dirty = false; _pending = null;
            try { _view.SuspendEditing(); _view.ResumeInputFocus(); _view.ShowStatus("无法刷新：" + FastPropertyAccess.Describe(ex)); }
            catch (System.Exception output) { System.Diagnostics.Trace.WriteLine(output); }
        }
        finally { _inside = false; }
    }

    /// <summary>批次租约覆盖排队、同步写入及回读，结束前不释放；异常不穿过原生回调。</summary>
    private async void WritePending(PnEditRequest request, EditLease edit)
    {
        if (_disposed) return;
        using var lease = BatchRunGate.TryEnter();
        if (lease == null) { _notice = "未写入：已有 Pn3DKit 批次。"; RequestRefresh(); _view.ResumeInputFocus(); return; }
        _writing = true; _view.SuspendInputFocus();
        try
        {
            var host = new AutoCadHost(() => !_disposed && edit.Access.IsCurrent);
            var drawing = host.OriginalDrawing ?? throw new InvalidOperationException("当前没有活动图纸。");
            string actual = await host.InDocumentAsync(drawing, document =>
            {
                EnsureEdit(edit);
                var selected = Selection(document);
                var spec = _view.ReadSpecs().FirstOrDefault(s => s.Id == request.RowId)
                    ?? throw new InvalidOperationException("属性行已删除。");
                string path = PnPropertyPolicy.Normalize(spec.Input, out string reason) ?? throw new InvalidOperationException(reason);
                var access = new FastPropertyAccess(busy: false);
                PnObjectProperties item;
                using (var transaction = document.Database.TransactionManager.StartOpenCloseTransaction())
                    item = access.Read(edit.Context.Target, document, transaction, [spec], true);
                var cell = item.Cells[0];
                var current = new PnEditContext<ObjectId, ObjectId>(document.Database.BlockTableId,
                    selected.Length == 1 ? selected[0] : ObjectId.Null, _epoch, spec.Id, spec.Revision, path,
                    item.TargetKey, cell.Value.Raw, selected.Length, cell.Permission);
                string? rejection = PnPropertyPolicy.Validate(edit.Context, current);
                if (rejection != null || cell.Value.Kind != FastValueKind.Value)
                    throw new InvalidOperationException(rejection ?? "属性当前不可读取。");
                EnsureEdit(edit);
                return access.Write(edit.Context.Target, document, spec, request.Original, request.Value, currentKey =>
                {
                    EnsureEdit(edit);
                    if (currentKey != edit.Context.TargetKey) throw new InvalidOperationException("写入前 Plant 数据关联已变化。");
                    var latest = Selection(document);
                    if (latest.Length != 1 || latest[0] != edit.Context.Target)
                        throw new InvalidOperationException("写入前选择对象已变化。");
                    if (!_view.ReadSpecs().Contains(spec)) throw new InvalidOperationException("写入前属性路径已变化。");
                });
            });
            _notice = request.Path + " 已更新为 " + actual + "；未保存图纸。";
        }
        catch (System.Exception ex) { _notice = "提交失败：" + FastPropertyAccess.Describe(ex); }
        finally
        {
            _writing = false; RequestRefresh();
            if (!_disposed)
            {
                try { _view.ResumeInputFocus(); _view.ShowStatus(_notice); }
                catch (System.Exception ex) { System.Diagnostics.Trace.WriteLine(ex); }
            }
        }
    }

    /// <summary>回调及写入前核对原数据库、项目、轮次和面板状态；不按包装引用识别图纸。</summary>
    private void EnsureEdit(EditLease edit)
    {
        if (_disposed || !_visible()) throw new InvalidOperationException("面板已关闭或隐藏，未继续提交。");
        if (!SameDatabase(edit.Database, edit.Context.Identity)) throw new InvalidOperationException("图纸身份已变化或原图已关闭。");
        if (_epoch != edit.Context.Epoch || !edit.Access.IsCurrent) throw new InvalidOperationException("选择、对象、路径或项目已变化，编辑已过期。");
    }
    private string Notice() => _notice.Length == 0 ? "" : "\n" + _notice;
    private static ObjectId[] Selection(Document document)
    {
        var result = document.Editor.SelectImplied();
        using var selection = result.Value;
        return result.Status == PromptStatus.OK && selection != null ? selection.GetObjectIds().Distinct().ToArray() : [];
    }
    private static bool SameDatabase(Database database, ObjectId identity)
    {
        if (database.IsDisposed || identity.IsNull || !identity.IsValid || identity.IsErased) return false;
        var current = AcadApp.DocumentManager.MdiActiveDocument?.Database;
        return current != null && !current.IsDisposed && current.BlockTableId == identity;
    }
    private void Bind(Document document, Database database, ObjectId identity)
    {
        Unbind(); RequestRefresh(); _document = document; _database = database; _identity = identity;
        try
        {
            document.ImpliedSelectionChanged += Changed; document.CommandEnded += CommandChanged;
            document.CommandCancelled += CommandChanged; document.CommandFailed += CommandChanged;
            database.ObjectModified += ObjectChanged; database.ObjectErased += ObjectErased;
        }
        catch { Unbind(); throw; }
    }
    private void Clear(string message)
    {
        bool changed = _document != null || _dirty;
        Unbind(); RequestRefresh(); _dirty = false; _pending = null;
        if (changed) { _view.ClearInputFocus(); _view.SuspendEditing(); _view.Show(PnPropertyPolicy.Empty(_view.ReadSpecs(), "只读 · " + message + Notice())); }
    }
    private void Unbind()
    {
        var document = _document; var database = _database; _document = null; _database = null; _identity = ObjectId.Null;
        if (document != null)
        {
            Try(() => document.ImpliedSelectionChanged -= Changed); Try(() => document.CommandEnded -= CommandChanged);
            Try(() => document.CommandCancelled -= CommandChanged); Try(() => document.CommandFailed -= CommandChanged);
        }
        if (database != null) { Try(() => database.ObjectModified -= ObjectChanged); Try(() => database.ObjectErased -= ObjectErased); }
    }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true; _pending = null;
        Try(() => AcadApp.Idle -= Idle); Try(() => AcadApp.DocumentManager.DocumentActivated -= DocumentChanged);
        Try(() => AcadApp.DocumentManager.DocumentToBeDestroyed -= DocumentChanged);
        _view.RowsChanging -= PathsChanging; _view.RowsCommitted -= PathsCommitted; _view.EditRequested -= QueueEdit;
        Unbind(); RequestRefresh();
        try { _view.SuspendEditing(); }
        finally { _view.ClearInputFocus(); }
    }
    private static void Try(Action action)
    {
        try { action(); }
        catch (System.Exception ex) { System.Diagnostics.Trace.WriteLine("PN快速特性清理：" + ex.Message); }
    }
}
