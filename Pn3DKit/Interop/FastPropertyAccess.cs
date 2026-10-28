using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Pn3DKit.Models;

namespace Pn3DKit.Interop;

/// <summary>隔离可选 Plant 模块，界面和路径输入在普通 CAD 中仍可使用。</summary>
internal interface IFastPlantProperties
{
    bool IsCurrent { get; }
    IReadOnlyList<PnPropertyCatalogEntry> Catalog(ObjectId id);
    PnObjectProperties Read(ObjectId id, Document document, Transaction transaction, IReadOnlyList<PnPropertySpec> specs);
    PnPropertyWritePlan<ObjectId> Prepare(ObjectId id, Document document, PnPropertySpec spec, string? original, string value);
    string Write(ObjectId id, Document document, PnPropertySpec spec, string? original, string value, Action<string> validate);
}

/// <summary>主线程访问门面；缺少 Plant 时仅显示不适用，不强制加载模块。</summary>
internal sealed class FastPropertyAccess
{
    private readonly IFastPlantProperties _plant;
    internal FastPropertyAccess(bool busy)
    {
        try { _plant = FastPlantPropertiesAccess.Capture(busy); }
        catch (System.Exception ex) { _plant = new UnavailablePlant(Describe(ex)); }
    }
    internal bool IsCurrent => _plant.IsCurrent;

    /// <summary>调用方在主线程读锁内检索当前对象自身属性；不求值位号或探测写入。</summary>
    internal IReadOnlyList<PnPropertyCatalogEntry> Catalog(ObjectId id)
    {
        if (id.IsNull || !id.IsValid || id.IsErased) throw new InvalidOperationException("对象已失效或删除。");
        return _plant.Catalog(id);
    }

    /// <summary>以只读事务读取自身属性；单个失效对象不从多选总量中丢弃。</summary>
    internal PnObjectProperties Read(ObjectId id, Document document, Transaction transaction,
        IReadOnlyList<PnPropertySpec> specs)
    {
        try
        {
            if (id.IsNull || !id.IsValid || id.IsErased) throw new InvalidOperationException("对象已失效或删除。");
            return _plant.Read(id, document, transaction, specs) with { Label = Label(id) };
        }
        catch (System.Exception ex)
        {
            string error = Describe(ex);
            return new("", "读取失败", specs.Select(s => new PnPropertyCell(s, FastPropertyValue.Error(error),
                PnEditPermission.ReadOnly(error))).ToArray());
        }
    }

    /// <summary>只读预检同一新值的字段类型、长度和登记占用，实际写入仍重复检查。</summary>
    internal PnPropertyWritePlan<ObjectId> Prepare(ObjectId id, Document document, PnPropertySpec spec, string? original, string value) =>
        _plant.Prepare(id, document, spec, original, value);

    /// <summary>可容错的目标标签，不使用句柄代替完整 ObjectId 身份。</summary>
    internal static string Label(ObjectId id)
    {
        try { return "句柄 " + id.Handle; }
        catch (System.Exception) { return "已失效对象"; }
    }

    /// <summary>只在正确命令上下文、写前校验后调用；具体适配再次核对权限并回读。</summary>
    internal string Write(ObjectId id, Document document, PnPropertySpec spec, string? original, string value, Action<string> validate) =>
        _plant.Write(id, document, spec, original, value, validate);

    internal static string Describe(System.Exception ex) => ex is Autodesk.AutoCAD.Runtime.Exception cad
        ? $"SDK ErrorStatus={cad.ErrorStatus}：{cad.Message}" : ex.Message;

    private sealed class UnavailablePlant(string detail) : IFastPlantProperties
    {
        public bool IsCurrent => true;
        public IReadOnlyList<PnPropertyCatalogEntry> Catalog(ObjectId id) =>
            throw new InvalidOperationException("当前 Plant 环境不可用：" + detail);
        public PnObjectProperties Read(ObjectId id, Document document, Transaction transaction, IReadOnlyList<PnPropertySpec> specs) =>
            new("", "所选对象", specs.Select(s => new PnPropertyCell(s, FastPropertyValue.Missing("当前 Plant 环境不可用：" + detail),
                PnEditPermission.ReadOnly("当前 Plant 环境不可用。"))).ToArray());
        public PnPropertyWritePlan<ObjectId> Prepare(ObjectId id, Document document, PnPropertySpec spec, string? original, string value) =>
            throw new InvalidOperationException("当前 Plant 环境不可用。");
        public string Write(ObjectId id, Document document, PnPropertySpec spec, string? original, string value, Action<string> validate) =>
            throw new InvalidOperationException("当前 Plant 环境不可用。");
    }
}
