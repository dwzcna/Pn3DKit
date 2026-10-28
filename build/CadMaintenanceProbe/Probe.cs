using System.Text.Json;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.ProcessPower.DataObjects;
using Pn3DKit.Utils;

namespace Pn3DKit.CadVisualProbe;

/// <summary>复用既有受控测试入口，使用独立内存表验证真实 SDK 筛选；不访问用户项目数据。</summary>
public static class Probe
{
    /// <summary>仅在 CAD 主线程调用；由外层宿主验证活动文档、选择和系统变量前后不变。</summary>
    public static string Run(Document document, string product, string output, Action ensure)
    {
        var checks = new List<object>();
        void Check(string name, Action action)
        {
            ensure();
            try { action(); checks.Add(new { Name = name, Success = true, Error = (string?)null }); }
            catch (Exception ex) { checks.Add(new { Name = name, Success = false, Error = ex.ToString() }); }
        }
        var table = new PnPTable("Pn3DKit_QueryFixture_" + Guid.NewGuid().ToString("N"));
        try
        {
            table.Columns.Add(new PnPColumn("Tag", typeof(string), 255));
            foreach (string value in new[] { "Valve", "VALVE", "V'1", "阀%_[1]", "Pump" })
            {
                var row = table.NewRow(); row["Tag"] = value; table.Rows.Add(row);
            }
            foreach (string value in new[] { "Valve", "V'1", "阀%_[1]", "Missing" })
            {
                Check("SDK Select 字面量及大小写：" + value, () =>
                {
                    var expected = table.Select().Where(r => string.Equals(Convert.ToString(r["Tag"]), value,
                        StringComparison.OrdinalIgnoreCase)).Select(r => Convert.ToString(r["Tag"])).Order().ToArray();
                    var actual = table.Select(ProjectTagLookup.Filter(value)).Select(r => Convert.ToString(r["Tag"])).Order().ToArray();
                    if (!actual.SequenceEqual(expected)) throw new InvalidOperationException(
                        "筛选与全量基线不符：" + JsonSerializer.Serialize(new { expected, actual }));
                });
            }
        }
        finally { if ((object)table is IDisposable disposable) disposable.Dispose(); }
        bool success = checks.All(c => (bool)c.GetType().GetProperty("Success")!.GetValue(c)!);
        return JsonSerializer.Serialize(new { Success = success, Product = product, Checks = checks },
            new JsonSerializerOptions { WriteIndented = true });
    }
}
