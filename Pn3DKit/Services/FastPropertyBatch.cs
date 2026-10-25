using Pn3DKit.Models;
using Pn3DKit.Utils;

namespace Pn3DKit.Services;

/// <summary>分批读取与过期结果屏障；身份由宿主传入完整数据库标识，不依赖文件名。</summary>
/// <remarks>串行调用，事件可使正在读取的一轮立即过期；不限制选择总量。</remarks>
public sealed class FastPropertyBatch<TIdentity, TTarget>
{
    private sealed class Work(TIdentity identity, IReadOnlyList<TTarget> targets)
    {
        internal readonly TIdentity Identity = identity;
        internal readonly TTarget[] Targets = targets.ToArray();
        internal readonly List<FastObjectProperties> Results = [];
        internal int Index;
    }
    private Work? _work;
    /// <summary>是否还有待处理选择。</summary>
    public bool HasWork => _work != null;
    /// <summary>当前批次已读对象数量。</summary>
    public int Processed => _work?.Index ?? 0;
    /// <summary>固定完整目标清单，替换并使旧批次失效。</summary>
    public void Begin(TIdentity identity, IReadOnlyList<TTarget> targets) => _work = new(identity, targets);
    /// <summary>选择、图纸或项目变化后丢弃旧批次。</summary>
    public void Invalidate() => _work = null;
    /// <summary>一次最多处理 budget 个对象；只有完整且身份仍相同的一轮才发布共同值。</summary>
    public FastPanelSnapshot? Step(TIdentity identity, Func<TTarget, FastObjectProperties> read, int budget = 32)
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
        var snapshot = FastPropertyPolicy.Merge(work.Results);
        if (!ReferenceEquals(_work, work)) return null;
        _work = null;
        return snapshot;
    }
}
