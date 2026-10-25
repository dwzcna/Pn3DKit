using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Runtime;
using Pn3DKit.Interop;
using Pn3DKit.Models;
using Pn3DKit.Services;
using Pn3DKit.Utils;
using AcadApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;

[assembly: CommandClass(typeof(Pn3DKit.Commands.TagRebuildCommands))]

namespace Pn3DKit.Commands;

/// <summary>在会话入口协调位号预检、默认否确认、单撤销组写入和异步 QSAVE；预检不保证无副作用。</summary>
public sealed class TagRebuildCommands
{
    /// <summary>按当前图纸类别重建 PnId.Tag；异步入口捕获所有回调异常，租约覆盖保存及日志收尾。</summary>
    [CommandMethod("REBUILDPNTAGBYCLASS", CommandFlags.Session)]
    public async void RebuildPnTagByClass()
    {
        using var lease = BatchRunGate.TryEnter();
        if (lease == null) { Write("已有 Pn3DKit 批次正在运行。"); return; }
        TagRebuildJournal? journal = null;
        IReadOnlyList<TagRebuildRecord> final = [];
        string status = "未执行";
        var files = new RunLogFiles();
        bool issues = false;
        int total = 0;
        try
        {
            var originalDocument = AcadApp.DocumentManager.MdiActiveDocument;
            if (originalDocument == null) { Write("当前没有活动图纸。"); return; }
            var database = originalDocument.Database;
            var identity = database.BlockTableId;
            Func<bool> isCurrent = () => true;
            var host = new AutoCadHost(() => isCurrent(), files);
            var original = host.OriginalDrawing!;
            bool save = await host.InDocumentAsync(original, document =>
            {
                void EnsureDocument() => TagCheckCommands.EnsureDocument(database, identity);
                EnsureDocument();
                Write(TagEvaluationNotice.Text);
                PlantTagRebuildAccess.EnsureWritable(document);
                var scan = PlantTagCheckAccess.Scan(database, () =>
                { EnsureDocument(); TagCheckCommands.CheckBreak(); });
                isCurrent = scan.IsCurrent;
                var access = new PlantTagRebuildAccess(document, scan.IsCurrent);
                access.EnsureContext();
                var groups = SelectGroups(document.Editor, scan, access.EnsureContext);
                if (groups == null || groups.Length == 0) return false;
                string documents = RunPaths.Documents();
                string? directory = TagCheckCommands.SelectDirectory(document.Editor,
                    TagRebuildPaths.Suggest(documents, DateTime.Now), access.EnsureContext,
                    TagRebuildPaths.Validate, "位号重建");
                if (directory == null) return false;
                access.EnsureContext();
                journal = new TagRebuildJournal(directory,
                    string.IsNullOrWhiteSpace(database.Filename) ? document.Name : database.Filename,
                    groups.Select(g => g.ClassName).Distinct(), files);
                Write("日志：" + journal.LogPath + "\nCSV：" + journal.CsvPath);
                issues |= scan.Warnings.Count != 0;
                foreach (string warning in scan.Warnings) journal.Message("扫描警告：" + warning);
                var selected = groups.SelectMany(g => g.Targets).DistinctBy(x => x.Key).ToArray();
                total = selected.Length;
                var candidates = new List<TagRebuildCandidate>();
                IReadOnlyList<TagRebuildPlan>? plans = null;
                try
                {
                    access.IndexTargets(scan);
                    foreach (var target in selected)
                    {
                        access.EnsureContext();
                        var item = access.ReadCandidate(target);
                        if (candidates.Any(x => x.Key == item.Key)) continue;
                        candidates.Add(item);
                        string? unavailable = TagRebuildPolicy.UnavailableReason(item);
                        journal.Append(new(item, unavailable == null && item.Check.Status == TagCheckStatus.Mismatch
                            ? TagRebuildAction.Pending : TagRebuildAction.Skipped,
                            unavailable ?? (item.Check.Status == TagCheckStatus.Consistent ? "位号一致" : "预检中，等待冲突检查")));
                        if (candidates.Count % 50 == 0) Write($"位号预检进度：{candidates.Count} 个数据行");
                    }
                    var limited = candidates.Where(x => x.Registry.Length == 0
                        && TagRebuildPolicy.UnavailableReason(x) == null && x.Check.Status == TagCheckStatus.Mismatch).ToArray();
                    if (limited.Length != 0)
                    {
                        string warning = $"注意：{limited.Length} 个目标未配置可用位号注册表，冲突检查仅覆盖本图模型空间，不能排除项目其他图纸的占用。";
                        Write(warning);
                        journal.Message(warning);
                    }
                    plans = TagRebuildPolicy.Prepare(candidates, access.FindOwners, access.EnsureContext);
                    foreach (var plan in plans) journal.Append(TagRebuildPolicy.Preview(plan));
                    foreach (var group in plans.GroupBy(x => x.Item.Check.Subject.ClassName))
                    {
                        string summary = group.Key + "：一致 " + group.Count(x => x.State == TagRebuildState.Consistent)
                            + "／可重建 " + group.Count(x => x.State == TagRebuildState.Ready)
                            + "／不可重建 " + group.Count(x => x.State == TagRebuildState.Unavailable)
                            + "／冲突 " + group.Count(x => x.State == TagRebuildState.Conflict);
                        Write(summary); journal.Message("预检：" + summary);
                    }
                    var ready = plans.Where(x => x.State == TagRebuildState.Ready).ToArray();
                    foreach (var plan in ready.Take(20))
                        Write(plan.Item.Check.Subject.ExistingTag + " → " + plan.Item.Check.ExpectedTag
                            + "（" + plan.Item.Check.Subject.Identity + "）");
                    if (ready.Length > 20) Write("其余预览请查看 CSV。");
                    access.EnsureContext();
                    if (ready.Length == 0)
                    {
                        final = plans.Select(TagRebuildPolicy.Preview).ToArray();
                        status = "预检完成，无可重建对象";
                        Write(status);
                        return false;
                    }
                    foreach (var groupTarget in ready.Where(x => x.Item.Check.Subject.Kind == TagTargetKind.LineGroup))
                    {
                        string range = "管线组写入范围"
                            + (groupTarget.Item.Check.Subject.Drawings.Count > 1 ? "（跨图纸共享组）：" : "：")
                            + groupTarget.Item.Check.Subject.Identity
                            + "；只写组自身 Tag，不扩展为重建未选择的成员";
                        Write(range); journal.Message(range);
                    }
                    Write("预检已写入 CSV，可先以只读方式打开核对；同项目部分及数据行ID的最后一行为最终动作。");
                    Write("本次计划用一个 UNDO 组组织写入；Plant 数据缓存是否随 UNDO 回退尚待实机确认，必要时请用日志恢复。默认不写入、不保存。");
                    if (!Confirm(document.Editor, $"重建 {ready.Length} 个位号？", access.EnsureStable))
                    {
                        journal.Message("重建确认：否或取消；未写入");
                        final = TagRebuildPolicy.Decline(plans, journal, "用户未确认重建");
                        status = "仅预检，用户未确认重建";
                        return false;
                    }
                    journal.Message("重建确认：是");
                    access.EnsureContext();
                    access.RefreshDrawingIndex();
                    // 确认后再次预检全部候选，避免预览期间新占用或占用链只跳过一端。
                    var refreshed = TagRebuildPolicy.Prepare(candidates, access.FindOwners, access.EnsureContext);
                    plans = TagRebuildPolicy.RestrictToPreview(plans, refreshed);
                    foreach (var plan in plans.Where(x => x.State != TagRebuildState.Ready))
                        journal.Append(TagRebuildPolicy.Preview(plan));
                    var result = TagRebuildBatch.Run(plans, access, journal,
                        (done, total) => Write($"位号重建进度：{done}/{total}"));
                    final = result.Records;
                    issues |= result.StopReason != null;
                    status = result.StopReason ?? "重建完成";
                    Write(status);
                    foreach (var group in final.GroupBy(x => x.Item.Check.Subject.ClassName))
                        Write(group.Key + "：" + TagRebuildPolicy.Summary(group));
                    if (!result.Attempted || !result.SafeToSave) return false;
                    access.EnsureStable();
                    Write("QSAVE 会保存本图所有修改，包括重建前的已有修改。选择否将保留当前未保存状态。");
                    bool confirmed = Confirm(document.Editor, "重建完成，保存图纸？", access.EnsureStable);
                    journal.Message("保存选择：" + (confirmed ? "是" : "否或取消，未保存"));
                    return confirmed;
                }
                catch (System.Exception ex)
                {
                    issues = true;
                    status = (ex is OperationCanceledException ? "已中断：" : "已停止：") + ex.Message;
                    if (final.Count == 0)
                    {
                        var results = plans == null
                            ? candidates.Select(item =>
                            {
                                string? reason = TagRebuildPolicy.UnavailableReason(item);
                                return reason != null || item.Check.Status == TagCheckStatus.Consistent
                                    ? new TagRebuildRecord(item, TagRebuildAction.Skipped, reason ?? "位号一致")
                                    : new TagRebuildRecord(item, TagRebuildAction.Unexecuted, status);
                            }).ToList()
                            : plans.Select(plan => plan.State == TagRebuildState.Ready
                                ? new TagRebuildRecord(plan.Item, TagRebuildAction.Unexecuted, status)
                                : TagRebuildPolicy.Preview(plan)).ToList();
                        var completedKeys = candidates.Select(x => x.Key).ToHashSet();
                        foreach (var target in selected.Where(x => !completedKeys.Contains(x.Key)))
                        {
                            var item = new TagRebuildCandidate(target.Key,
                                new(target.Subject, "", TagCheckStatus.Error, [], status));
                            results.Add(new(item, TagRebuildAction.Unexecuted, "预检未完成：" + status));
                        }
                        final = results;
                        foreach (var record in final) journal.Append(record);
                    }
                    journal.Message(status);
                    Write(status);
                    return false;
                }
            });
            if (save)
            {
                string internalDirectory = Path.Combine(Path.GetDirectoryName(journal!.LogPath)!, "_internal");
                files.EnsureDirectory(internalDirectory);
                await TagRebuildSave.RunAsync(true, true,
                    () => host.SaveAsync(original, Path.Combine(internalDirectory, "save-" + Guid.NewGuid().ToString("N") + ".done")),
                    async () => (await host.ReadStateAsync(original)).Modified, journal);
                Write("图纸已保存。");
            }
        }
        catch (System.Exception ex)
        {
            issues = true;
            status = "重建未完成：" + ex.Message;
            Write(status + (ex is FileNotFoundException or FileLoadException or TypeLoadException
                ? "；请在 AutoCAD Plant 3D 2026 中打开项目后运行。" : ""));
            try { journal?.Message(status); }
            catch (System.Exception logError) { Write("日志写入失败：" + logError.Message); }
        }
        finally
        {
            if (journal != null)
            {
                try { journal.Complete(final, status); }
                catch (System.Exception ex) { issues = true; Write("日志收尾失败：" + ex.Message); }
                try { journal.Dispose(); }
                catch (System.Exception ex) { issues = true; Write("日志关闭失败：" + ex.Message); }
                Write("合计：" + TagRebuildPolicy.Summary(final));
                Write("日志：" + journal.LogPath + "\nCSV：" + journal.CsvPath);
            }
            await LogRetentionPrompt.FinishAsync(files,
                issues || LogRetentionPolicy.RebuildHasIssues(final, total));
        }
    }

    /// <summary>复用扫描类别和编号规则，无格式类别不编号，格式读取错误保留为可预检项。</summary>
    private static PlantTagClassGroup[]? SelectGroups(Editor editor, PlantTagScan scan, Action ensure)
    {
        var skipped = scan.Groups.Where(g => !g.HasFormat && g.FormatError == null).ToArray();
        if (skipped.Length != 0)
            Write("未配置位号格式的类别已跳过：" + string.Join("、", skipped.Select(x => x.DisplayName + " " + x.Targets.Count + x.Unit))
                + $"（共 {skipped.Select(x => x.ClassName).Distinct().Count()} 类，{skipped.Sum(x => x.Targets.Count)} 个目标）");
        foreach (string warning in scan.Warnings.Take(20)) Write(warning);
        var groups = scan.Groups.Where(g => g.HasFormat || g.FormatError != null).ToArray();
        if (groups.Length == 0)
        {
            foreach (string diagnostic in scan.Diagnostics) Write("扫描诊断：" + diagnostic);
            Write("当前模型空间没有可预检的位号类别。");
            return null;
        }
        for (int i = 0; i < groups.Length; i++)
            Write($"{i + 1} - {groups[i].DisplayName} [{groups[i].PartName}] ({groups[i].Targets.Count}{groups[i].Unit})");
        while (true)
        {
            ensure();
            var input = editor.GetString(new PromptStringOptions("\n请选择类别编号 [如 1,3,5-7；*=全部] <0>: ")
            { AllowSpaces = true });
            ensure();
            if (input.Status is not (PromptStatus.OK or PromptStatus.None)) return null;
            try
            {
                var numbers = TagCheckPolicy.ParseClasses(input.Status == PromptStatus.None ? "" : input.StringResult, groups.Length);
                if (numbers.Length == 0) Write("未选择类别，未执行重建");
                return numbers.Select(number => groups[number - 1]).ToArray();
            }
            catch (ArgumentException ex) { Write(ex.Message); }
        }
    }

    /// <summary>可点击中文是／否，键盘保持 Y/N，默认 N；交互前后核对原图纸及项目。</summary>
    private static bool Confirm(Editor editor, string message, Action ensure)
    {
        ensure();
        var prompt = new PromptKeywordOptions("\n" + message);
        prompt.Keywords.Add("Y", "Y", "是(Y)");
        prompt.Keywords.Add("N", "N", "否(N)");
        prompt.Keywords.Default = "N";
        prompt.AllowNone = true;
        var result = editor.GetKeywords(prompt);
        ensure();
        return result.Status == PromptStatus.OK && TagRebuildPolicy.IsYes(result.StringResult);
    }

    /// <summary>通过当前可用 Editor 容错输出，不持有可能已经关闭的图纸编辑器。</summary>
    private static void Write(string message) => TagCheckCommands.WriteSafely("\n" + message);
}
