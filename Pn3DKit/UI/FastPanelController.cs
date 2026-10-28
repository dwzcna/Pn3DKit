using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.Windows;
using Pn3DKit.Interop;
using AcadApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;

namespace Pn3DKit.UI;

/// <summary>惰性管理单例原生可停靠面板和主线程属性刷新器。</summary>
internal static class FastPanelController
{
    private static PaletteSet? _palette;
    private static FastPropertiesView? _view;
    private static bool _themeHooked;
    private static FastSelectionController? _selection;

    /// <summary>在 CAD 主线程首次创建浮动面板；关闭后复用实例并保留本会话手动位置。</summary>
    internal static void Show()
    {
        if (_palette == null)
        {
            var view = new FastPropertiesView();
            var palette = new PaletteSet("PN快速特性", new Guid("D4AE77B1-2AE3-4CE2-A2BD-766553E84BA9"));
            try
            {
                palette.Style = PaletteSetStyles.ShowCloseButton | PaletteSetStyles.ShowAutoHideButton | PaletteSetStyles.ShowPropertiesMenu;
                palette.DockEnabled = DockSides.Left | DockSides.Right;
                palette.DeviceIndependentSize = new(320, 480);
                palette.DeviceIndependentMinimumSize = new(280, 300);
                palette.KeepFocus = false;
                palette.AddVisual("PN快速特性", view, true);
                _view = view; _palette = palette;
                view.InputFocusChanged += RetainInputFocus;
                palette.StateChanged += PaletteStateChanged;
                AcadApp.SystemVariableChanged += ThemeChanged;
                _themeHooked = true;
                UpdateTheme();
                palette.Visible = true;
                // 首次显示后解除宿主恢复的旧停靠状态，复用分支不再重设位置。
                palette.Dock = DockSides.None;
            }
            catch
            {
                if (ReferenceEquals(_palette, palette)) Shutdown();
                else palette.Dispose();
                throw;
            }
        }
        else { UpdateTheme(); _palette.Visible = true; }
        _palette.Activate(0);
        StartSelection();
        _selection?.RequestRefresh();
    }

    /// <summary>仅文本输入或复制时保留焦点；释放后由 CAD 正常接管，不主动设置选择。</summary>
    private static void RetainInputFocus(bool retain)
    {
        try { if (_palette != null) _palette.KeepFocus = retain && _palette.Visible; }
        catch (System.Exception ex) { System.Diagnostics.Trace.WriteLine("PN快速特性焦点更新：" + ex.Message); }
    }

    /// <summary>关闭面板即解绑选择、对象和空闲事件；再次显示时重新安装。</summary>
    private static void PaletteStateChanged(object sender, PaletteSetStateEventArgs args)
    {
        try
        {
            if (args.NewState == StateEventIndex.Hide) { var selection = _selection; _selection = null; selection?.Dispose(); }
            else if (args.NewState == StateEventIndex.Show) StartSelection();
        }
        catch (System.Exception ex) { System.Diagnostics.Trace.WriteLine("PN快速特性状态更新：" + ex.Message); }
    }

    /// <summary>自动隐藏收起时只检测面板状态，不查询文档，展开后再刷新。</summary>
    private static void StartSelection()
    {
        if (_selection == null && _view != null)
            _selection = new FastSelectionController(() => _palette is { Visible: true, RolledUp: false }, _view);
    }

    /// <summary>CAD 主题变化事件内部捕获异常，不让异常穿过宿主原生回调。</summary>
    private static void ThemeChanged(object? sender, SystemVariableChangedEventArgs args)
    {
        try { if (args.Name.Equals("COLORTHEME", StringComparison.OrdinalIgnoreCase)) UpdateTheme(); }
        catch (System.Exception ex) { System.Diagnostics.Trace.WriteLine("PN快速特性主题更新：" + ex.Message); }
    }

    /// <summary>COLORTHEME 为零表示深色；读取失败时使用深色默认，不影响面板打开。</summary>
    private static void UpdateTheme()
    {
        bool dark = true;
        try { dark = Convert.ToInt32(AcadApp.GetSystemVariable("COLORTHEME")) == 0; }
        catch (System.Exception) { /* 未知主题时仍展示属性界面。 */ }
        _view?.SetTheme(dark);
    }

    /// <summary>插件退出时解绑主题事件并释放原生窗口，不操作或保存任何图纸。</summary>
    internal static void Shutdown()
    {
        var selection = _selection; _selection = null;
        try { selection?.Dispose(); }
        finally { ReleasePalette(); }
    }

    /// <summary>单独释放主题与窗口，即使刷新器清理失败仍执行。</summary>
    private static void ReleasePalette()
    {
        try { if (_themeHooked) AcadApp.SystemVariableChanged -= ThemeChanged; }
        finally
        {
            _themeHooked = false;
            var palette = _palette; var view = _view;
            view?.ClearInputFocus();
            if (view != null) view.InputFocusChanged -= RetainInputFocus;
            _palette = null; _view = null;
            if (palette != null)
            {
                try { palette.StateChanged -= PaletteStateChanged; }
                finally { palette.Dispose(); }
            }
        }
    }
}
