using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Pn3DKit.Models;

namespace Pn3DKit.Utils;

/// <summary>对身份无关的内容图做严格比较；实体多重集和关联拓扑均参与，不执行 CAD 操作。</summary>
public static class XrefBlockComparison
{
    /// <summary>精确保留浮点数，不依显示精度取整；非有限值不能形成可靠快照。</summary>
    public static string Number(double value)
    {
        if (!double.IsFinite(value)) throw new InvalidOperationException("几何值不是有限数字。");
        return value == 0 ? "0" : value.ToString("R", CultureInfo.InvariantCulture);
    }

    /// <summary>检查图内引用并返回根可达节点；环合法保留，不以本地 Id 作为内容。</summary>
    private static BlockContentNode[] Reachable(BlockContentSnapshot graph, Action? check)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Stack<string>(); pending.Push(graph.Root);
        var result = new List<BlockContentNode>();
        while (pending.Count != 0)
        {
            check?.Invoke();
            string id = pending.Pop();
            if (!seen.Add(id)) continue;
            if (!graph.Nodes.TryGetValue(id, out var node))
                throw new InvalidOperationException("内容关联目标未完整读取：" + id);
            result.Add(node);
            foreach (var edge in node.Edges) pending.Push(edge.Target);
        }
        return result.ToArray();
    }

    /// <summary>无歧义编码类型和字段，保留大小写、换行与重复边标签。</summary>
    private static string Local(BlockContentNode node) => JsonSerializer.Serialize(new
    {
        node.Kind,
        Fields = node.Fields.OrderBy(x => x.Key, StringComparer.Ordinal).Select(x => new[] { x.Key, x.Value }),
        Payloads = (node.Payloads ?? new Dictionary<string, byte[]>()).OrderBy(x => x.Key, StringComparer.Ordinal)
            .Select(x => new { x.Key, Length = x.Value.Length, Digest = Convert.ToHexString(SHA256.HashData(x.Value)) }),
        Labels = node.Edges.GroupBy(x => x.Label, StringComparer.Ordinal).OrderBy(x => x.Key, StringComparer.Ordinal)
            .Select(x => new { x.Key, Count = x.Count() })
    });

    /// <summary>资源哈希相同后仍比较全部字节，不能仅凭字体或图片摘要判定相同。</summary>
    private static bool SamePayloads(BlockContentNode a, BlockContentNode b)
    {
        var left = a.Payloads ?? new Dictionary<string, byte[]>();
        var right = b.Payloads ?? new Dictionary<string, byte[]>();
        return left.Count == right.Count && left.All(x => right.TryGetValue(x.Key, out var value)
            && x.Value.AsSpan().SequenceEqual(value));
    }

    /// <summary>非递归后序遍历，循环或缺失依赖不能进入一致判定。</summary>
    private static BlockContentNode[] BottomUp(BlockContentSnapshot graph, Action? check)
    {
        var states = new Dictionary<string, int>(StringComparer.Ordinal);
        var result = new List<BlockContentNode>();
        var stack = new Stack<(string Id, bool Exit)>(); stack.Push((graph.Root, false));
        while (stack.Count != 0)
        {
            check?.Invoke();
            var (id, exit) = stack.Pop();
            if (!graph.Nodes.TryGetValue(id, out var node)) throw new BlockContentNormalizationException("依赖内容缺失：" + id);
            if (!string.Equals(id, node.Id, StringComparison.Ordinal))
                throw new BlockContentNormalizationException("内容节点身份与本地图索引不一致。");
            if (exit) { states[id] = 2; result.Add(node); continue; }
            if (states.GetValueOrDefault(id) == 2) continue;
            if (states.GetValueOrDefault(id) == 1) throw new BlockContentNormalizationException("循环依赖无法完整比较：" + node.Kind);
            states[id] = 1; stack.Push((id, true));
            foreach (var edge in node.Edges.Reverse()) stack.Push((edge.Target, false));
        }
        return result.ToArray();
    }

    /// <summary>按依赖内容自底向上生成稳定摘要，不含节点编号及实体创建顺序。</summary>
    public static string Fingerprint(BlockContentSnapshot graph, Action? check = null)
    {
        var hashes = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var node in BottomUp(graph, check))
        {
            string data = JsonSerializer.Serialize(new { Node = Local(node),
                Edges = node.Edges.Select(e => new[] { e.Label, hashes[e.Target] })
                    .OrderBy(e => e[0], StringComparer.Ordinal).ThenBy(e => e[1], StringComparer.Ordinal) });
            hashes[node.Id] = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(data)));
        }
        return hashes[graph.Root];
    }

    /// <summary>以完整字段、边多重集和资源字节构造共同内容身份；摘要相同不能跳过核对。</summary>
    /// <remarks>只规范化无环依赖，不执行图同构回溯；共享的相同依赖不因存储别名不同误报。</remarks>
    public static bool Equivalent(BlockContentSnapshot left, BlockContentSnapshot right, Action? check = null)
    {
        var intern = new Dictionary<string, List<(BlockContentNode Node, int Canonical)>>(StringComparer.Ordinal);
        int next = 0;
        int Canonical(BlockContentSnapshot graph)
        {
            var ids = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var node in BottomUp(graph, check))
            {
                check?.Invoke();
                string key = JsonSerializer.Serialize(new { Node = Local(node),
                    Edges = node.Edges.Select(e => (e.Label, Target: ids[e.Target])).OrderBy(e => e.Label, StringComparer.Ordinal)
                        .ThenBy(e => e.Target).Select(e => new object[] { e.Label, e.Target }) });
                if (!intern.TryGetValue(key, out var bucket)) intern[key] = bucket = [];
                var match = bucket.FirstOrDefault(item => SamePayloads(item.Node, node));
                int id = match.Node == null ? ++next : match.Canonical;
                if (match.Node == null) bucket.Add((node, id));
                ids[node.Id] = id;
            }
            return ids[graph.Root];
        }
        return Canonical(left) == Canonical(right);
    }

    /// <summary>输出类型、具体字段与拓扑差异；先移除相同字段节点，避免创建顺序导致错误配对。</summary>
    public static IReadOnlyList<string> Differences(BlockContentSnapshot left, BlockContentSnapshot right, Action? check = null)
    {
        var result = new List<string>();
        var a = Reachable(left, check); var b = Reachable(right, check);
        foreach (string kind in a.Select(n => n.Kind).Union(b.Select(n => n.Kind)).OrderBy(x => x, StringComparer.Ordinal))
        {
            check?.Invoke();
            var x = a.Where(n => n.Kind == kind).OrderBy(Local, StringComparer.Ordinal).ToList();
            var y = b.Where(n => n.Kind == kind).OrderBy(Local, StringComparer.Ordinal).ToList();
            if (x.Count != y.Count) result.Add(kind + " 数量：" + x.Count + " → " + y.Count);
            var buckets = y.GroupBy(Local, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);
            var remaining = new List<BlockContentNode>();
            foreach (var item in x)
            {
                check?.Invoke();
                if (!buckets.TryGetValue(Local(item), out var bucket)) { remaining.Add(item); continue; }
                int same = bucket.FindLastIndex(other => SamePayloads(item, other));
                if (same < 0) remaining.Add(item); else bucket.RemoveAt(same);
            }
            x = remaining;
            y = buckets.Values.SelectMany(items => items).OrderBy(Local, StringComparer.Ordinal).ToList();
            for (int i = 0; i < Math.Min(x.Count, y.Count); i++)
            {
                var leftPayloads = x[i].Payloads ?? new Dictionary<string, byte[]>();
                var rightPayloads = y[i].Payloads ?? new Dictionary<string, byte[]>();
                foreach (string payload in leftPayloads.Keys.Union(rightPayloads.Keys))
                    if (!leftPayloads.TryGetValue(payload, out var leftBytes) || !rightPayloads.TryGetValue(payload, out var rightBytes)
                        || !leftBytes.AsSpan().SequenceEqual(rightBytes))
                        result.Add(kind + "." + payload + "：资源文件内容不同。");
                foreach (string field in x[i].Fields.Keys.Union(y[i].Fields.Keys).OrderBy(s => s, StringComparer.Ordinal))
                {
                    string old = x[i].Fields.GetValueOrDefault(field) ?? "〈不存在〉";
                    string current = y[i].Fields.GetValueOrDefault(field) ?? "〈不存在〉";
                    if (old != current) result.Add(kind + "." + field + "：定义1为 " + old + "；另一组为 " + current);
                }
            }
        }
        foreach (string label in left.Nodes[left.Root].Edges.Select(e => e.Label).Union(right.Nodes[right.Root].Edges.Select(e => e.Label)))
        {
            int old = left.Nodes[left.Root].Edges.Count(e => e.Label == label), current = right.Nodes[right.Root].Edges.Count(e => e.Label == label);
            if (old != current) result.Add("根定义." + label + " 数量：" + old + " → " + current);
        }
        if (result.Count == 0) result.Add("关联拓扑、嵌套定义、绘制顺序或资源连接不同。");
        return result;
    }

    /// <summary>只从本次内存统计筛选重复普通名称，不回读 CSV。</summary>
    public static IReadOnlyList<XrefBlockCandidate> Candidates(XrefBlockInventory inventory, XrefBlockStatistics statistics)
    {
        var byName = inventory.Entries.Where(e => !e.IsAnonymous).GroupBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.DistinctBy(e => e.Source.DwgNo).OrderBy(e => e.Source.DwgNo).ToArray(), StringComparer.OrdinalIgnoreCase);
        return statistics.Normal.Where(r => r.DwgCount > 1).Select(r =>
        {
            var entries = byName[r.Name];
            if (!entries.Select(e => e.Source.DwgNo).SequenceEqual(r.DwgNumbers))
                throw new InvalidOperationException("候选来源与名称统计不一致：" + r.Name);
            return new XrefBlockCandidate(r.Name, entries);
        }).ToArray();
    }

    /// <summary>按最小来源编号优先归组，只有完整内容通过精确核对才可合并。</summary>
    public static XrefBlockComparisonResult Group(XrefBlockCandidate candidate, IReadOnlyList<XrefComparedDefinition> definitions,
        bool scopeComplete, Action? check = null, Func<BlockContentSnapshot, string>? fingerprint = null)
    {
        var groups = new List<List<XrefComparedDefinition>>(); var hashes = new List<string>();
        var unknown = new List<XrefComparedDefinition>(); var problems = new List<string>();
        foreach (var item in definitions.OrderBy(d => d.Entry.Source.DwgNo))
        {
            check?.Invoke();
            if (!item.Content.Complete) { unknown.Add(item); continue; }
            try
            {
                string hash = fingerprint == null ? Fingerprint(item.Content, check) : fingerprint(item.Content);
                int found = -1;
                for (int i = 0; i < groups.Count; i++)
                    if (hashes[i] == hash && Equivalent(groups[i][0].Content, item.Content, check)) { found = i; break; }
                if (found >= 0) groups[found].Add(item);
                else { groups.Add([item]); hashes.Add(hash); }
            }
            catch (OperationCanceledException) { throw; }
            catch (BlockContentNormalizationException ex) { unknown.Add(item with { Content = item.Content with { Issues = [ex.Message] } }); }
        }
        foreach (var item in unknown)
        {
            string source = "dwgNo=" + item.Entry.Source.DwgNo + " " + item.Entry.Source.Drawing + "：";
            if (item.Content.ReadError != null) problems.Add(source + item.Content.ReadError);
            problems.AddRange(item.Content.Issues.Select(s => source + s));
        }
        if (definitions.Count != candidate.Entries.Count) problems.Add("候选来源尚未全部读取。");
        if (!scopeComplete) problems.Add("名称扫描范围不完整，已知来源相同不能证明完整范围一致。");
        bool complete = scopeComplete && unknown.Count == 0 && definitions.Count == candidate.Entries.Count;
        var status = groups.Count > 1 ? XrefBlockComparisonStatus.Different
            : !complete ? unknown.Any(d => d.Content.ReadError != null) ? XrefBlockComparisonStatus.ReadError : XrefBlockComparisonStatus.Incomplete
            : XrefBlockComparisonStatus.Consistent;
        var differences = new List<string>();
        for (int i = 1; i < groups.Count; i++)
        {
            differences.Add("内容组1 与 内容组" + (i + 1) + "：");
            differences.AddRange(Differences(groups[0][0].Content, groups[i][0].Content, check));
        }
        return new(candidate.Name, status, complete, candidate.Entries,
            groups.Select((g, i) => new XrefBlockContentGroup(i + 1, g)).ToArray(), differences, problems);
    }

    /// <summary>停止后的未完成候选不再读取 SDK，逐来源保留未执行记录。</summary>
    public static XrefBlockComparisonResult NotExecuted(XrefBlockCandidate candidate, string reason) =>
        new(candidate.Name, XrefBlockComparisonStatus.NotExecuted, false, candidate.Entries, [], [], [reason]);
}

/// <summary>纯快照结构不完整，与用户取消、文档切换及输出故障严格区分。</summary>
internal sealed class BlockContentNormalizationException(string message) : InvalidOperationException(message);
