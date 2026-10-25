using System.Windows.Input;
using Autodesk.AutoCAD.Ribbon;
using Autodesk.Windows;
using Pn3DKit.Commands;
using AcadApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;

namespace Pn3DKit.UI;

/// <summary>延迟创建 Pn3DKit Ribbon，监听工作空间重载并防止重复选项卡。</summary>
internal sealed class RibbonController : IDisposable
{
    private const string TabId = "Pn3DKit.Ribbon";
    private RibbonPaletteSet? _palette;
    private RibbonControl? _control;
    private RibbonTab? _ownedTab;
    private bool _pending = true;

    /// <summary>安装事件并在下次空闲时创建 Ribbon，不强制打开用户关闭的 Ribbon。</summary>
    internal void Start()
    {
        RibbonServices.RibbonPaletteSetCreated += PaletteCreated;
        AcadApp.Idle += OnIdle;
        AttachPalette();
    }

    /// <summary>绑定当前 Ribbon 容器，并从旧容器解除事件。</summary>
    private void AttachPalette()
    {
        var palette = RibbonServices.RibbonPaletteSet;
        if (ReferenceEquals(palette, _palette)) return;
        if (_palette != null) _palette.WorkspaceLoaded -= WorkspaceLoaded;
        _palette = palette;
        if (_palette != null) _palette.WorkspaceLoaded += WorkspaceLoaded;
        _pending = true;
    }

    /// <summary>容器创建后安排一次空闲初始化。</summary>
    private void PaletteCreated(object? sender, EventArgs args) { AttachPalette(); _pending = true; }
    /// <summary>工作空间切换完成后重新检查选项卡。</summary>
    private void WorkspaceLoaded(object? sender, EventArgs args) => _pending = true;

    /// <summary>仅在需要初始化或 Ribbon 实例变化时更新，避免持续重建 UI。</summary>
    private void OnIdle(object? sender, EventArgs args)
    {
        try
        {
            var ribbon = ComponentManager.Ribbon;
            if (ribbon == null || (!_pending && ReferenceEquals(ribbon, _control))) return;
            _pending = false;
            _control = ribbon;
            if (ribbon.Tabs.Any(t => t.Id == TabId)) return;
            var source = new RibbonPanelSource { Title = "项目导出" };
            source.Items.Add(new RibbonButton
            {
                Id = "Pn3DKit.BatchExport", Text = "批量导出CAD", ShowText = true, ShowImage = true,
                Image = RibbonImages.Small, LargeImage = RibbonImages.Large,
                Orientation = System.Windows.Controls.Orientation.Vertical,
                ToolTip = "将当前 Plant 项目的 PnId 或 Piping 图纸导出为普通 DWG。",
                Size = RibbonItemSize.Large, CommandHandler = new ExportCommand()
            });
            _ownedTab = new RibbonTab { Id = TabId, Title = "Pn3DKit" };
            _ownedTab.Panels.Add(new RibbonPanel { Source = source });
            ribbon.Tabs.Add(_ownedTab);
        }
        catch (System.Exception ex) { BatchExportCommands.Write("Ribbon 初始化失败：" + ex.Message); }
    }

    /// <summary>将 Ribbon 点击转换为标准 CAD 命令；实际批次由命令入口互斥。</summary>
    private sealed class ExportCommand : ICommand
    {
        /// <summary>CAD 文档和批次状态变化时由 WPF 重新检查可用性。</summary>
        public event EventHandler? CanExecuteChanged
        {
            add => CommandManager.RequerySuggested += value;
            remove => CommandManager.RequerySuggested -= value;
        }
        /// <summary>仅在有活动图纸且没有运行中批次时允许执行。</summary>
        public bool CanExecute(object? parameter) =>
            !BatchExportCommands.IsRunning && AcadApp.DocumentManager.MdiActiveDocument is { } document
            && document.Editor.IsQuiescent;
        /// <summary>通过 CAD 命令队列执行入口，不在 Ribbon 回调中直接导出。</summary>
        public void Execute(object? parameter)
        {
            if (CanExecute(parameter))
                AcadApp.DocumentManager.MdiActiveDocument.SendStringToExecute("PNDWGEXPORT ", true, false, false);
        }
    }

    /// <summary>解除所有宿主事件，并仅移除本实例创建的选项卡。</summary>
    public void Dispose()
    {
        AcadApp.Idle -= OnIdle;
        RibbonServices.RibbonPaletteSetCreated -= PaletteCreated;
        if (_palette != null) _palette.WorkspaceLoaded -= WorkspaceLoaded;
        if (_ownedTab != null) _control?.Tabs.Remove(_ownedTab);
    }
}
