using System.Diagnostics;
using Pn3DKit.Models;
using Pn3DKit.Utils;

namespace Pn3DKit.Services;

/// <summary>先筛选跨来源同名候选，再按需读取；中断和输出失败保留已落盘结果。</summary>
public static class XrefBlockBatch
{
    /// <summary>只依据轻量名称清单分类；同来源多条链不增加候选，唯一名称不读取内容。</summary>
    public static XrefBlockSelection Select(XrefBlockInventory inventory, Action ensure)
    {
        var names = new Dictionary<string, Dictionary<string, XrefBlockNameEntry>>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in inventory.Entries)
        {
            ensure();
            if (!names.TryGetValue(entry.Name, out var sources))
                names.Add(entry.Name, sources = new(StringComparer.Ordinal));
            sources.TryAdd(entry.Source.Key, entry);
        }
        var candidates = new List<XrefBlockCandidate>();
        var unique = new Dictionary<string, int>(StringComparer.Ordinal);
        var named = new Dictionary<string, int>(StringComparer.Ordinal);
        var anonymous = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var sources in names.Values)
        {
            ensure();
            if (sources.Count == 1)
            {
                string key = sources.Values.First().Source.Key;
                unique[key] = unique.GetValueOrDefault(key) + 1;
                continue;
            }
            var entries = sources.Values.OrderByDescending(x => x.Source.IsHost)
                .ThenBy(x => x.Source.Drawing, StringComparer.OrdinalIgnoreCase)
                .ThenBy(x => x.Source.Key, StringComparer.Ordinal).ToArray();
            var candidate = new XrefBlockCandidate(entries[0].Name, entries);
            candidates.Add(candidate);
            var counts = candidate.IsAnonymous ? anonymous : named;
            foreach (var entry in entries) counts[entry.Source.Key] = counts.GetValueOrDefault(entry.Source.Key) + 1;
        }
        var summaries = new List<XrefBlockSourceSummary>();
        foreach (var source in inventory.Sources.DistinctBy(x => x.Key))
        {
            ensure();
            int skipped = unique.GetValueOrDefault(source.Key);
            int deep = named.GetValueOrDefault(source.Key), hints = anonymous.GetValueOrDefault(source.Key);
            summaries.Add(new(source, skipped + deep + hints, skipped, deep, hints));
        }
        return new(candidates.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.Name, StringComparer.Ordinal).ToArray(), summaries, inventory.ScopeComplete);
    }

    /// <summary>只有命名候选调用读取回调；匿名同名直接落报告，不创建空内容组。</summary>
    public static XrefBlockBatchResult Compare(XrefBlockSelection selection,
        Func<XrefBlockNameEntry, XrefBlockDefinition> read, Func<bool> scopeComplete,
        Action ensure, Action<XrefBlockResult> append, Action<int, int> progress)
    {
        var results = new List<XrefBlockResult>();
        bool interrupted = false;
        string? reason = null;
        int requests = 0;
        TimeSpan reading = TimeSpan.Zero, comparing = TimeSpan.Zero;
        bool CompleteScope() => selection.ScopeComplete && scopeComplete();
        try
        {
            foreach (var candidate in selection.Candidates)
            {
                ensure();
                XrefBlockResult result;
                if (candidate.IsAnonymous)
                    result = new(candidate.Name, XrefBlockStatus.AnonymousSkipped, false, [],
                        ["跨图纸匿名块同名，按规则未比较内容；名称重复不代表已确认内容冲突。"], [],
                        "匿名编号属于各来源内部标识，不能据此判断 Insert 合并的实际影响。",
                        candidate.Entries);
                else
                {
                    var definitions = new List<XrefBlockDefinition>();
                    var timer = Stopwatch.StartNew();
                    try
                    {
                        foreach (var entry in candidate.Entries)
                        {
                            ensure(); requests++;
                            definitions.Add(read(entry));
                        }
                    }
                    finally { reading += timer.Elapsed; }
                    ensure();
                    timer.Restart();
                    try { result = XrefBlockComparison.Compare(candidate.Name, definitions, CompleteScope(), ensure); }
                    finally { comparing += timer.Elapsed; }
                }
                ensure();
                append(result);
                results.Add(result);
                if (results.Count % 50 == 0) progress(results.Count, selection.Candidates.Count);
            }
        }
        catch (OperationCanceledException) { interrupted = true; reason = "用户取消，已中断。"; }
        catch (Exception ex) { reason = ex.Message; }
        try
        {
            if (results.Count % 50 != 0 || selection.Candidates.Count == 0)
                progress(results.Count, selection.Candidates.Count);
        }
        catch (OperationCanceledException) { interrupted = true; reason ??= "用户取消，已中断。"; }
        catch (Exception ex) { reason ??= ex.Message; }
        return new(results, CompleteScope(), interrupted, reason, selection.Candidates.Count, selection,
            new(requests, reading, comparing));
    }
}
