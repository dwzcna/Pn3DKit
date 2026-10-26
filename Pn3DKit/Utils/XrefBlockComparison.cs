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

    /// <summary>计算图不变量作加速；相同指纹仍必须逐字段及图拓扑比较。</summary>
    public static string Fingerprint(BlockContentSnapshot graph)
    {
        string data = JsonSerializer.Serialize(new
        {
            Root = Local(graph.Nodes[graph.Root]),
            Nodes = Reachable(graph, null).Select(Local).OrderBy(x => x, StringComparer.Ordinal)
        });
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(data)));
    }

    /// <summary>严格比较根图的双射关系，保留循环、共享节点和重复边，忽略节点编号和实体创建顺序。</summary>
    /// <remarks>回溯中检测取消，不用哈希或近似替代等价性证明。</remarks>
    public static bool Equivalent(BlockContentSnapshot left, BlockContentSnapshot right, Action? check = null)
    {
        var a = Reachable(left, check); var b = Reachable(right, check);
        if (a.Length != b.Length) return false;
        var signaturesA = a.ToDictionary(x => x.Id, Local, StringComparer.Ordinal);
        var signaturesB = b.ToDictionary(x => x.Id, Local, StringComparer.Ordinal);
        if (signaturesA[left.Root] != signaturesB[right.Root]) return false;
        var bySignature = b.GroupBy(x => signaturesB[x.Id]).ToDictionary(g => g.Key, g => g.Select(x => x.Id).ToArray());
        var map = new Dictionary<string, string>(StringComparer.Ordinal) { [left.Root] = right.Root };
        var inverse = new Dictionary<string, string>(StringComparer.Ordinal) { [right.Root] = left.Root };
        Dictionary<string, (string Label, string Source)[]> Incoming(BlockContentNode[] nodes) =>
            nodes.SelectMany(n => n.Edges.Select(e => (e.Target, e.Label, Source: n.Id)))
                .GroupBy(e => e.Target).ToDictionary(g => g.Key, g => g.Select(e => (e.Label, e.Source)).ToArray());
        var incomingA = Incoming(a); var incomingB = Incoming(b);
        bool Links(BlockContentNode n, BlockContentNode m)
        {
            if (!SamePayloads(n, m)) return false;
            var x = n.Edges.Where(e => map.ContainsKey(e.Target))
                .GroupBy(e => (e.Label, Target: map[e.Target])).ToDictionary(g => g.Key, g => g.Count());
            var y = m.Edges.Where(e => inverse.ContainsKey(e.Target))
                .GroupBy(e => (e.Label, e.Target)).ToDictionary(g => g.Key, g => g.Count());
            if (x.Count != y.Count || x.Any(p => y.GetValueOrDefault(p.Key) != p.Value)) return false;
            var p = incomingA.GetValueOrDefault(n.Id, []).Where(e => map.ContainsKey(e.Source))
                .GroupBy(e => (e.Label, Source: map[e.Source])).ToDictionary(g => g.Key, g => g.Count());
            var q = incomingB.GetValueOrDefault(m.Id, []).Where(e => inverse.ContainsKey(e.Source))
                .GroupBy(e => (e.Label, e.Source)).ToDictionary(g => g.Key, g => g.Count());
            return p.Count == q.Count && p.All(e => q.GetValueOrDefault(e.Key) == e.Value);
        }
        if (!Links(left.Nodes[left.Root], right.Nodes[right.Root])) return false;
        // 显式栈回溯避免大型定义因节点数造成托管递归栈溢出。
        var frames = new Stack<(BlockContentNode Node, string[] Choices, int Index)>();
        bool Advance(BlockContentNode node, string[] choices, int start)
        {
            for (int index = start; index < choices.Length; index++)
            {
                check?.Invoke();
                string candidate = choices[index];
                if (inverse.ContainsKey(candidate) || !Links(node, right.Nodes[candidate])) continue;
                map.Add(node.Id, candidate); inverse.Add(candidate, node.Id);
                if (Links(node, right.Nodes[candidate]))
                { frames.Push((node, choices, index)); return true; }
                map.Remove(node.Id); inverse.Remove(candidate);
            }
            return false;
        }
        while (true)
        {
            check?.Invoke();
            if (map.Count == a.Length) return true;
            var node = a.First(n => !map.ContainsKey(n.Id));
            string[] choices = bySignature.GetValueOrDefault(signaturesA[node.Id], []);
            if (Advance(node, choices, 0)) continue;
            bool restored = false;
            while (frames.Count != 0)
            {
                var frame = frames.Pop();
                string previous = map[frame.Node.Id];
                map.Remove(frame.Node.Id); inverse.Remove(previous);
                if (Advance(frame.Node, frame.Choices, frame.Index + 1))
                { restored = true; break; }
            }
            if (!restored) return false;
        }
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
            for (int i = x.Count - 1; i >= 0; i--)
            {
                int same = y.FindIndex(n => Local(n) == Local(x[i]) && SamePayloads(n, x[i]));
                if (same >= 0) { y.RemoveAt(same); x.RemoveAt(i); }
            }
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
        if (result.Count == 0) result.Add("关联拓扑、嵌套定义、绘制顺序或资源连接不同。");
        return result;
    }

    /// <summary>按内容归组；不完整来源独立保留，已确认冲突优先显示，并独立标记完整性。</summary>
    public static XrefBlockResult Compare(string name, IReadOnlyList<XrefBlockDefinition> definitions,
        bool scopeComplete, Action? check = null, Func<BlockContentSnapshot, string>? fingerprint = null)
    {
        fingerprint ??= Fingerprint;
        var groups = new List<List<XrefBlockDefinition>>(); var hashes = new List<string>();
        var unknown = new List<XrefBlockDefinition>(); var issues = new List<string>();
        foreach (var item in definitions.OrderByDescending(d => d.Source.IsHost)
            .ThenBy(d => d.Source.Drawing, StringComparer.OrdinalIgnoreCase).ThenBy(d => d.Source.Key, StringComparer.Ordinal))
        {
            check?.Invoke();
            if (!item.Content.Complete) { unknown.Add(item); continue; }
            try
            {
                string hash = fingerprint(item.Content);
                int group = -1;
                for (int i = 0; i < groups.Count; i++)
                    if (hashes[i] == hash && Equivalent(groups[i][0].Content, item.Content, check)) { group = i; break; }
                if (group >= 0) groups[group].Add(item);
                else { groups.Add([item]); hashes.Add(hash); }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { unknown.Add(item); issues.Add(item.Source.Drawing + "：" + ex.Message); }
        }
        foreach (var item in unknown)
        {
            if (item.Content.ReadError != null) issues.Add(item.Source.Drawing + "：" + item.Content.ReadError);
            issues.AddRange(item.Content.Issues.Select(s => item.Source.Drawing + "：" + s));
        }
        if (!scopeComplete) issues.Add("来源范围不完整；未读取外参中可能仍有同名定义。");
        bool complete = scopeComplete && unknown.Count == 0;
        var status = groups.Count > 1 ? XrefBlockStatus.Conflict
            : !complete ? unknown.Any(d => d.Content.ReadError != null) ? XrefBlockStatus.ReadError : XrefBlockStatus.Incomplete
            : definitions.Count <= 1 ? XrefBlockStatus.Unique : XrefBlockStatus.Consistent;
        var differences = new List<string>();
        for (int i = 1; i < groups.Count; i++)
        {
            differences.Add("定义1 与 定义" + (i + 1) + "：");
            differences.AddRange(Differences(groups[0][0].Content, groups[i][0].Content, check));
        }
        string risk = status != XrefBlockStatus.Conflict ? ""
            : definitions.Any(d => d.Source.IsHost)
                ? "主图已存在 " + name + "，Insert 合并时存在复用主图定义的风险。"
                : (scopeComplete ? "主图没有 " : "已读主图清单未发现 ") + name
                    + "，外参定义不同，Insert 合并结果可能受绑定顺序影响。";
        return new(name, status, complete,
            groups.Select((g, i) => new XrefBlockGroup((i + 1).ToString(CultureInfo.InvariantCulture), g)).Concat(
                unknown.Select((d, i) => new XrefBlockGroup("未完整" + (i + 1), [d]))).ToArray(),
            differences, issues.Distinct().ToArray(), risk);
    }

    /// <summary>仅删除关系图已经确认的本源命名空间；不能截取未知名称的最后一段。</summary>
    public static string? RemoveSourceNamespace(string stored, IEnumerable<string> namespaces)
    {
        if (!stored.Contains('|')) return stored;
        foreach (string prefix in namespaces.OrderByDescending(x => x.Length))
            if (stored.StartsWith(prefix + "|", StringComparison.OrdinalIgnoreCase))
                return stored[(prefix.Length + 1)..];
        return null;
    }

    /// <summary>根层直接外参均纳入；嵌套覆盖不向上传播。</summary>
    public static bool IncludesReference(int parentDepth, bool overlay) => parentDepth == 0 || !overlay;
}
