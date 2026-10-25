using Pn3DKit.Models;

namespace Pn3DKit.Utils;

/// <summary>只读重建预检与冲突传播；不生成号码，不安排互换或占用链写入。</summary>
public static class TagRebuildPolicy
{
    /// <summary>只有明确 Y/Yes 才允许写入或保存；回车、No 和取消均为否。</summary>
    public static bool IsYes(string? input) =>
        string.Equals(input, "Y", StringComparison.OrdinalIgnoreCase)
        || string.Equals(input, "Yes", StringComparison.OrdinalIgnoreCase);

    /// <summary>检查范围和重建值；SLINE 继承判断由 SDK 适配层提供。</summary>
    public static string? UnavailableReason(TagRebuildCandidate item)
    {
        if (item.Exclusion != null) return item.Exclusion;
        if (item.Check.Subject.ProjectPart != "PnId") return "管线号需按管线组处理；本版只写 PnId.Tag";
        if (item.Check.Status is not (TagCheckStatus.Mismatch or TagCheckStatus.Consistent))
            return item.Check.StatusText + (item.Check.Error == null ? "" : "：" + item.Check.Error);
        if (string.IsNullOrWhiteSpace(item.Check.ExpectedTag)) return "期望 Tag 为空";
        if (item.Check.ExpectedTag.Contains('?')) return "期望 Tag 含 ?，有属性为空";
        return null;
    }

    /// <summary>构建完整预检；同批重号、项目占用、互换及占用链的所有所选参与者均跳过。</summary>
    /// <param name="items">已按实际数据行去重的对象快照。</param>
    /// <param name="findOwners">注册表或本图索引返回的占用行，可包含批次以外的对象。</param>
    public static IReadOnlyList<TagRebuildPlan> Prepare(IReadOnlyList<TagRebuildCandidate> items,
        Func<TagRebuildCandidate, IReadOnlyList<string>> findOwners, Action? ensureContext = null)
    {
        items = MergeAliases(items);
        var plans = items.ToDictionary(x => x.Key, x =>
        {
            string? reason = UnavailableReason(x);
            return new TagRebuildPlan(x, reason != null ? TagRebuildState.Unavailable
                : x.Check.Status == TagCheckStatus.Consistent ? TagRebuildState.Consistent
                : TagRebuildState.Ready, reason ?? (x.Check.Status == TagCheckStatus.Consistent ? "位号一致" : ""));
        }, StringComparer.Ordinal);
        var evidence = items.ToDictionary(x => x.Key, _ => new HashSet<string>(StringComparer.Ordinal));
        void Conflict(string a, string b, string reason)
        {
            evidence[a].Add(reason);
            if (evidence.ContainsKey(b)) evidence[b].Add(reason);
        }
        var ready = plans.Values.Where(x => x.State == TagRebuildState.Ready).Select(x => x.Item).ToArray();
        // 注册键比较采用保守的大小写不敏感策略，最终回读仍严格使用 Ordinal。
        void DuplicateGroup(IEnumerable<TagRebuildCandidate> source)
        {
            var values = source.ToArray();
            for (int i = 1; i < values.Length; i++)
                Conflict(values[0].Key, values[i].Key,
                    $"本批期望 Tag 重复：{values[0].Check.ExpectedTag}；对象 {values[0].Check.Subject.Handle}、{values[i].Check.Subject.Handle}");
        }
        foreach (var group in ready.Where(x => x.Registry.Length != 0).GroupBy(x =>
            x.Check.Subject.ProjectPart + "\n" + x.Registry + "\n" + x.CandidateTag, StringComparer.OrdinalIgnoreCase))
            DuplicateGroup(group);
        foreach (var group in ready.GroupBy(x => x.Check.Subject.ProjectPart + "\n" + x.Check.ExpectedTag,
            StringComparer.OrdinalIgnoreCase).Where(g => g.Any(x => x.Registry.Length == 0)))
            DuplicateGroup(group);
        foreach (var item in ready)
        {
            // 图纸／项目失效及 Esc 属于批次停止，不能转换为单对象 SDK 查询失败。
            ensureContext?.Invoke();
            try
            {
                foreach (string owner in findOwners(item).Where(key => key != item.Key).Distinct())
                    Conflict(item.Key, owner, $"期望 Tag {item.Check.ExpectedTag} 已被 {owner} 占用；目标句柄 {item.Check.Subject.Handle}，占用链参与者均跳过");
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            { plans[item.Key] = new(item, TagRebuildState.Unavailable, "冲突查询失败：" + ex.Message); }
        }
        return items.Select(x => evidence[x.Key].Count == 0 ? plans[x.Key]
            : new TagRebuildPlan(x, TagRebuildState.Conflict, string.Join("；", evidence[x.Key]))).ToArray();
    }

    /// <summary>合并同数据行图形别名；同一行的快照不一致时禁止写入，不把别名算作重号。</summary>
    public static IReadOnlyList<TagRebuildCandidate> MergeAliases(IEnumerable<TagRebuildCandidate> items) =>
        items.GroupBy(x => x.Key, StringComparer.Ordinal).Select(group =>
        {
            var first = group.First();
            string handles = string.Join("、", group.SelectMany(x => x.Check.Subject.Handle.Split('、')).Distinct());
            bool inconsistent = group.Any(x => x.Check.Subject.ExistingTag != first.Check.Subject.ExistingTag
                || x.Check.ExpectedTag != first.Check.ExpectedTag || x.Check.Subject.Format != first.Check.Subject.Format
                || x.Check.Status != first.Check.Status || x.Registry != first.Registry || x.CandidateTag != first.CandidateTag);
            return first with
            {
                Check = first.Check with { Subject = first.Check.Subject with { Handle = handles } },
                Exclusion = inconsistent ? "同一数据行的预检快照不一致，请重新检查"
                    : group.Select(x => x.Exclusion).FirstOrDefault(x => x != null)
            };
        }).ToArray();

    /// <summary>确认后只允许原预览中的候选写入，同时传播新发现冲突到全部所选参与者。</summary>
    public static IReadOnlyList<TagRebuildPlan> RestrictToPreview(IReadOnlyList<TagRebuildPlan> preview,
        IReadOnlyList<TagRebuildPlan> refreshed)
    {
        var original = preview.ToDictionary(x => x.Item.Key, StringComparer.Ordinal);
        return refreshed.Select(x => x.State == TagRebuildState.Conflict
            || original[x.Item.Key].State == TagRebuildState.Ready ? x : original[x.Item.Key]).ToArray();
    }

    /// <summary>把预检映射为追加历史；只有待重建项需要后续最终动作。</summary>
    public static TagRebuildRecord Preview(TagRebuildPlan plan) => new(plan.Item, plan.State switch
    {
        TagRebuildState.Ready => TagRebuildAction.Pending,
        TagRebuildState.Conflict => TagRebuildAction.Conflict,
        _ => TagRebuildAction.Skipped
    }, plan.Reason);

    /// <summary>取消确认时保留预览，为所有候选追加未执行。</summary>
    public static IReadOnlyList<TagRebuildRecord> Decline(IReadOnlyList<TagRebuildPlan> plans,
        ITagRebuildJournal journal, string reason)
    {
        return plans.Select(plan =>
        {
            var record = plan.State == TagRebuildState.Ready
                ? new TagRebuildRecord(plan.Item, TagRebuildAction.Unexecuted, reason) : Preview(plan);
            if (plan.State == TagRebuildState.Ready) journal.Append(record);
            return record;
        }).ToArray();
    }

    /// <summary>汇总最终记录，跳过原因单独列出，不重复计算 CSV 中的预览行。</summary>
    public static string Summary(IEnumerable<TagRebuildRecord> source)
    {
        var records = source.ToArray();
        return $"已更新 {records.Count(x => x.Action == TagRebuildAction.Updated)}／失败 {records.Count(x => x.Action == TagRebuildAction.Failed)}"
            + $"／跳过 {records.Count(x => x.Action == TagRebuildAction.Skipped)}／冲突 {records.Count(x => x.Action == TagRebuildAction.Conflict)}"
            + $"／未执行 {records.Count(x => x.Action == TagRebuildAction.Unexecuted)}"
            + string.Concat(records.Where(x => x.Action == TagRebuildAction.Skipped).GroupBy(x => x.Reason)
                .Select(g => $"；{g.Key} {g.Count()}"));
    }
}
