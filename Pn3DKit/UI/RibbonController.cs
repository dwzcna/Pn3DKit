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
    private readonly List<RibbonButton> _buttons = [];

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
            UpdateAvailability();
            var ribbon = ComponentManager.Ribbon;
            if (ribbon == null || (!_pending && ReferenceEquals(ribbon, _control))) return;
            _pending = false;
            _control = ribbon;
            if (ribbon.Tabs.Any(t => t.Id == TabId)) return;
            _buttons.Clear();
            _ownedTab = new RibbonTab { Id = TabId, Title = "Pn3D工具箱" };
            _ownedTab.Panels.Add(QuickActions());
            _ownedTab.Panels.Add(TagChecks());
            _ownedTab.Panels.Add(DrawingOperations());
            ribbon.Tabs.Add(_ownedTab);
            UpdateAvailability();
        }
        catch (System.Exception ex) { BatchExportCommands.Write("Ribbon 初始化失败：" + ex.Message); }
    }

    /// <summary>常用属性、格式和求值入口；特性大按钮在左，两个小按钮在右侧上下排列。</summary>
    private RibbonPanel QuickActions() => Panel("Pn3DKit.Panel.QuickActions", "快速操作",
        LargeButton("Pn3DKit.FastPanel", "PNFASTPANEL", "PN快速特性",
            "显示自定义 Plant 属性；支持统一编辑所选对象的共有可写属性，不自动保存图纸。",
            RibbonImages.FastPanelSmall, RibbonImages.FastPanelLarge),
        Rows(
            [SmallButton("EVALUATEPNTAG", "评估元件位号", "输入格式，计算所选元件对应的位号；SDK 求值可能更新序列字段。")],
            [SmallButton("GETPNTAGFORMAT", "获取位号格式", "查看所选部件实际采用的位号格式。")]));

    /// <summary>按单个、本图、项目分三行；本图及项目检查在左、重建在右，不创建空白按钮。</summary>
    private RibbonPanel TagChecks() => Panel("Pn3DKit.Panel.TagChecks", "位号检查",
        Rows(
            [SmallButton("CHECKPNTAG", "检查元件位号", "检查所选元件的现有位号，显示当前属性差异；SDK 求值可能更新序列字段。")],
            [SmallButton("CHECKPNTAGBYCLASS", "检查本图位号", "按类别检查当前图纸模型空间的位号，生成日志和 CSV；SDK 求值可能更新序列字段。"),
             SmallButton("REBUILDPNTAGBYCLASS", "重建本图位号", "预检后确认，重建当前图纸 PnId 位号，默认不保存；SDK 求值可能更新序列字段。")],
            [SmallButton("CHECKPROJECTPNTAGBYCLASS", "检查项目位号", "检查整个项目实际使用的元件和管线组，包含未打开图纸；SDK 求值可能更新序列字段。"),
             SmallButton("REBUILDPROJECTPNTAGBYCLASS", "重建项目位号", "预检整个项目，选择逐图重建或仅提交项目数据；默认 N 提交项目数据；SDK 求值可能更新序列字段。")]));

    /// <summary>导出独立成区，保留既有大图标、按钮 ID 和命令。</summary>
    private RibbonPanel DrawingOperations() => Panel("Pn3DKit.Panel.DrawingOperations", "图纸操作",
        LargeButton("Pn3DKit.BatchExport", "PNDWGEXPORT", "批量导出CAD",
            "将当前 Plant 项目的 PnId 或 Piping 图纸导出为普通 DWG。", RibbonImages.Small, RibbonImages.Large));

    /// <summary>固定面板身份及标题，使用原生面板边界分区，不再添加手工分隔线。</summary>
    private static RibbonPanel Panel(string id, string title, params RibbonItem[] items)
    {
        var source = new RibbonPanelSource { Id = id, Title = title };
        foreach (var item in items) source.Items.Add(item);
        return new RibbonPanel { Source = source };
    }

    /// <summary>用原生换行组织最多三行；同一行按钮横向排列，宽度一致使两列对齐。</summary>
    private static RibbonRowPanel Rows(params RibbonButton[][] rows)
    {
        var panel = new RibbonRowPanel { IsTopJustified = true };
        foreach (var row in rows)
        {
            if (panel.Items.Count != 0) panel.Items.Add(new RibbonRowBreak());
            foreach (var button in row) panel.Items.Add(button);
        }
        return panel;
    }

    /// <summary>两个大按钮共用创建及注册流程，保留独立 16／32 DIP 矢量图标。</summary>
    private RibbonButton LargeButton(string id, string command, string text, string purpose,
        System.Windows.Media.ImageSource image, System.Windows.Media.ImageSource largeImage) => Register(new RibbonButton
    {
        Id = id, Text = text, ShowText = true, ShowImage = true, Image = image, LargeImage = largeImage,
        Orientation = System.Windows.Controls.Orientation.Vertical, Size = RibbonItemSize.Large,
        ToolTip = new RibbonToolTip { Title = text, Command = command, Content = purpose },
        CommandHandler = new QueuedCommand(command)
    });

    /// <summary>所有分区的按钮统一登记，使空闲时的启用状态更新覆盖全部九个入口。</summary>
    private RibbonButton Register(RibbonButton button)
    {
        _buttons.Add(button);
        return button;
    }

    /// <summary>16 DIP 矢量图标配右侧中文文字，Tooltip 保留完整命令和用途。</summary>
    private RibbonButton SmallButton(string command, string text, string purpose) => Register(new RibbonButton
    {
        Id = "Pn3DKit." + command, Text = text, Size = RibbonItemSize.Standard, Width = 128, MinWidth = 128,
        ShowText = true, ShowImage = true, Image = RibbonImages.ForTagCommand(command),
        Orientation = System.Windows.Controls.Orientation.Horizontal,
        ToolTip = new RibbonToolTip { Title = text, Command = command, Content = purpose },
        CommandHandler = new QueuedCommand(command)
    });

    /// <summary>空闲通知同步批次和命令忙碌状态，不依赖鼠标触发 WPF 重查。</summary>
    private void UpdateAvailability()
    {
        bool enabled = false;
        try { enabled = new QueuedCommand("PNDWGEXPORT").CanExecute(null); }
        catch (System.Exception) { /* 文档关闭期间保持入口禁用，等待下一次空闲通知。 */ }
        foreach (var button in _buttons)
            if (button.IsEnabled != enabled) button.IsEnabled = enabled;
    }

    /// <summary>通过 CAD 命令队列执行，不发送取消前缀，保留 PICKFIRST。</summary>
    private sealed class QueuedCommand(string command) : ICommand
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
                AcadApp.DocumentManager.MdiActiveDocument.SendStringToExecute(command + " ", true, false, false);
        }
    }

    /// <summary>解除所有宿主事件，并仅移除本实例创建的选项卡。</summary>
    public void Dispose()
    {
        AcadApp.Idle -= OnIdle;
        RibbonServices.RibbonPaletteSetCreated -= PaletteCreated;
        if (_palette != null) _palette.WorkspaceLoaded -= WorkspaceLoaded;
        if (_ownedTab != null) _control?.Tabs.Remove(_ownedTab);
        _buttons.Clear();
    }
}
