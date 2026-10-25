using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Pn3DKit.Models;

namespace Pn3DKit.Interop;

/// <summary>隔离可选 Plant 模块，界面和路径输入在普通 CAD 中仍可使用。</summary>
internal interface IFastPlantProperties
{
    bool IsCurrent { get; }
    PnObjectProperties Read(ObjectId id, Document document, Transaction transaction, IReadOnlyList<PnPropertySpec> specs, bool single);
    string Write(ObjectId id, Document document, PnPropertySpec spec, string original, string value, Action<string> validate);
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

    /// <summary>以只读事务读取自身属性；单个失效对象不从多选总量中丢弃。</summary>
    internal PnObjectProperties Read(ObjectId id, Document document, Transaction transaction,
        IReadOnlyList<PnPropertySpec> specs, bool single)
    {
        try
        {
            if (id.IsNull || !id.IsValid || id.IsErased) throw new InvalidOperationException("对象已失效或删除。");
            return _plant.Read(id, document, transaction, specs, single);
        }
        catch (System.Exception ex)
        {
            string error = Describe(ex);
            return new("", "读取失败", specs.Select(s => new PnPropertyCell(s, FastPropertyValue.Error(error),
                PnEditPermission.ReadOnly(error))).ToArray());
        }
    }

    /// <summary>只在正确命令上下文、写前校验后调用；具体适配再次核对权限并回读。</summary>
    internal string Write(ObjectId id, Document document, PnPropertySpec spec, string original, string value, Action<string> validate) =>
        _plant.Write(id, document, spec, original, value, validate);

    internal static string Describe(System.Exception ex) => ex is Autodesk.AutoCAD.Runtime.Exception cad
        ? $"SDK ErrorStatus={cad.ErrorStatus}：{cad.Message}" : ex.Message;

    private sealed class UnavailablePlant(string detail) : IFastPlantProperties
    {
        public bool IsCurrent => true;
        public PnObjectProperties Read(ObjectId id, Document document, Transaction transaction, IReadOnlyList<PnPropertySpec> specs, bool single) =>
            new("", "所选对象", specs.Select(s => new PnPropertyCell(s, FastPropertyValue.Missing("当前 Plant 环境不可用：" + detail),
                PnEditPermission.ReadOnly("当前 Plant 环境不可用。"))).ToArray());
        public string Write(ObjectId id, Document document, PnPropertySpec spec, string original, string value, Action<string> validate) =>
            throw new InvalidOperationException("当前 Plant 环境不可用。");
    }
}
