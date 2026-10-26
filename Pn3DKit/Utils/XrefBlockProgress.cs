using Pn3DKit.Models;

namespace Pn3DKit.Utils;

/// <summary>保存外参检查的当前阶段，所有输出由调用方在 CAD 主线程执行；离线可注入时钟。</summary>
internal sealed class XrefBlockProgress
{
    /// <summary>每次日志路径展示前的固定提醒。</summary>
    internal const string Reminder = "文件较多或块较多时，耗时可能比较长，长按ESC键中断处理过程";
    private readonly Action<string> _write;
    private readonly TimeProvider _clock;
    private string? _current;
    private long _lastOutput;

    /// <summary>只接收文本输出，不持有 Editor、数据库或后台计时器。</summary>
    /// <param name="write">线程内同步的容错输出。</param>
    /// <param name="clock">测试可推进时钟，默认使用系统时钟。</param>
    internal XrefBlockProgress(Action<string> write, TimeProvider? clock = null)
    { _write = write; _clock = clock ?? TimeProvider.System; _lastOutput = _clock.GetTimestamp(); }

    /// <summary>立即显示新阶段，后续检查点心跳重复该阶段而非固定的扫描文案。</summary>
    internal void Stage(string message) { _current = message; Emit(message); }

    /// <summary>发现总数前只报告已确认来源，不虚构最终分母。</summary>
    internal void SourceFound(int count, string drawing) =>
        Stage("正在发现外参文件……已发现 " + count + " 个来源；" + FileName(drawing));

    /// <summary>名称扫描编号与 dwgNo 对齐，分母来自冻结的来源清单。</summary>
    internal void Scan(int index, int total, string drawing) =>
        Stage("扫描文件 " + index + "/" + total + " " + FileName(drawing) + " ...");

    /// <summary>比较进度按候选名称组计数，不按来源数或实体数计数。</summary>
    internal void Compare(int index, int total, string name) =>
        Stage("正在对比块 " + index + "/" + total + " " + OneLine(name) + " ...");

    /// <summary>仅在报告成功写入并刷新后显示结果；写入故障不输出伪造的完成消息。</summary>
    /// <param name="result">已比较或中断后未执行的候选。</param>
    /// <param name="append">现有逐组报告写入器，任何异常直接传播。</param>
    internal void Complete(XrefBlockComparisonResult result, Action<XrefBlockComparisonResult> append)
    {
        append(result);
        string label = ResultLabel(result);
        Emit("块 " + OneLine(result.Name) + " " + label
            + (result.Status == XrefBlockComparisonStatus.Different && !result.Complete ? "；同时存在未验证来源" : ""));
        _current = null;
    }

    /// <summary>命令行采用符合／不符合，CSV 继续使用原有匹配／不匹配文字。</summary>
    internal static string ResultLabel(XrefBlockComparisonResult result) =>
        result.ResultText switch { "匹配" => "符合", "不匹配" => "不符合", _ => "未验证" };

    /// <summary>空闲检查点约每三秒显示当前阶段，不创建线程或访问宿主。</summary>
    internal void Pulse()
    {
        if (_current != null && _clock.GetElapsedTime(_lastOutput) >= TimeSpan.FromSeconds(3)) Emit(_current);
    }

    /// <summary>开始和结束都先提醒再展示日志路径，提醒不进入周期心跳。</summary>
    internal void Paths(params string[] paths)
    {
        _current = null;
        Emit(Reminder);
        foreach (string path in paths) Emit(path);
    }

    /// <summary>统一刷新心跳时间，避免阶段切换后立即重复输出。</summary>
    private void Emit(string message) { _write(message); _lastOutput = _clock.GetTimestamp(); }
    /// <summary>命令行仅展示文件名，完整路径及来源身份保留在报告中。</summary>
    private static string FileName(string drawing)
    {
        string name;
        try { name = Path.GetFileName(drawing); } catch (ArgumentException) { name = drawing; }
        return OneLine(string.IsNullOrWhiteSpace(name) ? drawing : name);
    }
    /// <summary>来源名称或异常中的换行不制造额外进度行。</summary>
    private static string OneLine(string value) => value.Replace('\r', ' ').Replace('\n', ' ');
}
