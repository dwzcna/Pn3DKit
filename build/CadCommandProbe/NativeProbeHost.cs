using System.Reflection;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Pn3DKit.Models;
using Pn3DKit.Utils;
using AcadApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;

namespace Pn3DKit.CadCommandProbe;

/// <summary>测试直接主线程状态读取与原生命令；不调用旧的只读命令上下文切换。</summary>
internal sealed class NativeProbeHost(ICadHost legacy, Document document, Action ensure, Func<object[], Task> run) : ICadHost
{
    /// <inheritdoc />
    public ICadDrawing? OriginalDrawing => legacy.OriginalDrawing;
    /// <inheritdoc />
    public void EnsureProject() => legacy.EnsureProject();
    /// <inheritdoc />
    public ICadDrawing? FindOpen(string source) => legacy.FindOpen(source);
    /// <inheritdoc />
    public Task<ICadDrawing> OpenAsync(string source) => throw new NotSupportedException("原型只处理已打开的工具副本。");
    /// <inheritdoc />
    public Task ActivateAsync(ICadDrawing drawing) => legacy.ActivateAsync(drawing);

    /// <summary>核对完整身份；opaque token反射仅用于独立原型，不进入产品。</summary>
    private void Verify(ICadDrawing drawing)
    {
        ensure();
        var property = drawing.GetType().GetProperty("Identity", BindingFlags.Instance | BindingFlags.NonPublic);
        if (property?.GetValue(drawing) is not ObjectId id || id != document.Database.BlockTableId)
            throw new CadOperationException("原型读取的数据库身份不符。");
    }

    /// <inheritdoc />
    public Task<DrawingState> ReadStateAsync(ICadDrawing drawing)
    {
        Verify(drawing);
        return Task.FromResult(new DrawingState(Convert.ToInt32(AcadApp.GetSystemVariable("DBMOD")) != 0, document.IsReadOnly));
    }

    /// <inheritdoc />
    public Task SaveAsync(ICadDrawing drawing, string marker)
    {
        Verify(drawing);
        return run(["_.QSAVE"]);
    }

    /// <inheritdoc />
    public Task ExportAsync(ICadDrawing drawing, string temporary, string version, string marker)
    {
        Verify(drawing);
        return run(ExportPolicy.ExportArguments(temporary, version));
    }

    /// <inheritdoc />
    public Task CloseAsync(ICadDrawing drawing) => throw new NotSupportedException("原型仅在明确结束后独立关闭只读副本。");
}
