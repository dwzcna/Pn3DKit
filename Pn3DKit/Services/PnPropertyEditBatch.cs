using Pn3DKit.Models;
using Pn3DKit.Utils;

namespace Pn3DKit.Services;

/// <summary>主线程文档锁内的多选属性适配；Capture 和 Prepare 不写入、不求值位号。</summary>
public interface IPnPropertyEditHost<TIdentity, TTarget> where TTarget : notnull
{
    /// <summary>在同一文档锁内重新读取完整选择与逐对象许可；状态无效时抛出具体异常。</summary>
    IReadOnlyList<PnEditContext<TIdentity, TTarget>> Capture();
    /// <summary>只读校验新值并返回真实数据行及登记身份；不得试写探测权限。</summary>
    PnPropertyWritePlan<TTarget> Prepare(PnEditContext<TIdentity, TTarget> target, string value);
    /// <summary>再次复核后仅写一条实际数据行并立即回读；失败抛异常，不保证 Plant 缓存回滚。</summary>
    string Write(PnPropertyWritePlan<TTarget> plan);
    /// <summary>回读真实当前值，空值与无法读取分别用 Value／Error 状态表达。</summary>
    FastPropertyValue ReadActual(TTarget target);
    /// <summary>返回可容错的用户目标标签，仅用于诊断，不替代完整对象身份。</summary>
    string Label(TTarget target);
}

/// <summary>先全量预检，再逐数据行写入和回读；任何实际失败停止后续，不自动回滚或保存。</summary>
public static class PnPropertyEditBatch
{
    /// <summary>调用方在同一宿主主线程及文档锁内执行，租约覆盖全部同步回调及收尾。</summary>
    public static PnPropertyEditResult<TTarget> Run<TIdentity, TTarget>(
        IReadOnlyList<PnEditContext<TIdentity, TTarget>> snapshot, string value,
        IPnPropertyEditHost<TIdentity, TTarget> host) where TTarget : notnull
    {
        var expected = snapshot.ToArray();
        var results = expected.Select(x => new PnPropertyWriteResult<TTarget>(x.Target, host.Label(x.Target),
            x.TargetKey, x.Original, value, null, PnPropertyWriteState.NotExecuted, "尚未执行。")).ToArray();
        var prepared = new Dictionary<TTarget, PnPropertyWritePlan<TTarget>>();
        try
        {
            Validate(expected, host.Capture());
            foreach (var target in expected)
            {
                var plan = host.Prepare(target, value);
                VerifyPlan(target, value, plan);
                prepared.Add(target.Target, plan);
            }
            var conflict = prepared.Values.Where(x => x.Registration != null).GroupBy(x => x.Registration)
                .FirstOrDefault(group => group.Select(x => x.DataKey).Distinct(StringComparer.Ordinal).Count() > 1);
            if (conflict != null) throw new InvalidOperationException("所选对象的新位号存在批内登记冲突。");
            foreach (var aliases in expected.GroupBy(x => x.TargetKey, StringComparer.Ordinal))
                if (aliases.Select(x => x.Original).Distinct(StringComparer.Ordinal).Count() != 1)
                    throw new InvalidOperationException("同一数据行的关联对象原值不一致。");
            Validate(expected, host.Capture());
        }
        catch (Exception ex)
        {
            return new(false, results.Select(x => x with { Detail = "预检拒绝，未写入：" + ex.Message }).ToArray(),
                "全部目标预检未通过，未写入任何对象：" + ex.Message);
        }

        string message = "";
        foreach (var group in expected.GroupBy(x => x.TargetKey, StringComparer.Ordinal).Select(x => x.ToArray()))
        {
            try
            {
                Validate(expected, host.Capture());
                foreach (var target in group)
                {
                    var fresh = host.Prepare(target, value);
                    VerifyPlan(target, value, fresh);
                    if (fresh != prepared[target.Target]) throw new InvalidOperationException("写入计划或位号登记身份已变化。");
                }
                bool unchanged = group.All(x => x.Original == value);
                if (!unchanged)
                {
                    string actual = host.Write(prepared[group[0].Target]);
                    if (actual != value) throw new InvalidOperationException("回读不符，实际值：" + actual);
                }
                foreach (var target in group)
                {
                    var actual = host.ReadActual(target.Target);
                    if (actual.Kind != FastValueKind.Value || actual.Raw != value)
                        throw new InvalidOperationException(host.Label(target.Target) + " 回读不符：" + actual.Display + " " + actual.Detail);
                }
                for (int i = 0; i < results.Length; i++)
                    if (results[i].DataKey == group[0].TargetKey)
                    {
                        results[i] = results[i] with { Actual = value, State = unchanged ? PnPropertyWriteState.Unchanged : PnPropertyWriteState.Updated, Detail = "" };
                        expected[i] = expected[i] with { Original = value };
                    }
            }
            catch (Exception ex)
            {
                message = "已停止后续写入；不假定 Plant 数据已回滚：" + ex.Message;
                for (int i = 0; i < results.Length; i++)
                {
                    if (results[i].DataKey == group[0].TargetKey)
                    {
                        FastPropertyValue actual;
                        try { actual = host.ReadActual(results[i].Target); }
                        catch (Exception read) { actual = FastPropertyValue.Error(read.Message); }
                        results[i] = results[i] with { Actual = actual.Kind == FastValueKind.Value ? actual.Raw : null,
                            State = PnPropertyWriteState.Failed,
                            Detail = ex.Message + (actual.Kind == FastValueKind.Error ? "；回读：" + actual.Detail : "") };
                    }
                    else if (results[i].State == PnPropertyWriteState.NotExecuted)
                        results[i] = results[i] with { Detail = "前一目标失败或状态变化，未执行。" };
                }
                break;
            }
        }
        if (message.Length == 0)
        {
            try { Validate(expected, host.Capture()); }
            catch (Exception ex)
            {
                message = "写入后最终复核失败，请核对实际数据：" + ex.Message;
                for (int i = 0; i < results.Length; i++)
                {
                    FastPropertyValue actual;
                    try { actual = host.ReadActual(results[i].Target); }
                    catch (Exception read) { actual = FastPropertyValue.Error(read.Message); }
                    if (actual.Kind != FastValueKind.Value || actual.Raw != value)
                        results[i] = results[i] with { State = PnPropertyWriteState.Failed,
                            Actual = actual.Kind == FastValueKind.Value ? actual.Raw : null,
                            Detail = "此前已处理，但最终状态不符或无法确认：" + ex.Message + "；" + actual.Detail };
                }
            }
        }
        return new(true, results, message);
    }

    private static void Validate<TIdentity, TTarget>(IReadOnlyList<PnEditContext<TIdentity, TTarget>> expected,
        IReadOnlyList<PnEditContext<TIdentity, TTarget>> current) where TTarget : notnull
    {
        string? reason = PnPropertyEditPolicy.ValidateSelection(expected, current);
        if (reason != null) throw new InvalidOperationException(reason);
    }

    private static void VerifyPlan<TIdentity, TTarget>(PnEditContext<TIdentity, TTarget> target,
        string value, PnPropertyWritePlan<TTarget> plan) where TTarget : notnull
    {
        if (!EqualityComparer<TTarget>.Default.Equals(target.Target, plan.Target) || target.TargetKey != plan.DataKey
            || target.Original != plan.Original || plan.Value != value)
            throw new InvalidOperationException("写入预检没有返回相同的目标、数据行或原值。");
    }
}
