using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using Pn3DKit.Models;
using Pn3DKit.Utils;
using WpfButton = System.Windows.Controls.Button;
using Binding = System.Windows.Data.Binding;

namespace Pn3DKit.UI;

/// <summary>仅用于弹窗的勾选状态；已有属性不允许取消，文本及元数据不参与编辑。</summary>
internal sealed class PropertyCatalogChoice : INotifyPropertyChanged
{
    private bool _isChecked;
    internal PropertyCatalogChoice(PnPropertyCatalogEntry entry, IReadOnlyList<string> existing)
    {
        PropertyPath = entry.PropertyPath; FriendlyDisplayName = entry.FriendlyDisplayName;
        IsExisting = PnPropertyCatalogPolicy.MissingPaths(existing, [PropertyPath]).Count == 0
            && PnPropertyPolicy.Normalize(PropertyPath, out _) != null;
        bool supported = PnPropertyPolicy.Normalize(PropertyPath, out string reason) != null;
        CanSelect = !IsExisting && supported; _isChecked = IsExisting;
        Detail = PropertyPath + " — " + FriendlyDisplayName
            + (IsExisting ? "\n已在面板；此窗口只追加，不移除已有行。" : "")
            + (!supported ? "\n该路径暂不能添加：" + reason : "")
            + (entry.Detail.Length == 0 ? "" : "\n" + entry.Detail);
    }
    public string PropertyPath { get; }
    public string FriendlyDisplayName { get; }
    public string Detail { get; }
    public bool IsExisting { get; }
    public bool CanSelect { get; }
    public bool IsChecked
    {
        get => _isChecked;
        set
        {
            if (!CanSelect || _isChecked == value) return;
            _isChecked = value; PropertyChanged?.Invoke(this, new(nameof(IsChecked)));
        }
    }
    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>纯 WPF 属性目录，打开与勾选不改面板；确认时只发布需要追加的内部属性名。</summary>
internal sealed class PropertyCatalogView : System.Windows.Controls.UserControl
{
    internal IReadOnlyList<PropertyCatalogChoice> Choices { get; }
    internal event Action<IReadOnlyList<string>>? AddRequested;
    internal event Action? CancelRequested;

    /// <summary>接收文本快照和已有路径，可独立离屏测试，不访问 CAD、选择集或 SDK。</summary>
    internal PropertyCatalogView(IReadOnlyList<PnPropertyCatalogEntry> entries, IReadOnlyList<string> existing,
        ResourceDictionary theme)
    {
        Resources.MergedDictionaries.Add(theme);
        FontFamily = new System.Windows.Media.FontFamily("Segoe UI, Microsoft YaHei UI"); FontSize = 12;
        UseLayoutRounding = true; SnapsToDevicePixels = true;
        SetResourceReference(BackgroundProperty, "PanelBackground");
        SetResourceReference(ForegroundProperty, "PanelForeground");
        Choices = entries.Select(x => new PropertyCatalogChoice(x, existing)).ToArray();
        var root = new Grid { Margin = new Thickness(12) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var hint = new TextBlock
        {
            Text = $"共 {Choices.Count} 项属性，{Choices.Count(x => x.IsExisting)} 项已在面板。勾选后追加，不更改已有行。",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10)
        };
        root.Children.Add(hint);
        var grid = new System.Windows.Controls.DataGrid
        {
            ItemsSource = Choices, AutoGenerateColumns = false, CanUserAddRows = false, CanUserDeleteRows = false,
            CanUserResizeRows = false, HeadersVisibility = DataGridHeadersVisibility.Column, RowHeaderWidth = 0,
            SelectionMode = DataGridSelectionMode.Extended, SelectionUnit = DataGridSelectionUnit.Cell,
            ClipboardCopyMode = DataGridClipboardCopyMode.ExcludeHeader,
            GridLinesVisibility = DataGridGridLinesVisibility.Horizontal, MinRowHeight = 24,
            BorderThickness = new Thickness(1), RowHeight = double.NaN, EnableRowVirtualization = true
        };
        grid.SetResourceReference(BackgroundProperty, "PanelSurface");
        grid.SetResourceReference(ForegroundProperty, "PanelForeground");
        grid.SetResourceReference(BorderBrushProperty, "PanelBorder");
        grid.SetResourceReference(System.Windows.Controls.DataGrid.HorizontalGridLinesBrushProperty, "PanelBorder");
        var rowStyle = new Style(typeof(DataGridRow));
        rowStyle.Setters.Add(new Setter(BackgroundProperty, new DynamicResourceExtension("PanelSurface")));
        rowStyle.Setters.Add(new Setter(ForegroundProperty, new DynamicResourceExtension("PanelForeground")));
        grid.RowStyle = rowStyle;
        var header = new Style(typeof(System.Windows.Controls.Primitives.DataGridColumnHeader));
        header.Setters.Add(new Setter(BackgroundProperty, new DynamicResourceExtension("PanelHeader")));
        header.Setters.Add(new Setter(ForegroundProperty, new DynamicResourceExtension("PanelForeground")));
        header.Setters.Add(new Setter(PaddingProperty, new Thickness(6, 5, 6, 5)));
        header.Setters.Add(new Setter(FontWeightProperty, FontWeights.SemiBold));
        grid.ColumnHeaderStyle = header;
        var cellStyle = new Style(typeof(DataGridCell));
        cellStyle.Setters.Add(new Setter(BorderThicknessProperty, new Thickness(0)));
        cellStyle.Setters.Add(new Setter(ToolTipProperty, new Binding(nameof(PropertyCatalogChoice.Detail))));
        grid.CellStyle = cellStyle;

        var check = new FrameworkElementFactory(typeof(System.Windows.Controls.CheckBox));
        check.SetValue(HorizontalAlignmentProperty, System.Windows.HorizontalAlignment.Center);
        check.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
        check.SetValue(ToolTipService.ShowOnDisabledProperty, true);
        check.SetBinding(System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty,
            new Binding(nameof(PropertyCatalogChoice.IsChecked)) { Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged });
        check.SetBinding(IsEnabledProperty, new Binding(nameof(PropertyCatalogChoice.CanSelect)));
        check.SetBinding(ToolTipProperty, new Binding(nameof(PropertyCatalogChoice.Detail)));
        grid.Columns.Add(new DataGridTemplateColumn
        {
            Header = "", Width = new DataGridLength(32), CanUserSort = false,
            CellTemplate = new DataTemplate { VisualTree = check }
        });
        var textStyle = new Style(typeof(TextBlock));
        textStyle.Setters.Add(new Setter(MarginProperty, new Thickness(6, 3, 6, 3)));
        textStyle.Setters.Add(new Setter(VerticalAlignmentProperty, VerticalAlignment.Center));
        textStyle.Setters.Add(new Setter(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis));
        grid.Columns.Add(new DataGridTextColumn
        {
            Header = "属性路径", Binding = new Binding(nameof(PropertyCatalogChoice.PropertyPath)),
            IsReadOnly = true, Width = new DataGridLength(1, DataGridLengthUnitType.Star), ElementStyle = textStyle
        });
        grid.Columns.Add(new DataGridTextColumn
        {
            Header = "显示名称", Binding = new Binding(nameof(PropertyCatalogChoice.FriendlyDisplayName)),
            IsReadOnly = true, Width = new DataGridLength(1.2, DataGridLengthUnitType.Star), ElementStyle = textStyle
        });
        Grid.SetRow(grid, 1); root.Children.Add(grid);
        var actions = new StackPanel
        {
            Orientation = System.Windows.Controls.Orientation.Horizontal, HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
            Margin = new Thickness(0, 12, 0, 0)
        };
        var accept = ActionButton("添加所选属性"); accept.IsDefault = true; accept.IsEnabled = false;
        var cancel = ActionButton("取消"); cancel.IsCancel = true;
        foreach (var item in Choices)
            item.PropertyChanged += (_, _) => accept.IsEnabled = Choices.Any(x => x.CanSelect && x.IsChecked);
        accept.Click += (_, _) =>
        {
            var selected = Choices.Where(x => x.CanSelect && x.IsChecked).Select(x => x.PropertyPath).ToArray();
            if (selected.Length != 0) AddRequested?.Invoke(selected);
        };
        cancel.Click += (_, _) => CancelRequested?.Invoke();
        actions.Children.Add(accept); actions.Children.Add(cancel);
        Grid.SetRow(actions, 2); root.Children.Add(actions); Content = root;
    }

    private WpfButton ActionButton(string title) => new()
    {
        Content = title, Padding = new Thickness(10, 5, 10, 5), Margin = new Thickness(6, 0, 0, 0),
        Style = (Style)FindResource(typeof(WpfButton))
    };
}

/// <summary>由 CAD ShowModalWindow 设置原生所有者；关闭或取消不会发布追加请求。</summary>
internal sealed class PropertyCatalogWindow : System.Windows.Window
{
    internal IReadOnlyList<string> SelectedProperties { get; private set; } = [];

    internal PropertyCatalogWindow(IReadOnlyList<PnPropertyCatalogEntry> entries, IReadOnlyList<string> existing, ResourceDictionary theme)
    {
        Title = "检索 Plant 属性"; Width = 680; Height = 480; MinWidth = 480; MinHeight = 320;
        ShowInTaskbar = false; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var view = new PropertyCatalogView(entries, existing, theme);
        view.AddRequested += selected => { SelectedProperties = selected; DialogResult = true; };
        view.CancelRequested += () => DialogResult = false;
        Content = view;
    }
}
