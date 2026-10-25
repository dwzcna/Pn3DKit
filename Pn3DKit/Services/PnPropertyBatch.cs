using Pn3DKit.Models;
using Pn3DKit.Utils;

namespace Pn3DKit.Services;

/// <summary>自定义属性表的分批读取，事件使旧轮次失效；保留完整选择集。</summary>
public sealed class PnPropertyBatch<TIdentity, TTarget>
{
    private sealed class Work(TIdentity identity, IReadOnlyList<TTarget> targets, IReadOnlyList<PnPropertySpec> specs)
    {
        internal readonly TIdentity Identity = identity;
        internal readonly TTarget[] Targets = targets.ToArray();
        internal readonly PnPropertySpec[] Specs = specs.ToArray();
        internal readonly List<PnObjectProperties> Results = [];
        internal int Index;
    }
    private Work? _work;
    public bool HasWork => _work != null;
    public int Processed => _work?.Index ?? 0;
    /// <summary>固定选择清单和属性行修订，输入变化后必须另起轮次。</summary>
    public void Begin(TIdentity identity, IReadOnlyList<TTarget> targets, IReadOnlyList<PnPropertySpec> specs) => _work = new(identity, targets, specs);
    public void Invalidate() => _work = null;
    /// <summary>只有完整且身份相同的一轮返回共同值。</summary>
    public PnPanelSnapshot? Step(TIdentity identity, Func<TTarget, PnObjectProperties> read, int budget = 32)
    {
        if (budget <= 0) throw new ArgumentOutOfRangeException(nameof(budget));
        var work = _work;
        if (work == null) return null;
        if (!EqualityComparer<TIdentity>.Default.Equals(identity, work.Identity)) { Invalidate(); return null; }
        for (int i = 0; i < budget && work.Index < work.Targets.Length; i++)
        {
            var value = read(work.Targets[work.Index]);
            if (!ReferenceEquals(_work, work)) return null;
            work.Results.Add(value); work.Index++;
        }
        if (work.Index != work.Targets.Length) return null;
        var snapshot = PnPropertyPolicy.Merge(work.Specs, work.Results);
        if (!ReferenceEquals(_work, work)) return null;
        _work = null; return snapshot;
    }
}
