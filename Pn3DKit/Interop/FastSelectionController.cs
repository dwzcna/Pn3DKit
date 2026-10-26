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
    private sealed record EditLease(IReadOnlyList<PnEditContext<ObjectId, ObjectId>> Contexts, Database Database, FastPropertyAccess Access);
    private sealed record CatalogLease(PnPropertyCatalogContext<ObjectId, ObjectId> Context, Database Database,
        FastPropertyAccess Access, IReadOnlyList<PnPropertySpec> Specs);
    private readonly Func<bool> _visible;
    private readonly FastPropertiesView _view;
    private readonly PnPropertyBatch<ObjectId, ObjectId> _batch = new();
    private readonly Dictionary<Guid, EditLease> _tickets = [];
    private readonly Dictionary<ObjectId, PnObjectProperties> _readObjects = [];
    private Document? _document;
    private Database? _database;
    private ObjectId _identity;
    private FastPropertyAccess? _access;
    private IReadOnlyList<PnPropertySpec> _specs = [];
    private ObjectId[] _targets = [];
    private PnObjectProperties? _single;
    private (PnEditRequest Request, EditLease Lease)? _pending;
    private CatalogLease? _catalogPending;
    private bool _dirty = true, _paused, _inside, _disposed, _writing, _applyingValue;
    // 同步写入事件在下一次完整 Capture 中复核，不把句柄作为身份。
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
            view.CatalogRequested += QueueCatalog;
        }
        catch { Dispose(); throw; }
    }
    internal void RequestRefresh() => Invalidate(true);
    private void Invalidate(bool read)
    {
        _epoch++; _dirty = read; _batch.Invalidate(); _access = null; _tickets.Clear(); _readObjects.Clear(); _single = null;
        _view.SetCatalogAvailability(false, "选择或属性状态已变化，等待读取完成。");
    }
    private void PathsChanging() => Invalidate(false);
    private void PathsCommitted() => RequestRefresh();
    private void Changed(object? sender, EventArgs args) => RequestRefresh();
    private void DocumentChanged(object? sender, DocumentCollectionEventArgs args) => RequestRefresh();
    private void CommandChanged(object? sender, CommandEventArgs args) => RequestRefresh();
    /// <summary>同步写入期间的数据库事件要求重新核对；下个目标前全量回读，不让自身刷新使整批误过期。</summary>
    private void ObjectChanged(object? sender, ObjectEventArgs args)
    {
        if (_applyingValue) { _dirty = true; return; } // 包含 SDK 对关联注释的刷新；下一步 Capture 仍核对所有目标。
        RequestRefresh();
    }
    private void ObjectErased(object? sender, ObjectErasedEventArgs args)
    {
        if (_applyingValue) { _dirty = true; return; } // 若目标本身失效，紧接的回读／Capture 会拒绝继续。
        RequestRefresh();
    }

    /// <summary>UI 只排队显式提交，空闲时重新验证；无效票据不会延期自动写入。</summary>
    private void QueueEdit(PnEditRequest request)
    {
        try
        {
            if (_writing || _pending != null || !_tickets.TryGetValue(request.Token, out var lease))
                throw new InvalidOperationException("编辑已过期或正在提交，请刷新后重试。");
            var expected = lease.Contexts[0];
            if (request.RowId != expected.RowId || request.RowRevision != expected.RowRevision
                || request.Path != expected.Path || (lease.Contexts.Count == 1 && request.Original != expected.Original))
                throw new InvalidOperationException("属性路径或原值与读取快照不一致。");
            _pending = (request, lease); _tickets.Clear();
        }
        catch (System.Exception ex) { _notice = "未写入：" + ex.Message; RequestRefresh(); _view.ResumeInputFocus(); _view.ShowStatus(_notice); }
    }

    /// <summary>按钮回调只固定已有快照，SDK 检索推迟到主线程 Idle。</summary>
    private void QueueCatalog()
    {
        try
        {
            if (_disposed || _writing || _pending != null || _catalogPending != null || _dirty || _batch.HasWork
                || _database == null || _access == null || _single == null || _targets.Length != 1
                || string.IsNullOrEmpty(_single.TargetKey))
                throw new InvalidOperationException("请选择一个有效的 Plant 对象，等待读取或提交完成。");
            _catalogPending = new(new(_identity, _targets[0], _epoch, _single.TargetKey, 1),
                _database, _access, _view.ReadSpecs());
            _view.SetCatalogAvailability(false, "正在检索属性。");
        }
        catch (System.Exception ex) { _view.ShowStatus("未检索：" + FastPropertyAccess.Describe(ex)); }
    }

    /// <summary>所有者由 CAD 设置，锁与事务在显示模态窗口前释放，取消不更改属性行。</summary>
    private void ShowCatalog(CatalogLease lease)
    {
        try
        {
            var document = EnsureCatalog(lease);
            IReadOnlyList<PnPropertyCatalogEntry> entries;
            using (document.LockDocument())
            using (var transaction = document.Database.TransactionManager.StartOpenCloseTransaction())
            {
                EnsureCatalog(lease);
                VerifyCatalogLink(lease, document, transaction);
                entries = lease.Access.Catalog(lease.Context.Target);
            }
            EnsureCatalog(lease);
            var window = new PropertyCatalogWindow(entries, lease.Specs.Select(x => x.Input).ToArray(), _view.Resources);
            _view.SuspendInputFocus();
            if (AcadApp.ShowModalWindow(window) != true)
            {
                _notice = "已取消属性检索，未更改面板行。";
                return;
            }
            document = EnsureCatalog(lease);
            using (document.LockDocument())
            using (var transaction = document.Database.TransactionManager.StartOpenCloseTransaction())
                VerifyCatalogLink(lease, document, transaction);
            EnsureCatalog(lease);
            int count = _view.AppendCatalogProperties(window.SelectedProperties);
            _notice = $"已追加 {count} 项属性；保留已有行，未写入对象。";
        }
        catch (System.Exception ex) { _notice = "未追加属性：" + FastPropertyAccess.Describe(ex); }
        finally
        {
            _view.ResumeInputFocus(); RequestRefresh(); _view.ShowStatus(_notice);
        }
    }

    /// <summary>查询和确认后重新核对完整图纸身份、选择、项目、行修订及忙碌状态。</summary>
    private Document EnsureCatalog(CatalogLease lease)
    {
        if (lease.Database.IsDisposed || lease.Context.Identity.IsNull || !lease.Context.Identity.IsValid
            || lease.Context.Identity.IsErased) throw new InvalidOperationException("原图纸数据库已失效或关闭。");
        var document = AcadApp.DocumentManager.MdiActiveDocument
            ?? throw new InvalidOperationException("当前没有活动图纸。");
        var database = document.Database;
        if (database.IsDisposed) throw new InvalidOperationException("当前图纸数据库已失效。");
        var targets = Selection(document);
        var current = new PnPropertyCatalogContext<ObjectId, ObjectId>(database.BlockTableId,
            targets.Length == 1 ? targets[0] : ObjectId.Null, _epoch, lease.Context.TargetKey, targets.Length);
        string? reason = PnPropertyCatalogPolicy.Validate(lease.Context, current, lease.Access.IsCurrent,
            BatchRunGate.IsRunning || !string.IsNullOrEmpty(document.CommandInProgress), !_disposed && _visible());
        if (reason != null) throw new InvalidOperationException(reason);
        if (!_view.ReadSpecs().SequenceEqual(lease.Specs)) throw new InvalidOperationException("属性行已变化，请重新检索。");
        return document;
    }

    /// <summary>重新定位而非只依赖旧窗口的项目行身份；读取自身关联不求值位号。</summary>
    private void VerifyCatalogLink(CatalogLease lease, Document document, Transaction transaction)
    {
        var item = lease.Access.Read(lease.Context.Target, document, transaction, []);
        if (item.TargetKey != lease.Context.TargetKey) throw new InvalidOperationException("Plant 数据关联已变化或无法读取。");
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
                _pending = null; _catalogPending = null; return;
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
                document.Editor.IsQuiescent, _view.HasInputFocus || _catalogPending != null);
            if (busy)
            {
                if (_catalogPending != null) { _catalogPending = null; _notice = "未检索：CAD 或批次忙碌，请空闲后重试。"; }
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
            if (_catalogPending is { } catalog)
            {
                _catalogPending = null; ShowCatalog(catalog); return;
            }
            if (_access != null && !_access.IsCurrent) RequestRefresh();
            if (_dirty)
            {
                long epoch = _epoch;
                var targets = Selection(document);
                if (epoch != _epoch || !SameDatabase(database, identity)) return;
                _specs = _view.ReadSpecs(); _dirty = false; _targets = targets; _readObjects.Clear();
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
                    var item = access.Read(id, document, transaction, _specs);
                    _readObjects[id] = item;
                    if (_targets.Length == 1) _single = item;
                    return item;
                });
            }
            if (currentEpoch != _epoch || !SameDatabase(database, identity) || !access.IsCurrent)
            { RequestRefresh(); return; }
            if (snapshot != null)
            {
                var cells = snapshot.Cells.Select(cell =>
                {
                    if (!cell.Permission.Allowed || cell.Value.Kind is not (FastValueKind.Value or FastValueKind.Mixed)) return cell;
                    string? path = PnPropertyPolicy.Normalize(cell.Spec.Input, out _);
                    if (path == null || _targets.Any(id => !_readObjects.ContainsKey(id))) return cell;
                    var contexts = _targets.Select(id =>
                    {
                        var item = _readObjects[id];
                        var field = item.Cells.First(x => x.Spec == cell.Spec);
                        return new PnEditContext<ObjectId, ObjectId>(identity, id, _epoch, cell.Spec.Id,
                            cell.Spec.Revision, path, item.TargetKey, field.Value.Raw, _targets.Length, field.Permission);
                    }).ToArray();
                    Guid token = Guid.NewGuid();
                    _tickets[token] = new(contexts, database, access);
                    return cell with { EditToken = token };
                }).ToArray();
                snapshot = snapshot with { Cells = cells };
                _view.Show(snapshot with { Status = snapshot.Status + Notice() });
                bool canCatalog = snapshot.Count == 1 && !string.IsNullOrEmpty(_single?.TargetKey);
                _view.SetCatalogAvailability(canCatalog, canCatalog
                    ? "检索此对象自身的全部 Plant 属性，勾选后追加到面板。"
                    : "请选择一个有效的 Plant 对象；无选择、多选或非 Plant 对象不可检索。");
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

    /// <summary>租约覆盖全部目标预检、同步写入及回读；主线程应用上下文不启动命令或清除 PICKFIRST。</summary>
    private async void WritePending(PnEditRequest request, EditLease edit)
    {
        if (_disposed) return;
        using var lease = BatchRunGate.TryEnter();
        if (lease == null) { _notice = "未写入：已有 Pn3DKit 批次。"; RequestRefresh(); _view.ResumeInputFocus(); return; }
        _writing = true;
        try
        {
            var host = new AutoCadHost(() => !_disposed && edit.Access.IsCurrent);
            var drawing = host.OriginalDrawing ?? throw new InvalidOperationException("当前没有活动图纸。");
            var result = await host.InLockedDocumentAsync(drawing, document =>
            {
                EnsureEdit(edit);
                var spec = _view.ReadSpecs().FirstOrDefault(x => x.Id == request.RowId)
                    ?? throw new InvalidOperationException("属性行已删除。");
                var adapter = new SelectionEditHost(this, edit, document, spec, new FastPropertyAccess(busy: false));
                var outcome = PnPropertyEditBatch.Run(edit.Contexts, request.Value, adapter);
                if (outcome.Items.Any(x => x.State is PnPropertyWriteState.Updated or PnPropertyWriteState.Failed))
                {
                    try
                    {
                        if (!SameDatabase(edit.Database, edit.Contexts[0].Identity) || !edit.Access.IsCurrent)
                            throw new InvalidOperationException("图纸或项目已变化。");
                        document.Editor.Regen();
                    }
                    catch (System.Exception ex)
                    { outcome = outcome with { Message = outcome.Message + "\n图形刷新失败：" + FastPropertyAccess.Describe(ex) }; }
                }
                return outcome;
            });
            _notice = request.Path + "：" + result.Details + "\n未保存图纸。";
        }
        catch (System.Exception ex) { _notice = "提交失败，未继续后续对象：" + FastPropertyAccess.Describe(ex); }
        finally
        {
            _applyingValue = false; _writing = false;
            try
            {
                RequestRefresh();
                if (!_disposed) { _view.ResumeInputFocus(); _view.ShowStatus(_notice); }
            }
            catch (System.Exception ex) { System.Diagnostics.Trace.WriteLine(ex); }
        }
    }

    /// <summary>回调及写入前核对原数据库、项目、轮次和属性行；选择集合由宿主逐次核对。</summary>
    private void EnsureEdit(EditLease edit)
    {
        var first = edit.Contexts[0];
        if (_disposed || !_visible()) throw new InvalidOperationException("面板已关闭或隐藏，未继续提交。");
        if (!SameDatabase(edit.Database, first.Identity)) throw new InvalidOperationException("图纸身份已变化或原图已关闭。");
        if (_epoch != first.Epoch || !edit.Access.IsCurrent) throw new InvalidOperationException("选择、对象、路径或项目已变化，编辑已过期。");
        if (!string.IsNullOrEmpty(AcadApp.DocumentManager.MdiActiveDocument!.CommandInProgress))
            throw new InvalidOperationException("CAD 正在执行命令，未继续提交。");
        var spec = _view.ReadSpecs().FirstOrDefault(x => x.Id == first.RowId);
        if (spec == null || spec.Revision != first.RowRevision || PnPropertyPolicy.Normalize(spec.Input, out _) != first.Path)
            throw new InvalidOperationException("属性路径已变化，编辑已过期。");
    }

    /// <summary>在协调器持有的同一文档锁内逐次重新读取所有目标；不复用聚合后的显示值作为原值。</summary>
    private sealed class SelectionEditHost(FastSelectionController owner, EditLease edit, Document document,
        PnPropertySpec spec, FastPropertyAccess access) : IPnPropertyEditHost<ObjectId, ObjectId>
    {
        private void EnsureSelection()
        {
            owner.EnsureEdit(edit);
            var selected = Selection(document);
            if (selected.Length != edit.Contexts.Count || !selected.ToHashSet().SetEquals(edit.Contexts.Select(x => x.Target)))
                throw new InvalidOperationException("选择对象已变化，编辑已过期。");
        }

        public IReadOnlyList<PnEditContext<ObjectId, ObjectId>> Capture()
        {
            EnsureSelection();
            using var transaction = document.Database.TransactionManager.StartOpenCloseTransaction();
            return edit.Contexts.Select(expected =>
            {
                var item = access.Read(expected.Target, document, transaction, [spec]);
                var cell = item.Cells[0];
                var permission = cell.Value.Kind == FastValueKind.Value ? cell.Permission
                    : PnEditPermission.ReadOnly(cell.Value.Detail ?? "属性已不存在或无法读取。");
                return new PnEditContext<ObjectId, ObjectId>(document.Database.BlockTableId, expected.Target, owner._epoch,
                    spec.Id, spec.Revision, PnPropertyPolicy.Normalize(spec.Input, out _) ?? "", item.TargetKey,
                    cell.Value.Raw, edit.Contexts.Count, permission);
            }).ToArray();
        }

        public PnPropertyWritePlan<ObjectId> Prepare(PnEditContext<ObjectId, ObjectId> target, string value)
        {
            EnsureSelection();
            try { return access.Prepare(target.Target, document, spec, target.Original, value); }
            catch (System.Exception ex) { throw new InvalidOperationException(Label(target.Target) + "：" + FastPropertyAccess.Describe(ex), ex); }
        }

        public string Write(PnPropertyWritePlan<ObjectId> plan)
        {
            try
            {
                return access.Write(plan.Target, document, spec, plan.Original, plan.Value, key =>
                {
                    EnsureSelection();
                    if (key != plan.DataKey) throw new InvalidOperationException("写入前 Plant 数据关联已变化。");
                    owner._applyingValue = true;
                });
            }
            finally { owner._applyingValue = false; }
        }

        public FastPropertyValue ReadActual(ObjectId target)
        {
            try
            {
                owner.EnsureEdit(edit);
                using var transaction = document.Database.TransactionManager.StartOpenCloseTransaction();
                return access.Read(target, document, transaction, [spec]).Cells[0].Value;
            }
            catch (System.Exception ex) { return FastPropertyValue.Error(FastPropertyAccess.Describe(ex)); }
        }

        public string Label(ObjectId target) => FastPropertyAccess.Label(target);
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
        Unbind(); RequestRefresh(); _dirty = false; _pending = null; _catalogPending = null;
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
        _disposed = true; _pending = null; _catalogPending = null;
        Try(() => AcadApp.Idle -= Idle); Try(() => AcadApp.DocumentManager.DocumentActivated -= DocumentChanged);
        Try(() => AcadApp.DocumentManager.DocumentToBeDestroyed -= DocumentChanged);
        _view.RowsChanging -= PathsChanging; _view.RowsCommitted -= PathsCommitted; _view.EditRequested -= QueueEdit;
        _view.CatalogRequested -= QueueCatalog;
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
