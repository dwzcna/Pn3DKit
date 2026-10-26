namespace Pn3DKit.Utils;

/// <summary>本轮可读取的二维实体范围；未知类型必须说明原因，不能用未知数据证明一致。</summary>
public static class XrefBlockContentScope
{
    private static readonly HashSet<string> Entities = new(StringComparer.Ordinal)
    {
        "AcDbLine", "AcDbRay", "AcDbXline", "AcDbArc", "AcDbCircle", "AcDbEllipse", "AcDbPolyline",
        "AcDb2dPolyline", "AcDb2dVertex", "AcDbSpline", "AcDbText", "AcDbMText",
        "AcDbAttributeDefinition", "AcDbAttribute", "AcDbBlockReference", "AcDbMInsertBlock",
        "AcDbHatch", "AcDbSolid", "AcDbTrace", "AcDbPoint", "AcDbLeader", "AcDbMLeader",
        "AcDbAlignedDimension", "AcDbRotatedDimension", "AcDbRadialDimension", "AcDbDiametricDimension",
        "AcDb2LineAngularDimension", "AcDb3PointAngularDimension", "AcDbOrdinateDimension",
        "AcDbRadialDimensionLarge", "AcDbArcDimension", "AcDbWipeout"
    };

    /// <summary>以 SDK 实际 RX 类名核对覆盖范围；动态及代理状态另由适配层先行拒绝。</summary>
    public static bool SupportsEntity(string rxClass) => Entities.Contains(rxClass);
}
