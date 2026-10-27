using Autodesk.ProcessPower.DataObjects;
using Autodesk.ProcessPower.ProjectManager;
using Pn3DKit.Interop;
using CadException = Autodesk.AutoCAD.Runtime.Exception;
using ErrorStatus = Autodesk.AutoCAD.Runtime.ErrorStatus;

namespace Pn3DKit.Tests;

/// <summary>链接生产类别显示名适配的离线回归；不把替身名称当作真实 SDK 本地化验收。</summary>
public sealed class PlantClassDisplayNameTests
{
    private const string ClassName = "Primary";
    private static PnPDatabase Database(PnPTable table) => new(() => new Dictionary<string,PnPTable> { [ClassName] = table });

    /// <summary>项目名称有效即返回，即使表集合读取会失败也不访问。</summary>
    [Fact] public void ProjectNameWinsWithoutReadingTable()
    {
        var part = new Project(_ => "主工艺管线");
        var database = new PnPDatabase(() => throw new InvalidOperationException("不应读取表"));
        Assert.Equal("主工艺管线", PlantTagCatalogAccess.ReadDisplayName(part, database, ClassName));
        Assert.Equal(new[] { ClassName }, part.Reads); Assert.Equal(0,database.Reads);
    }

    /// <summary>不同内部类别通过实际接口取得名称，不对主／次管线或中文做硬编码。</summary>
    [Theory]
    [InlineData("Primary", "主工艺管线")]
    [InlineData("Secondary", "次工艺管线")]
    [InlineData("CustomEquipment", "Équipement personnalisé")]
    public void ArbitraryClassUsesProjectConfiguration(string className, string display)
    {
        var part = new Project(name => name == className ? display : throw new InvalidOperationException());
        var database = new PnPDatabase(() => throw new InvalidOperationException());
        Assert.Equal(display,PlantTagCatalogAccess.ReadDisplayName(part,database,className));
        Assert.Equal(className,Assert.Single(part.Reads));
    }

    /// <summary>项目空值按原类别查表回退，保留 SDK 的显示名称属性标识。</summary>
    [Theory] [InlineData(null)] [InlineData("")] [InlineData(" \t")]
    public void MissingProjectNameFallsBackToTable(string? name)
    {
        var table = new PnPTable(_ => "Primary Line Segment");var database = Database(table);
        Assert.Equal("Primary Line Segment",PlantTagCatalogAccess.ReadDisplayName(new(_ => name),database,ClassName));
        Assert.Equal(1,database.Reads);Assert.Equal(PnPDatabase.DisplayNameAttribute,Assert.Single(table.Reads));
    }

    /// <summary>项目普通读取错误允许继续查表。</summary>
    [Fact] public void ProjectReadErrorFallsBackToTable()
    {
        Assert.Equal("表配置",PlantTagCatalogAccess.ReadDisplayName(new(_ => throw new IOException("项目读取失败")),
            Database(new(_ => "表配置")),ClassName));
    }

    /// <summary>两层均无有效配置时返回原内部名称，不能制造空标题。</summary>
    [Theory] [InlineData(null)] [InlineData("")] [InlineData("\r\n")]
    public void MissingBothNamesUsesInternalClass(string? name) => Assert.Equal(ClassName,
        PlantTagCatalogAccess.ReadDisplayName(new(_ => ""),Database(new(_ => name)),ClassName));

    /// <summary>两层普通异常不会阻断扫描，最终仍保留内部名称。</summary>
    [Fact] public void OrdinaryErrorsAtBothLevelsUseInternalName() => Assert.Equal(ClassName,
        PlantTagCatalogAccess.ReadDisplayName(new(_ => throw new IOException("项目")),
            Database(new(_ => throw new InvalidOperationException("表"))),ClassName));

    /// <summary>缺失表也只回退名称，不改变类别身份或要求创建表。</summary>
    [Fact] public void AbsentTableUsesInternalName() => Assert.Equal(ClassName,
        PlantTagCatalogAccess.ReadDisplayName(new(_ => ""),new(() => new Dictionary<string,PnPTable>()),ClassName));

    /// <summary>任一级的托管取消和 SDK UserBreak 都必须传播，不能显示成正常回退结果。</summary>
    [Theory] [InlineData(false,false)] [InlineData(false,true)] [InlineData(true,false)] [InlineData(true,true)]
    public void CancellationFromEitherProviderPropagates(bool tableLevel,bool sdk)
    {
        System.Exception error = sdk ? new CadException(ErrorStatus.UserBreak) : new OperationCanceledException("已取消");
        var part = new Project(_ => tableLevel ? "" : throw error);
        var database = Database(new(_ => throw error));
        var actual = Assert.Throws<OperationCanceledException>(() => PlantTagCatalogAccess.ReadDisplayName(part,database,ClassName));
        if(sdk)Assert.Same(error,actual.InnerException);else Assert.Same(error,actual);
        Assert.Equal(tableLevel?1:0,database.Reads);
    }

    /// <summary>同名内部类别在不同项目部分独立读取，不能误用另一部分的显示名。</summary>
    [Fact] public void SeparateProjectPartsKeepIndependentNames()
    {
        var pAndId = new Project(_ => "工艺类别");var piping = new Project(_ => "管道类别");
        var database = new PnPDatabase(() => throw new InvalidOperationException());
        Assert.Equal("工艺类别",PlantTagCatalogAccess.ReadDisplayName(pAndId,database,ClassName));
        Assert.Equal("管道类别",PlantTagCatalogAccess.ReadDisplayName(piping,database,ClassName));
        Assert.Equal(new[] {ClassName},pAndId.Reads);Assert.Equal(new[] {ClassName},piping.Reads);
    }
}
