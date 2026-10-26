using Pn3DKit.Models;
using Pn3DKit.Utils;

namespace Pn3DKit.Tests;

/// <summary>属性目录的完整性、显示名称回退、规范化追加及生命周期校验。</summary>
public sealed class PnPropertyCatalogTests
{
    /// <summary>目录不按权限隐藏字段；继承、自定义及系统字段均沿用 SDK 返回的键。</summary>
    [Fact]
    public void CatalogRetainsAllKindsOfPropertiesAndFallbackDetails()
    {
        var entries = PnPropertyCatalogPolicy.Build(["Size", "Comment", "Area", "CustomNote", "PnPID", "size"],
            name => name switch
            {
                "Area" => "绘图区域", "Size" => "尺寸", "Comment" => "注释",
                "CustomNote" => throw new InvalidOperationException("SDK 元数据失败"), _ => null
            });
        Assert.Equal(5, entries.Count);
        Assert.Equal("绘图区域", entries.Single(x => x.PropertyPath == "Area").FriendlyDisplayName);
        Assert.Equal("PnPID", entries.Single(x => x.PropertyPath == "PnPID").FriendlyDisplayName);
        var failed = entries.Single(x => x.PropertyPath == "CustomNote");
        Assert.Equal("CustomNote", failed.FriendlyDisplayName); Assert.Contains("SDK 元数据失败", failed.Detail);
        Assert.Equal(entries.OrderBy(x => x.PropertyPath, StringComparer.CurrentCultureIgnoreCase), entries);
    }

    /// <summary>完整路径和简写按同一身份去重；非法路径不追加，原有清单不改变。</summary>
    [Fact]
    public void AppendOnlyReturnsNewNormalizedProperties()
    {
        string[] current = ["Comment", "TargetObject.Area", ""];
        var result = PnPropertyCatalogPolicy.MissingPaths(current,
            ["area", "Size", "TargetObject.size", "COMMENT", "Not.A.Property", "LoopNumber"]);
        Assert.Equal(new[] { "Size", "LoopNumber" }, result);
        Assert.Equal(new[] { "Comment", "TargetObject.Area", "" }, current);
        Assert.Empty(PnPropertyCatalogPolicy.MissingPaths(current, []));
    }

    /// <summary>项目级名称按提供的有效配置展示，不改变属性身份，也不强制翻译有效英文自定义名称。</summary>
    [Fact]
    public void ProjectLabelsPreservePropertyKeysAndCustomNames()
    {
        var labels = new Dictionary<string, string>
        { ["Supplier"] = "供应商", ["Tag"] = "位号", ["Number"] = "编号", ["CustomNote"] = "Project note" };
        var entries = PnPropertyCatalogPolicy.Build(labels.Keys, name => labels[name]);
        Assert.Equal(labels.Count, entries.Count);
        Assert.All(entries, entry => Assert.Equal(labels[entry.PropertyPath], entry.FriendlyDisplayName));
        Assert.Equal("Project note", entries.Single(x => x.PropertyPath == "CustomNote").FriendlyDisplayName);
        Assert.Equal(new[] { "Supplier", "Tag", "Number" },
            PnPropertyCatalogPolicy.MissingPaths([], ["Supplier", "Tag", "Number"]));
    }

    /// <summary>不同内部字段可以使用同一友好名，显示名称不参与合并或属性寻址。</summary>
    [Fact]
    public void EqualFriendlyNamesKeepSeparatePropertyIdentities()
    {
        var entries = PnPropertyCatalogPolicy.Build(["Area", "Location"], _ => "位置");
        Assert.Equal(2, entries.Count);
        Assert.Equal(new[] { "Area", "Location" }, entries.Select(x => x.PropertyPath));
        Assert.All(entries, x => Assert.Equal("位置", x.FriendlyDisplayName));
    }

    /// <summary>每种过期或不可操作状态均拒绝追加，完整身份不以文件名或包装引用替代。</summary>
    [Theory]
    [InlineData("identity")]
    [InlineData("target")]
    [InlineData("epoch")]
    [InlineData("link")]
    [InlineData("multi")]
    [InlineData("project")]
    [InlineData("busy")]
    [InlineData("hidden")]
    public void StaleCatalogCannotBeApplied(string change)
    {
        var expected = new PnPropertyCatalogContext<int, int>(1, 2, 3, "PnId:10:Valve", 1);
        var current = change switch
        {
            "identity" => expected with { Identity = 99 },
            "target" => expected with { Target = 99 },
            "epoch" => expected with { Epoch = 4 },
            "link" => expected with { TargetKey = "PnId:11:Valve" },
            "multi" => expected with { SelectionCount = 2 }, _ => expected
        };
        Assert.NotNull(PnPropertyCatalogPolicy.Validate(expected, current, change != "project", change == "busy", change != "hidden"));
        Assert.Null(PnPropertyCatalogPolicy.Validate(expected, expected, true, false, true));
    }
}
