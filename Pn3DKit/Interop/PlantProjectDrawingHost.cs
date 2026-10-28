using Autodesk.AutoCAD.ApplicationServices;
using Pn3DKit.Commands;
using Pn3DKit.Models;
using Pn3DKit.Services;

namespace Pn3DKit.Interop;

/// <summary>项目图纸模式适配器：应用上下文协调文档，命令上下文内受控写入。</summary>
internal sealed class PlantProjectDrawingHost(AutoCadHost cad, PlantProjectTagAccess project) : IProjectTagDrawingHost
{
    /// <inheritdoc />
    public ICadDrawing? OriginalDrawing => cad.OriginalDrawing;
    /// <inheritdoc />
    public void EnsureProject() => cad.EnsureProject();
    /// <inheritdoc />
    public void CheckContext() { EnsureProject(); TagCheckCommands.CheckBreak(); }
    /// <inheritdoc />
    public ICadDrawing? FindOpen(string source) => cad.FindOpen(source);
    /// <inheritdoc />
    public Task<ICadDrawing> OpenAsync(string source) => cad.OpenAsync(source);
    /// <inheritdoc />
    public Task ActivateAsync(ICadDrawing drawing) => cad.ActivateAsync(drawing);
    /// <inheritdoc />
    public Task<DrawingState> ReadStateAsync(ICadDrawing drawing) => cad.ReadStateAsync(drawing);
    /// <inheritdoc />
    public Task SaveAsync(ICadDrawing drawing, string marker) => cad.SaveAsync(drawing, marker);
    /// <inheritdoc />
    public Task ExportAsync(ICadDrawing drawing, string temporary, string version, string marker) =>
        throw new NotSupportedException("位号重建不导出图纸。");
    /// <inheritdoc />
    public Task CloseAsync(ICadDrawing drawing) => cad.CloseAsync(drawing);
    /// <inheritdoc />
    public Task<bool> HasPendingDataAsync(ICadDrawing drawing) =>
        InDrawingAsync(drawing, _ => project.HasPendingChanges());

    /// <inheritdoc />
    public Task<TagRebuildBatchResult> RebuildAsync(ICadDrawing drawing, IReadOnlyList<TagRebuildPlan> work,
        IReadOnlyList<TagRebuildRecord> synchronize, ITagRebuildJournal journal) =>
        InDrawingAsync(drawing, document =>
        {
            PlantTagRebuildAccess.EnsureWritable(document);
            var db = document.Database;
            var scan = PlantTagCatalogAccess.Read(db, project.EnsureContext);
            var local = new PlantTagRebuildAccess(document, scan.IsCurrent);
            local.IndexTargets(scan);
            var found = scan.Targets.Select(x => x.Key).ToHashSet();
            foreach (var previous in synchronize)
            {
                project.Current(previous.Item.Key);
                if (!found.Contains(previous.Item.Key))
                    throw new InvalidOperationException("共享数据行已不在此图纸范围：" + previous.Item.Key);
                var c = project.Current(previous.Item.Key).Context;
                string actual = PlantTagRebuildAccess.ReadTag(c);
                if (!string.Equals(actual, previous.Item.Check.ExpectedTag, StringComparison.Ordinal))
                    throw new InvalidOperationException("共享行回读与已更新结果不符：" + previous.Item.Key);
                journal.Message("共享行验证：" + previous.Item.Key + "；Tag=" + actual + "；仅刷新，不重复写入");
            }
            // 图纸上下文结果必须与已确认项目快照相符，不扩大用户授权的目标。
            var plans = work.Select(p => found.Contains(p.Item.Key) ? p
                : p with { State = TagRebuildState.Unavailable, Reason = "原关联在本图模型空间未找到，请重新预检" }).ToArray();
            var result = TagRebuildBatch.Run(plans, new GlobalConflictHost(local, project), journal,
                (done, total) => TagCheckCommands.WriteSafely($"\n本图位号重建：{done}/{total}"));
            if (synchronize.Count != 0 && result.SafeToSave)
            {
                local.Regen();
                journal.Message("已验证并 Regen 共享行关联图形；实际注释显示待 CAD 验收。");
            }
            return result;
        });

    /// <summary>每次操作独立绑定目标图纸，包含未执行重建的只读图关闭前检查。</summary>
    /// <param name="drawing">本次同步操作的目标图纸。</param>
    /// <param name="action">已进入目标文档命令上下文的同步操作。</param>
    /// <returns>操作结果；异常仍在恢复原 Guard 后传播。</returns>
    private Task<T> InDrawingAsync<T>(ICadDrawing drawing, Func<Document, T> action) =>
        cad.InDocumentAsync(drawing, document =>
        {
            var db = document.Database;
            var identity = db.BlockTableId;
            using var scope = project.UseGuard(() =>
            {
                TagCheckCommands.EnsureDocument(db, identity);
                TagCheckCommands.CheckBreak();
            });
            project.EnsureContext();
            return action(document);
        });

    /// <summary>图纸写入沿用本图撤销与校验，冲突仍使用完整项目范围。</summary>
    private sealed class GlobalConflictHost(PlantTagRebuildAccess local, PlantProjectTagAccess project) : ITagRebuildHost
    {
        /// <inheritdoc />
        public void EnsureContext() { project.EnsureContext(); local.EnsureContext(); }
        /// <inheritdoc />
        public void BeginUndo() => local.BeginUndo();
        /// <inheritdoc />
        public void EndUndo() => local.EndUndo();
        /// <inheritdoc />
        public string? Revalidate(TagRebuildCandidate item) => project.Revalidate(item) ?? local.Revalidate(item);
        /// <inheritdoc />
        public IReadOnlyList<string> FindOwners(TagRebuildCandidate item) => project.FindOwners(item);
        /// <inheritdoc />
        public string WriteAndRead(TagRebuildCandidate item)
        {
            string actual = local.WriteAndRead(item);
            project.RecordWrite(item, actual);
            return actual;
        }
        /// <inheritdoc />
        public void Regen() => local.Regen();
    }
}
