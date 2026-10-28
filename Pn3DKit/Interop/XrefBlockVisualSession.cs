using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.GraphicsInterface;
using Autodesk.AutoCAD.GraphicsSystem;
using Pn3DKit.Models;
using Pn3DKit.Services;
using Pn3DKit.Utils;
using AcadApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;
using Bitmap = System.Drawing.Bitmap;
using GColor = System.Drawing.Color;
using Rectangle = System.Drawing.Rectangle;
using Size = System.Drawing.Size;
using View = Autodesk.AutoCAD.GraphicsSystem.View;
using Projection = Autodesk.AutoCAD.GraphicsSystem.Projection;

namespace Pn3DKit.Interop;

/// <summary>CAD 主线程专用离屏绘制会话；批次复用新建设备，从不取得或修改宿主缓存设备。</summary>
internal sealed class XrefBlockVisualSession : IDisposable
{
    private readonly Document _document;
    private readonly XrefBlockCadCatalog _catalog;
    private readonly Action _ensure;
    private readonly Action<string>? _stage;
    private readonly int _thread = Environment.CurrentManagedThreadId;
    private readonly int _defaultWeight;
    private GraphicsKernel? _kernel;
    private Device? _device;
    private View? _view;
    private VisualStyle? _style;
    private bool _disposed;
    private Bitmap? _normalized;
    private System.Drawing.Graphics? _normalizer;
    private byte[]? _devicePixels, _repeatedPixels;
    /// <summary>专用设备实际创建数量；无候选或全部不支持时为零。</summary>
    internal int DevicesCreated { get; private set; }
    /// <summary>宿主未清除完成标志但视图、设备及重复读回完整一致的帧数。</summary>
    internal int LegacyInvalidFlags { get; private set; }
    /// <summary>实际原生光栅绘制调用次数，包含线宽位置通道。</summary>
    internal int RasterCalls { get; private set; }
    /// <summary>整批规范化位图实际分配次数，固定分辨率时为一次。</summary>
    internal int NormalizationBuffers { get; private set; }

    /// <summary>只记录宿主及检查回调，不在构造时创建任何图形设备或内容读取器。</summary>
    internal XrefBlockVisualSession(Document document, XrefBlockCadCatalog catalog, Action ensure, Action<string>? stage = null)
    {
        _document = document; _catalog = catalog; _ensure = ensure; _stage = stage;
        _defaultWeight = Convert.ToInt32(AcadApp.GetSystemVariable("LWDEFAULT"), System.Globalization.CultureInfo.InvariantCulture);
        if (_defaultWeight < 0) throw new XrefVisualFatalException("无法取得可靠的默认线宽。");
    }

    /// <summary>每组先准备全部可靠范围，再采用公共视窗串行绘制；yield 后的像素由离线层立即压缩。</summary>
    internal IEnumerable<XrefVisualCapture> Capture(XrefBlockCandidate candidate)
    {
        var prepared = new List<(XrefBlockNameEntry Entry, ObjectId Target, XrefBlockVisualPreparation? Scene, string? Problem)>();
        return XrefVisualCleanup.Iterate(CaptureCore(candidate, prepared), () =>
        {
            var errors = new List<System.Exception>();
            foreach (var item in prepared.AsEnumerable().Reverse())
                try { item.Scene?.Dispose(); } catch (System.Exception ex) { errors.Add(ex); }
            if (errors.Count > 0) throw new AggregateException(errors);
        }, "候选来源资源释放失败，停止后续绘制。");
    }

    /// <summary>惰性准备与绘制；外层包装器负责候选资源收尾及异常合并。</summary>
    private IEnumerable<XrefVisualCapture> CaptureCore(XrefBlockCandidate candidate,
        List<(XrefBlockNameEntry Entry, ObjectId Target, XrefBlockVisualPreparation? Scene, string? Problem)> prepared)
    {
        Check();
        foreach (var entry in candidate.Entries.OrderBy(e => e.Source.DwgNo))
        {
            Check(); _stage?.Invoke("正在对比块 " + candidate.Name + "：准备来源 " + entry.Source.DwgNo + " " + Path.GetFileName(entry.Source.Drawing));
            ObjectId target = ObjectId.Null; XrefBlockVisualPreparation? scene = null; string? problem = null;
            try
            {
                var located = _catalog.LocateVisual(entry); target = located.Definition;
                scene = new(located.Database, target, _defaultWeight, Check);
                _catalog.VerifyVisual(entry, target);
                problem = scene.Problem;
            }
            catch (System.Exception ex) when (Ordinary(ex)) { problem = XrefBlockCadCatalog.Detail(ex); }
            catch (System.Exception ex)
            {
                try { scene?.Dispose(); }
                catch (System.Exception cleanup)
                { throw new XrefVisualFatalException("候选准备及释放均失败。", new AggregateException(ex, cleanup)); }
                throw;
            }
            prepared.Add((entry, target, scene, problem));
        }
        var reliable = prepared.Where(p => p.Scene != null && p.Problem == null).ToArray();
        VisualFrame? frame = null; string? frameProblem = null;
        try { frame = BlockVisualComparison.Frame(reliable.Where(p => p.Scene!.Bounds != null).Select(p => p.Scene!.Bounds!.Value)); }
        catch (ArgumentException ex) { frameProblem = ex.Message; }
        var weights = reliable.SelectMany(p => p.Scene!.Weights).Distinct().OrderBy(w => w).ToArray();
        var palette = weights.Select((w, i) => (Weight: w, Color: (uint)((i + 1) * 0x9E3779) & 0xFFFFFF))
            .ToDictionary(p => p.Weight, p => p.Color);
        foreach (var item in prepared)
        {
            Check();
            if (item.Scene == null || item.Problem != null || frame == null)
            { yield return new(item.Entry, null, item.Problem ?? frameProblem ?? "无法准备候选来源。"); continue; }
            _catalog.VerifyVisual(item.Entry, item.Target);
            _stage?.Invoke("正在对比块 " + candidate.Name + "：绘制来源 " + item.Entry.Source.DwgNo + "（公共视窗／双背景）");
            VisualSample? sample = null; string? problem = null;
            try
            {
                EnsureDevice();
                var light = Draw(item.Scene, frame, GColor.White);
                var dark = Draw(item.Scene, frame, GColor.FromArgb(24, 32, 40));
                if (item.Scene.HasVisibleContent && !HasInk(light, GColor.White) && !HasInk(dark, GColor.FromArgb(24, 32, 40)))
                    throw new InvalidOperationException("非空内容意外得到双背景空白图，不能验证。");
                byte[]? code = null;
                if (weights.Length > 1 && item.Scene.Weights.Length > 1)
                {
                    item.Scene.EncodeWeights(palette);
                    code = Draw(item.Scene, frame, GColor.Black);
                }
                sample = new(item.Entry.Source.DwgNo, frame, light, dark, item.Scene.Colors, item.Scene.Weights,
                    true, true, code, true, null);
            }
            catch (System.Exception ex) when (Ordinary(ex)) { problem = XrefBlockCadCatalog.Detail(ex); }
            Check(); _catalog.VerifyVisual(item.Entry, item.Target);
            yield return new(item.Entry, sample, problem);
        }
    }

    /// <summary>只把普通候选读取／绘制失败降为未验证；取消、上下文、输出和设备故障必须停止。</summary>
    private static bool Ordinary(System.Exception ex) => ex is not OperationCanceledException
        and not XrefVisualFatalException and not XrefBlockLogException and not Pn3DKit.Commands.XrefCheckStoppedException
        && ex is not Autodesk.AutoCAD.Runtime.Exception { ErrorStatus: Autodesk.AutoCAD.Runtime.ErrorStatus.UserBreak };

    /// <summary>第一次可靠来源需要绘制时创建专用内核、设备及视图，整批最多一套。</summary>
    private void EnsureDevice()
    {
        Check();
        if (_device != null) return;
        try
        {
            using var descriptor = new KernelDescriptor();
            descriptor.addRequirement(Autodesk.AutoCAD.UniqueString.Intern("3D Drawing"));
            _kernel = Manager.AcquireGraphicsKernel(descriptor);
            _device = _document.GraphicsManager.CreateAutoCADOffScreenDevice(_kernel);
            _device.OnSize(new Size(2048, 2048));
            _style = new VisualStyle(VisualStyleType.Wireframe2D);
            _style.SetTrait(VisualStyleProperty.UseDrawOrder, true, VisualStyleOperation.Set);
            _style.SetTrait(VisualStyleProperty.ViewportTransparency, true, VisualStyleOperation.Set);
            DevicesCreated++;
        }
        catch (System.Exception ex) { throw new XrefVisualFatalException("专用离屏设备初始化失败。", ex); }
        Check();
    }

    /// <summary>切换仅限临时 WorkingDatabase 的同步作用域；任何异常均恢复，返回规范 BGRA32。</summary>
    private byte[] Draw(XrefBlockVisualPreparation scene, VisualFrame frame, GColor background)
    {
        Check();
        var previous = HostApplicationServices.WorkingDatabase;
        Model? model = null;
        System.Exception? failure = null;
        try
        {
            HostApplicationServices.WorkingDatabase = scene.Database;
            model = _document.GraphicsManager.CreateAutoCADModel(_kernel!);
            model.LinetypesEnabled = true;
            using var transaction = scene.Database.TransactionManager.StartOpenCloseTransaction();
            var table = (BlockTable)transaction.GetObject(scene.Database.BlockTableId, OpenMode.ForRead);
            var drawable = (BlockTableRecord)transaction.GetObject(table[BlockTableRecord.ModelSpace], OpenMode.ForRead);
            var viewport = (ViewportTableRecord)transaction.GetObject(scene.Database.CurrentViewportTableRecordId, OpenMode.ForRead);
            _view = _document.GraphicsManager.CreateViewport(_device!, viewport);
            if (_view.Device == null) _device!.Add(_view);
            _view.VisualStyle = _style!; _view.ClearFrozenLayers(); _view.Show();
            _view!.EraseAll(); _device!.OnSize(new Size(frame.Pixels, frame.Pixels)); _device.BackgroundColor = background;
            var target = new Point3d(frame.CenterX, frame.CenterY, 0);
            _view.SetView(target + Vector3d.ZAxis * frame.Span, target, Vector3d.YAxis, frame.Span, frame.Span, Projection.Parallel);
            _view.ViewportExtents = new Extents2d(new Point2d(0, 0), new Point2d(1, 1));
            _view.Add(drawable, model);
            _view.VisualStyle = _style!; model.VisualStyle = _style!; model.LinetypesEnabled = true;
            _device.Invalidate(); _device.Update(); RasterCalls++;
            _view.Update();
            // 2026 原型的 IsValid 始终为 false，文字首次读回还存在延迟更新；有界核对三路完整像素。
            int required = checked(frame.Pixels * frame.Pixels * 4);
            PreparePixelBuffers(frame.Pixels, required);
            var pixels = new byte[required];
            void Read(bool device, byte[] destination)
            {
                using var snapshot = device
                    ? _device.GetSnapshot(new Rectangle(0, 0, frame.Pixels, frame.Pixels))
                    : _view.GetSnapshot(new Rectangle(0, 0, frame.Pixels, frame.Pixels));
                if (snapshot == null || snapshot.Width != frame.Pixels || snapshot.Height != frame.Pixels)
                    throw new XrefVisualFatalException("离屏读回为空或尺寸与公共视窗不符。");
                _normalizer!.DrawImageUnscaled(snapshot, 0, 0);
                CopyPixels(_normalized!, destination);
            }
            for (int attempt = 0; attempt < 3; attempt++)
            {
                Check();
                Read(false, pixels);
                Read(true, _devicePixels!);
                Read(false, _repeatedPixels!);
                if (pixels.AsSpan().SequenceEqual(_devicePixels.AsSpan(0, required))
                    && pixels.AsSpan().SequenceEqual(_repeatedPixels.AsSpan(0, required)))
                {
                    if (!_device.IsValid) LegacyInvalidFlags++;
                    Check();
                    return pixels;
                }
                _view.Update(); _device.Update();
            }
            throw new XrefVisualFatalException("三轮同步读回仍未稳定，不能确认离屏绘制完成。");
        }
        catch (System.Exception ex) { failure = ex; throw; }
        finally
        {
            var errors = new List<System.Exception>();
            try { _view?.EraseAll(); } catch (System.Exception ex) { errors.Add(ex); }
            try { if (_view != null) _device?.Erase(_view); } catch (System.Exception ex) { errors.Add(ex); }
            try { _view?.Dispose(); _view = null; } catch (System.Exception ex) { errors.Add(ex); }
            try { model?.Dispose(); } catch (System.Exception ex) { errors.Add(ex); }
            try { HostApplicationServices.WorkingDatabase = previous; } catch (System.Exception ex) { errors.Add(ex); }
            XrefVisualCleanup.ThrowIfFailed(failure, errors, "释放离屏模型、清理视图或恢复 WorkingDatabase 失败。");
        }
    }

    /// <summary>逐行复制有效像素，处理行填充和有符号 stride，不把原生指针交给后台。</summary>
    internal static byte[] Pixels(Bitmap bitmap)
    {
        var pixels = new byte[checked(bitmap.Width * bitmap.Height * 4)];
        CopyPixels(bitmap, pixels);
        return pixels;
    }

    /// <summary>向已拥有的输出或临时缓冲复制；只处理有效像素，不暴露池化数组。</summary>
    private static void CopyPixels(Bitmap bitmap, byte[] destination)
    {
        int rowBytes = checked(bitmap.Width * 4);
        int required = checked(rowBytes * bitmap.Height);
        if (destination.Length < required) throw new InvalidOperationException("读回缓冲过小。");
        var bounds = new Rectangle(0, 0, bitmap.Width, bitmap.Height);
        var bits = bitmap.LockBits(bounds, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            if (Math.Abs((long)bits.Stride) < rowBytes) throw new InvalidOperationException("原生图像行跨度无效。");
            for (int y = 0; y < bitmap.Height; y++)
                Marshal.Copy(IntPtr.Add(bits.Scan0, checked(y * bits.Stride)), destination, y * rowBytes, rowBytes);
        }
        finally { bitmap.UnlockBits(bits); }
    }

    /// <summary>仅纯图像资源按分辨率复用；返回的样本数组始终单独拥有。</summary>
    private void PreparePixelBuffers(int pixels, int required)
    {
        if (_normalized != null && _normalized.Width == pixels && _normalized.Height == pixels) return;
        ReleasePixelBuffers();
        _normalized = new Bitmap(pixels, pixels, PixelFormat.Format32bppArgb);
        _normalizer = System.Drawing.Graphics.FromImage(_normalized);
        _normalizer.CompositingMode = System.Drawing.Drawing2D.CompositingMode.SourceCopy;
        _devicePixels = System.Buffers.ArrayPool<byte>.Shared.Rent(required);
        _repeatedPixels = System.Buffers.ArrayPool<byte>.Shared.Rent(required);
        NormalizationBuffers++;
    }

    /// <summary>关闭图像资源并清除后归还所有池化缓冲，任何失败仍继续清理。</summary>
    private void ReleasePixelBuffers()
    {
        var resources = new List<System.Exception>();
        try { _normalizer?.Dispose(); } catch (System.Exception ex) { resources.Add(ex); }
        try { _normalized?.Dispose(); } catch (System.Exception ex) { resources.Add(ex); }
        try { if (_devicePixels != null) System.Buffers.ArrayPool<byte>.Shared.Return(_devicePixels, clearArray: true); }
        catch (System.Exception ex) { resources.Add(ex); }
        try { if (_repeatedPixels != null) System.Buffers.ArrayPool<byte>.Shared.Return(_repeatedPixels, clearArray: true); }
        catch (System.Exception ex) { resources.Add(ex); }
        _normalizer = null; _normalized = null; _devicePixels = null; _repeatedPixels = null;
        if (resources.Count != 0) throw new XrefVisualFatalException("释放图像缓冲失败。", new AggregateException(resources));
    }

    /// <summary>双背景联合判断空图；不能把缺失内容的纯背景作为相同证据。</summary>
    private static bool HasInk(byte[] pixels, GColor background)
    {
        for (int i = 0; i < pixels.Length; i += 4)
            if (pixels[i] != background.B || pixels[i + 1] != background.G || pixels[i + 2] != background.R) return true;
        return false;
    }

    /// <summary>同步检查 CAD 主线程、原图身份及取消；释放路径不依赖原图仍然存在。</summary>
    private void Check()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(XrefBlockVisualSession));
        if (_thread != Environment.CurrentManagedThreadId) throw new XrefVisualFatalException("CAD 绘制必须在创建会话的主线程串行执行。");
        _ensure();
    }

    /// <summary>仅释放本会话创建的图形对象，继续清理其余资源后汇总所有错误。</summary>
    public void Dispose()
    {
        if (_disposed) return; _disposed = true;
        var errors = new List<System.Exception>();
        void Close(Action action) { try { action(); } catch (System.Exception ex) { errors.Add(ex); } }
        Close(() => _view?.EraseAll()); Close(() => _device?.EraseAll());
        Close(() => _view?.Dispose()); Close(() => _style?.Dispose()); Close(() => _device?.Dispose()); Close(ReleasePixelBuffers);
        if (_kernel != null) Close(() => Manager.ReleaseGraphicsKernel(_kernel));
        if (errors.Count > 0) throw new XrefVisualFatalException("释放专用离屏设备失败。", new AggregateException(errors));
    }
}
