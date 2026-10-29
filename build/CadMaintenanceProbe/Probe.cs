using System.IO;
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
        // 使用官方空项目架构的独占副本；PnPID等系统列由SDK继承，不仿造项目结构。
        string fixture = Path.Combine(output, "QueryFixture.dcf");
        File.Copy(@"C:\Program Files\Autodesk\AutoCAD 2026\PLNT3D\UserDataCache\zh-CN\DefaultProject\ProcessPower.dcf", fixture, false);
        File.SetAttributes(fixture, FileAttributes.Normal);
        using var database = PnPDatabase.Open(fixture)
            ?? throw new InvalidOperationException("SDK未打开独立查询测试数据库。");
        var table = new PnPTable("Pn3DKit_QueryFixture_" + Guid.NewGuid().ToString("N"), database.Tables["PnPBase"], false);
        try
        {
            database.Tables.Add(table);
            table.Columns.Add(new PnPColumn("Tag", typeof(string), 255));
            table.Columns.Add(new PnPColumn("Measure", typeof(double), 0));
            database.AcceptSchemaChanges();
            foreach (string value in new[] { "Valve", "VALVE", "V'1", "阀%_[1]", "Pump", "Ä1", "ä1", "Ι1", "ι1", "Ä%_[1]'\\x", "ä%_[1]'\\x", "K1", "K1", "S1", "ſ1", "𐐀1", "𐐨1" })
            {
                var row = table.NewRow(); row["Tag"] = value; table.Rows.Add(row);
            }
            database.AcceptChanges();
            foreach (string value in new[] { "Valve", "V'1", "阀%_[1]", "Missing", "Ä1", "Ι1", "Ä%_[1]'\\x", "K1", "S1", "𐐀1" })
            {
                Check("SDK Select 字面量及大小写：" + value, () =>
                {
                    var expected = table.Select().Where(r => string.Equals(Convert.ToString(r["Tag"]), value,
                        StringComparison.OrdinalIgnoreCase)).Select(r => Convert.ToString(r["Tag"])).Order().ToArray();
                    var actual = table.Select(ProjectTagLookup.Filter(value)).Where(r => string.Equals(Convert.ToString(r["Tag"]),value,StringComparison.OrdinalIgnoreCase))
                        .Select(r => Convert.ToString(r["Tag"])).Order().ToArray();
                    if (!actual.SequenceEqual(expected)) throw new InvalidOperationException(
                        "筛选与全量基线不符：" + JsonSerializer.Serialize(new { expected, actual }));
                });
            }
            var child = new PnPTable(table.Name + "_Child", table, false);
            database.Tables.Add(child);
            database.AcceptSchemaChanges();
            var inherited = child.NewRow();
            inherited["Tag"] = "vAlVe";
            inherited["Measure"] = 0d;
            child.Rows.Add(inherited);
            database.AcceptChanges();
            Check("SDK继承筛选及重复来源去重", () =>
            {
                var expected = table.Select().Where(r => string.Equals(Convert.ToString(r["Tag"]), "Valve",
                    StringComparison.OrdinalIgnoreCase)).Select(r => Convert.ToInt32(r["PnPID"])).Distinct().Order().ToArray();
                var actual = ProjectTagLookup.Owners("Valve", -1, filter => table.Select(filter).Concat(child.Select(filter))
                    .Select(r => (Convert.ToInt32(r["PnPID"]), Convert.ToString(r["Tag"])!)));
                if (!actual.SequenceEqual(expected) || !actual.Contains(Convert.ToInt32(inherited["PnPID"])))
                    throw new InvalidOperationException("继承行或去重结果不符。");
            });
            Check("真实SDK数值规范化与类型化回读", () =>
            {
                inherited["Measure"] = "01";
                string actual = Convert.ToString(inherited["Measure"], System.Globalization.CultureInfo.CurrentCulture)!;
                var plan = new Pn3DKit.Models.PnPropertyWritePlan<int>(Convert.ToInt32(inherited["PnPID"]), "fixture", "0", "01", null)
                    { ValueType = typeof(double), ValueCulture = System.Globalization.CultureInfo.CurrentCulture.Name };
                if (!PnPropertyValuePolicy.Equivalent(plan, actual) || actual == "01")
                    throw new InvalidOperationException("真实SDK未进行预期数值规范化：" + actual);
                database.AcceptChanges();
            });
        }
        finally { if ((object)table is IDisposable disposable) disposable.Dispose(); }
        bool success = checks.All(c => (bool)c.GetType().GetProperty("Success")!.GetValue(c)!);
        return JsonSerializer.Serialize(new { Success = success, Product = product, FilterUnderTest = "ProductionProjectTagLookup", Checks = checks },
            new JsonSerializerOptions { WriteIndented = true });
    }

}
