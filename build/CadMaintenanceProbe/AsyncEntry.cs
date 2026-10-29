using System.Text.Json;
using Autodesk.AutoCAD.ApplicationServices;

namespace Pn3DKit.CadCommandProbe;

/// <summary>复用等待异步结束的已加载验证宿主；SDK查询不冒充原生命令或Esc验收。</summary>
public static class Probe
{
    /// <summary>在CAD主线程同步运行独立项目数据库回归，再返回已完成任务。</summary>
    public static Task<string> RunAsync(Document document, string product, string output, Action ensure)
    {
        string result = Pn3DKit.CadVisualProbe.Probe.Run(document, product, output, ensure);
        using var parsed = JsonDocument.Parse(result);
        return Task.FromResult(JsonSerializer.Serialize(new
        {
            Success = parsed.RootElement.GetProperty("Success").GetBoolean(),
            AcceptanceComplete = false,
            Kind = "独立SDK查询及数值列验证，不含命令、图形事件或Esc验收",
            Details = parsed.RootElement.Clone()
        }, new JsonSerializerOptions { WriteIndented = true }));
    }
}
