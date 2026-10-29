using System.Globalization;

namespace Pn3DKit.Services;

/// <summary>导出命令独占的 Plant 自动检查保护；SDK 读写由主线程委托提供。</summary>
/// <remarks>不实现自动 Dispose，必须在原生操作收尾后根据状态明确决定是否恢复。</remarks>
internal sealed class ExportAutoCheckScope(Func<object?> read, Action<object> write)
{
    internal const string VariableName = "DWGCHECKERAUTOCHECKSWITCH";
    private readonly int _threadId = Environment.CurrentManagedThreadId;
    private object? _original;
    private long _originalNumber;
    private bool _attempted, _restoreRequired;
    private ExportAutoCheckOutcome? _outcome;

    /// <summary>在项目读取和交互前记录原值、关闭并回读；失败由入口捕获，随后仍须调用 Finish。</summary>
    /// <returns>供命令行展示的实际原值和关闭结果。</returns>
    internal string Disable()
    {
        VerifyThread();
        if (_attempted || _outcome != null) throw new InvalidOperationException(VariableName + " 保护不能重复开始。");
        _attempted = true;
        string step = "读取原值";
        try
        {
            _original = read();
            _originalNumber = Integer(_original);
            step = "设置为 0";
            // SDK 可能先改变值再抛错，必须在写入尝试前登记恢复责任。
            _restoreRequired = true;
            write(0);
            step = "回读确认";
            if (Integer(read()) != 0) throw new InvalidOperationException("回读值不是 0。");
            return $"已临时关闭 {VariableName}（原值 {_originalNumber}，本批为 0）；安全结束后恢复原值。";
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"{VariableName} {step}失败，未开始导出：{ex.Message}", ex);
        }
    }

    /// <summary>安全完成时恢复并回读；状态未知时不接触 SDK，恢复错误只返回结果，不覆盖原异常。</summary>
    /// <param name="unsafeReason">原生操作或清理状态不明的原因；为空表示已安全结束。</param>
    /// <returns>一次性的收尾结果，重复调用不会再次写入。</returns>
    internal ExportAutoCheckOutcome Finish(string? unsafeReason)
    {
        if (_outcome != null) return _outcome;
        if (!_restoreRequired) return _outcome = new(false, false, "");
        if (!string.IsNullOrEmpty(unsafeReason))
            return _outcome = new(true, false,
                $"{VariableName} 本批设置为 0；因原生操作或清理状态不明，未自动恢复原值 {_originalNumber}：{unsafeReason}。请待 CAD 空闲后手动核对恢复。");
        try
        {
            VerifyThread();
            write(_original!);
            if (Integer(read()) != _originalNumber) throw new InvalidOperationException("恢复后的回读值与原值不符。");
            return _outcome = new(false, true, $"{VariableName} 已恢复原值 {_originalNumber}，回读确认完成。");
        }
        catch (Exception ex)
        {
            return _outcome = new(true, false,
                $"{VariableName} 恢复原值 {_originalNumber} 失败：{ex.Message}。请待 CAD 空闲后手动核对；本批日志保留。");
        }
    }

    /// <summary>只接受 SDK 的整数值，拒绝 null、文本及被取整后伪装为零的小数。</summary>
    private static long Integer(object? value) => value is sbyte or byte or short or ushort or int or uint or long or ulong
        ? Convert.ToInt64(value, CultureInfo.InvariantCulture)
        : throw new InvalidOperationException("自动检查配置未返回可识别的整数值。");

    /// <summary>保证设置与恢复在创建保护时的 CAD 主线程执行。</summary>
    private void VerifyThread()
    {
        if (Environment.CurrentManagedThreadId != _threadId)
            throw new InvalidOperationException(VariableName + " 的读取和写入必须在 CAD 主线程执行。");
    }
}

/// <summary>导出环境恢复结果；未知或恢复失败必须保护本批日志。</summary>
internal sealed record ExportAutoCheckOutcome(bool HasIssue, bool Restored, string Message);
