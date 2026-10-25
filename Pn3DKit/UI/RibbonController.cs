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
            var source = new RibbonPanelSource { Title = "位号与导出" };
            source.Items.Add(Column(
                SmallButton("CHECKPNTAG", "检查元件位号", "检查所选元件的现有位号，显示当前属性差异；SDK 求值可能更新序列字段。"),
                SmallButton("CHECKPNTAGBYCLASS", "检查元件位号(本页)", "按类别检查当前图纸模型空间的位号，生成日志和 CSV；SDK 求值可能更新序列字段。"),
                SmallButton("CHECKPROJECTPNTAGBYCLASS", "检查项目元件位号", "检查整个项目实际使用的元件和管线组，包含未打开图纸；SDK 求值可能更新序列字段。")));
            source.Items.Add(Column(
                SmallButton("EVALUATEPNTAG", "评估元件位号", "输入格式，计算所选元件对应的位号；SDK 求值可能更新序列字段。"),
                SmallButton("REBUILDPNTAGBYCLASS", "重建元件位号", "预检后确认，重建当前图纸 PnId 位号，默认不保存；SDK 求值可能更新序列字段。"),
                SmallButton("REBUILDPROJECTPNTAGBYCLASS", "重建项目元件位号", "预检整个项目，选择逐图重建或仅提交项目数据；默认 N 提交项目数据；SDK 求值可能更新序列字段。")));
            source.Items.Add(Column(
                SmallButton("GETPNTAGFORMAT", "获取位号格式", "查看所选部件实际采用的位号格式。")));
            source.Items.Add(new RibbonSeparator { SeparatorStyle = RibbonSeparatorStyle.Line });
            var export = new RibbonButton
            {
                Id = "Pn3DKit.BatchExport", Text = "批量导出CAD", ShowText = true, ShowImage = true,
                Image = RibbonImages.Small, LargeImage = RibbonImages.Large,
                Orientation = System.Windows.Controls.Orientation.Vertical,
                ToolTip = new RibbonToolTip { Title = "批量导出CAD", Command = "PNDWGEXPORT",
                    Content = "将当前 Plant 项目的 PnId 或 Piping 图纸导出为普通 DWG。" },
                Size = RibbonItemSize.Large, CommandHandler = new QueuedCommand("PNDWGEXPORT")
            };
            _buttons.Add(export);
            source.Items.Add(export);
            var fastPanel = new RibbonButton
            {
                Id = "Pn3DKit.FastPanel", Text = "PN快速特性", ShowText = true, ShowImage = true,
                Image = RibbonImages.FastPanelSmall, LargeImage = RibbonImages.FastPanelLarge,
                Orientation = System.Windows.Controls.Orientation.Vertical, Size = RibbonItemSize.Large,
                ToolTip = new RibbonToolTip { Title = "PN快速特性", Command = "PNFASTPANEL",
                    Content = "显示自定义 Plant 属性；单选按 SDK 权限编辑，多选查看共同值，不自动保存图纸。" },
                CommandHandler = new QueuedCommand("PNFASTPANEL")
            };
            _buttons.Add(fastPanel);
            source.Items.Add(fastPanel);
            _ownedTab = new RibbonTab { Id = TabId, Title = "Pn3D工具箱" };
            _ownedTab.Panels.Add(new RibbonPanel { Source = source });
            ribbon.Tabs.Add(_ownedTab);
            UpdateAvailability();
        }
        catch (System.Exception ex) { BatchExportCommands.Write("Ribbon 初始化失败：" + ex.Message); }
    }

    /// <summary>原生行面板形成一列，最多三行，空白单元不增加虚假按钮。</summary>
    private static RibbonRowPanel Column(params RibbonButton[] buttons)
    {
        var column = new RibbonRowPanel();
        foreach (var button in buttons)
        {
            if (column.Items.Count != 0) column.Items.Add(new RibbonRowBreak());
            column.Items.Add(button);
        }
        return column;
    }

    /// <summary>16 DIP 矢量图标配右侧中文文字，Tooltip 保留完整命令和用途。</summary>
    private RibbonButton SmallButton(string command, string text, string purpose)
    {
        var button = new RibbonButton
        {
            Id = "Pn3DKit." + command, Text = text, Size = RibbonItemSize.Standard,
            ShowText = true, ShowImage = true, Image = RibbonImages.ForTagCommand(command),
            Orientation = System.Windows.Controls.Orientation.Horizontal,
            ToolTip = new RibbonToolTip { Title = text, Command = command, Content = purpose },
            CommandHandler = new QueuedCommand(command)
        };
        _buttons.Add(button);
        return button;
    }

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
