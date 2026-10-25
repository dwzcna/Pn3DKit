using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Runtime;
using Pn3DKit.Interop;
using Pn3DKit.Models;
using Pn3DKit.Services;
using Pn3DKit.Utils;
using AcadApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;

[assembly: CommandClass(typeof(Pn3DKit.Commands.ProjectTagCommands))]

namespace Pn3DKit.Commands;

/// <summary>按项目实际使用的数据行执行类别检查和预检；租约覆盖异步图纸操作与日志收尾。</summary>
public sealed class ProjectTagCommands
{
    /// <summary>检查全项目且不主动打开 DWG；SDK 求值可能更新序列字段，不保证项目数据不变。</summary>
    [CommandMethod("CHECKPROJECTPNTAGBYCLASS", CommandFlags.Session)]
    public async void CheckProjectPnTagByClass() => await RunAsync(false);

    /// <summary>全项目预检后选择数据提交或逐图重建，默认 N 明确表示修改项目数据。</summary>
    [CommandMethod("REBUILDPROJECTPNTAGBYCLASS", CommandFlags.Session)]
    public async void RebuildProjectPnTagByClass() => await RunAsync(true);

    /// <summary>已确认的逐图工作，不携带未选择的数据行。</summary>
    private sealed record DrawingWork(PlantProjectTagAccess Access, IReadOnlyList<TagRebuildPlan> Plans, bool Save);

    /// <summary>所有 CAD 回调内部的异常均转换为任务，最外层记录并保留现场。</summary>
    private static async Task RunAsync(bool rebuild)
    {
        using var lease = BatchRunGate.TryEnter();
        if (lease == null) { Write("已有 Pn3DKit 批次正在运行。"); return; }
        ProjectTagJournal? journal = null;
        string status = "未执行";
        var files = new RunLogFiles();
        bool issues = false;
        var checkedItems = new List<TagCheckResult>();
        var candidates = new List<TagRebuildCandidate>();
        IReadOnlyList<TagRebuildPlan> plans = [];
        IReadOnlyList<TagRebuildRecord> final = [];
        int total = 0;
        try
        {
            var document = AcadApp.DocumentManager.MdiActiveDocument;
            if (document == null) { Write("当前没有活动图纸。"); return; }
            var db = document.Database;
            var identity = db.BlockTableId;
            Func<bool> isCurrent = () => true;
            var cad = new AutoCadHost(() => isCurrent(), files);
            var original = cad.OriginalDrawing!;
            DrawingWork? work = await cad.InDocumentAsync(original, current =>
            {
                void Ensure()
                {
                    TagCheckCommands.EnsureDocument(db, identity);
                    if (!isCurrent()) throw new InvalidOperationException("当前 Plant 项目已变化，停止批次。");
                    TagCheckCommands.CheckBreak();
                }
                Ensure();
                Write(TagEvaluationNotice.Text);
                var scan = PlantTagCatalogAccess.Read(null, Ensure);
                isCurrent = scan.IsCurrent;
                var groups = SelectGroups(current.Editor, scan, Ensure, rebuild);
                if (groups.Length == 0) return null;
                string documents = RunPaths.Documents();
                string? directory = TagCheckCommands.SelectDirectory(current.Editor,
                    ProjectTagPolicy.Suggest(documents, DateTime.Now, rebuild), Ensure,
                    path => ProjectTagPolicy.Validate(path, rebuild), rebuild ? "项目位号重建" : "项目位号检查");
                if (directory == null) return null;
                Ensure();
                journal = new ProjectTagJournal(directory, rebuild, PlantProjectAccess.Read().Catalog.RootDirectory,
                    groups.Select(g => g.PartName + "/" + g.ClassName), files);
                Write("日志：" + journal.LogPath + "\nCSV：" + journal.CsvPath);
                issues |= scan.Warnings.Count != 0;
                foreach (string warning in scan.Warnings) journal.Message("扫描警告：" + warning);
                var targets = groups.SelectMany(g => g.Targets).DistinctBy(x => x.Key).ToArray();
                total = targets.Length;
                if (!rebuild)
                {
                    foreach (var target in targets)
                    {
                        Ensure();
                        var result = PlantTagCheckAccess.Check(target);
                        checkedItems.Add(result);
                        journal.AppendCheck(result);
                        if (checkedItems.Count % 50 == 0) Write($"项目位号检查：{checkedItems.Count}/{total}");
                    }
                    status = "检查完成";
                    return null;
                }
                var access = new PlantProjectTagAccess(scan, Ensure);
                foreach (var target in targets)
                {
                    Ensure();
                    var item = access.Candidate(target);
                    candidates.Add(item);
                    string? unavailable = TagRebuildPolicy.UnavailableReason(item);
                    journal.Append(new(item, unavailable == null && item.Check.Status == TagCheckStatus.Mismatch
                        ? TagRebuildAction.Pending : TagRebuildAction.Skipped,
                        unavailable ?? (item.Check.Status == TagCheckStatus.Consistent ? "位号一致" : "预检中")));
                    if (candidates.Count % 50 == 0) Write($"项目预检：{candidates.Count}/{total}");
                }
                plans = TagRebuildPolicy.Prepare(candidates, access.FindOwners, Ensure);
                foreach (var plan in plans) journal.Append(TagRebuildPolicy.Preview(plan));
                foreach (var group in plans.GroupBy(x => x.Item.Check.Subject.ProjectPart + "/" + x.Item.Check.Subject.ClassName))
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
                var jobs = ProjectTagPolicy.DrawingJobs(plans);
                Write("需要重建的完整图纸清单（" + jobs.Count + " 张）：");
                journal.Message("需要重建的完整图纸清单：");
                foreach (var job in jobs) { Write(job.Path); journal.Message(job.Path); }
                foreach (var plan in ready.Where(x => x.Item.Check.Subject.Kind == TagTargetKind.LineGroup))
                {
                    string range = "管线组范围" + (plan.Item.Check.Subject.Drawings.Count > 1 ? "（跨图纸共享）：" : "：")
                        + plan.Item.Check.Subject.Identity + "；仅写组自身 Tag，不扩展未选择成员";
                    Write(range); journal.Message(range);
                }
                if (ready.Length == 0)
                { final = plans.Select(TagRebuildPolicy.Preview).ToArray(); status = "预检完成，无可重建对象"; return null; }
                Write("预检已写入 CSV。N／回车会修改并提交项目数据，不打开 DWG；注释可能尚未刷新，DWG UNDO 不保证恢复项目数据，请按日志恢复。");
                journal.Message("默认 N 将提交项目数据；不承诺 DWG UNDO 回退。Y 每图独立撤销组，也不承诺跨图纸一次撤销。");
                var mode = SelectMode(current.Editor, Ensure);
                journal.Message("重建方式：" + mode);
                if (mode == ProjectTagMode.Cancel)
                {
                    final = TagRebuildPolicy.Decline(plans, journal, "用户取消，保留预检");
                    status = "仅预检，已取消"; return null;
                }
                access.RefreshIndex();
                plans = TagRebuildPolicy.RestrictToPreview(plans,
                    TagRebuildPolicy.Prepare(candidates, access.FindOwners, Ensure));
                if (mode == ProjectTagMode.Data)
                {
                    var result = ProjectTagDataBatch.Run(plans, access, journal,
                        (done, count) => Write($"项目数据重建：{done}/{count}"));
                    final = result.Records; status = result.StopReason ?? "项目数据重建完成；未保存 DWG";
                    issues |= result.StopReason != null;
                    return null;
                }
                Write("统一保存选择：Y 执行 QSAVE，包含图纸原有修改；默认不保存，全部修改图纸保持打开。");
                bool save = ConfirmSave(current.Editor, Ensure);
                journal.Message("统一保存选择：" + (save ? "Y" : "N，不保存，保留全部修改图纸"));
                return new DrawingWork(access, plans, save);
            });
            if (work != null)
            {
                var result = await ProjectTagDrawingBatch.RunAsync(work.Plans, work.Save,
                    Path.GetDirectoryName(journal!.LogPath)!, new PlantProjectDrawingHost(cad, work.Access), journal,
                    (done, count) => Write($"项目图纸处理：{done}/{count}"), files);
                final = result.Records; status = result.StopReason ?? "项目图纸重建完成";
                issues |= result.StopReason != null || result.Drawings.Any(x => x.HasIssue);
                foreach (var drawing in result.Drawings) Write(drawing.Path + "：" + drawing.Status + "；" + drawing.Reason);
            }
        }
        catch (System.Exception ex)
        {
            issues = true;
            status = (ex is OperationCanceledException ? "已中断：" : "已停止：") + PlantProjectTagAccess.Error(ex);
            Write(status);
            if (rebuild && final.Count == 0)
            {
                final = plans.Count != 0 ? plans.Select(p => p.State == TagRebuildState.Ready
                    ? new TagRebuildRecord(p.Item, TagRebuildAction.Unexecuted, status) : TagRebuildPolicy.Preview(p)).ToArray()
                    : candidates.Select(c => new TagRebuildRecord(c, TagRebuildAction.Unexecuted, "预检未完成：" + status)).ToArray();
                foreach (var record in final)
                    try { journal?.Append(record); } catch (System.Exception) { break; }
            }
        }
        finally
        {
            if (journal != null)
            {
                try
                {
                    Write(status); journal.Message(status);
                    if (rebuild)
                    {
                        foreach (var group in final.GroupBy(x => x.Item.Check.Subject.ProjectPart + "/" + x.Item.Check.Subject.ClassName))
                        { string line = group.Key + "：" + TagRebuildPolicy.Summary(group); Write(line); journal.Message(line); }
                        string summary = TagRebuildPolicy.Summary(final) + "；预检未完成 " + Math.Max(0, total - candidates.Count);
                        Write(summary); journal.Message(summary);
                    }
                    else
                    {
                        foreach (var group in checkedItems.GroupBy(x => x.Subject.ProjectPart + "/" + x.Subject.ClassName))
                        {
                            string line = group.Key + "：" + string.Join("／", Enum.GetValues<TagCheckStatus>()
                                .Select(s => new TagCheckResult(new("", "", "", "", ""), "", s, []).StatusText
                                    + " " + group.Count(x => x.Status == s)));
                            Write(line); journal.Message(line);
                        }
                        foreach (var item in checkedItems.Where(x => x.Status == TagCheckStatus.Mismatch).Take(20))
                            Write(item.Subject.ExistingTag + " → " + item.ExpectedTag + "（" + item.Subject.Identity + "）");
                        string counts = $"已处理 {checkedItems.Count}/{total}；未处理 {total - checkedItems.Count}";
                        Write(counts); journal.Message(counts);
                    }
                    journal.Complete(status);
                }
                catch (System.Exception ex) { issues = true; Write("日志收尾失败：" + ex.Message); }
                try { journal.Dispose(); } catch (System.Exception ex) { issues = true; Write("日志关闭失败：" + ex.Message); }
                Write("日志：" + journal.LogPath + "\nCSV：" + journal.CsvPath);
            }
            await LogRetentionPrompt.FinishAsync(files, issues || (rebuild
                ? LogRetentionPolicy.RebuildHasIssues(final, total)
                : LogRetentionPolicy.CheckHasIssues(checkedItems, total)));
        }
    }

    /// <summary>按实际类别列出目标数和涉及图纸数，无格式类别不编号。</summary>
    private static PlantTagClassGroup[] SelectGroups(Editor editor, PlantTagScan scan, Action ensure, bool rebuild)
    {
        var skipped = scan.Groups.Where(g => !g.HasFormat && g.FormatError == null).ToArray();
        if (skipped.Length != 0) Write("未配置位号格式已跳过：" + string.Join("、", skipped.Select(g => g.DisplayName))
            + $"（{skipped.Length} 类，{skipped.Sum(g => g.Targets.Count)} 个目标）");
        foreach (string warning in scan.Warnings) Write(warning);
        var groups = scan.Groups.Where(g => g.HasFormat || g.FormatError != null).ToArray();
        if (groups.Length == 0)
        { foreach (string line in scan.Diagnostics) Write(line); Write("当前项目没有可检查的位号类别。"); return []; }
        for (int i = 0; i < groups.Length; i++)
            Write($"{i + 1} - {groups[i].DisplayName} [{groups[i].PartName}] ({groups[i].Targets.Count}{groups[i].Unit}，"
                + groups[i].Targets.SelectMany(x => x.Drawings).Distinct(StringComparer.OrdinalIgnoreCase).Count() + " 张图纸)");
        while (true)
        {
            ensure();
            var input = editor.GetString(new PromptStringOptions("\n请选择类别编号 [如 1,3,5-7；*=全部] <0>: ") { AllowSpaces = true });
            if (input.Status is not (PromptStatus.OK or PromptStatus.None)) return [];
            ensure();
            try
            {
                var selected = TagCheckPolicy.ParseClasses(input.Status == PromptStatus.None ? "" : input.StringResult, groups.Length);
                if (selected.Length == 0) Write("未选择类别，未执行" + (rebuild ? "重建" : "检查"));
                return selected.Select(n => groups[n - 1]).ToArray();
            }
            catch (ArgumentException ex) { Write(ex.Message); }
        }
    }

    /// <summary>完整选项可点击，键盘 Y/N/C，回车按约定为数据模式 N。</summary>
    private static ProjectTagMode SelectMode(Editor editor, Action ensure)
    {
        ensure();
        var options = new PromptKeywordOptions("\n重建方式");
        options.Keywords.Add("Y", "Y", "打开图纸重建(Y)");
        options.Keywords.Add("N", "N", "仅重建项目数据(N)");
        options.Keywords.Add("C", "C", "取消(C)");
        options.Keywords.Default = "N"; options.AllowNone = true;
        var result = editor.GetKeywords(options);
        if (result.Status is not (PromptStatus.OK or PromptStatus.None)) return ProjectTagMode.Cancel;
        ensure();
        return ProjectTagPolicy.ParseMode(result.Status == PromptStatus.None ? "" : result.StringResult);
    }

    /// <summary>图纸保存全批统一，默认 N；取消终止整个批次而不是默认为数据模式。</summary>
    private static bool ConfirmSave(Editor editor, Action ensure)
    {
        ensure();
        var options = new PromptKeywordOptions("\n写入后保存所有处理图纸？");
        options.Keywords.Add("Y", "Y", "是(Y)"); options.Keywords.Add("N", "N", "否(N)");
        options.Keywords.Default = "N"; options.AllowNone = true;
        var input = editor.GetKeywords(options);
        if (input.Status is not (PromptStatus.OK or PromptStatus.None)) throw new OperationCanceledException("已取消保存策略选择，未写入。");
        ensure();
        return input.Status == PromptStatus.OK && TagRebuildPolicy.IsYes(input.StringResult);
    }

    /// <summary>图纸关闭时也尽量保留停止提示和日志位置。</summary>
    private static void Write(string value) => TagCheckCommands.WriteSafely("\n" + value);
}
