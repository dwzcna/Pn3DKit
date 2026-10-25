using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Pn3DKit.Models;
using Pn3DKit.Utils;
using WpfTextBox = System.Windows.Controls.TextBox;
using WpfButton = System.Windows.Controls.Button;

namespace Pn3DKit.UI;

/// <summary>可配置的两列 Plant 属性表；路径始终可输入，值按宿主票据开放单对象编辑。</summary>
internal sealed class FastPropertiesView : System.Windows.Controls.UserControl
{
    private sealed class PropertyRow
    {
        internal readonly Guid Id = Guid.NewGuid();
        internal long Revision;
        internal required WpfTextBox Input;
        internal required WpfTextBox Value;
        internal required Border Container;
        internal string Committed = "";
        internal bool PendingPath, Editing;
        internal PnPropertyCell? Cell;
    }
    private readonly List<PropertyRow> _rows = [];
    private readonly StackPanel _table = new();
    private readonly TextBlock _objectType;
    private readonly TextBlock _footer;
    private PropertyRow? _active;
    private bool _updating, _focusSuspended, _retainingFocus;
    private WpfTextBox? _focusedInput;
    internal bool HasInputFocus => _focusedInput != null;
    internal event Action<bool>? InputFocusChanged;
    internal event Action? RowsChanging;
    internal event Action? RowsCommitted;
    internal event Action<PnEditRequest>? EditRequested;

    /// <summary>默认一空行；无对象、缺少项目或忙碌时仍允许修改属性列及增删行。</summary>
    internal FastPropertiesView()
    {
        FontFamily = new System.Windows.Media.FontFamily("Segoe UI, Microsoft YaHei UI");
        FontSize = 12; UseLayoutRounding = true; SnapsToDevicePixels = true;
        SetResourceReference(BackgroundProperty, "PanelBackground");
        SetResourceReference(ForegroundProperty, "PanelForeground");
        var root = new Grid { Margin = new Thickness(10) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        KeyboardNavigation.SetTabNavigation(_table, KeyboardNavigationMode.Cycle);
        _objectType = Text("未选择对象", new Thickness(9, 8, 9, 8));
        _objectType.TextTrimming = TextTrimming.CharacterEllipsis;
        var summary = Surface(_objectType); summary.Margin = new Thickness(0, 2, 0, 12);
        root.Children.Add(summary);

        var header = Columns();
        var propertyName = Text("Pn属性", new Thickness(9, 7, 6, 7)); propertyName.FontWeight = FontWeights.SemiBold;
        var valueName = Text("值", new Thickness(9, 7, 6, 7)); valueName.FontWeight = FontWeights.SemiBold;
        header.Children.Add(propertyName); Grid.SetColumn(valueName, 1); header.Children.Add(valueName);
        var heading = Surface(header); heading.SetResourceReference(BackgroundProperty, "PanelHeader");
        Grid.SetRow(heading, 1); root.Children.Add(heading);
        var scroll = new ScrollViewer { Content = _table, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        Grid.SetRow(scroll, 2); root.Children.Add(scroll);

        var bottom = new StackPanel { Margin = new Thickness(0, 10, 0, 0) };
        var tools = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal };
        var add = Button("添加属性"); var remove = Button("删除属性");
        add.Click += (_, _) => AddRow("");
        remove.Click += (_, _) => RemoveActive();
        tools.Children.Add(add); tools.Children.Add(remove); bottom.Children.Add(tools);
        _footer = Text("只读 · 未选择对象", new Thickness(0, 8, 0, 2));
        _footer.FontSize = 11; _footer.TextWrapping = TextWrapping.Wrap; _footer.MaxHeight = 64;
        _footer.SetResourceReference(TextBlock.ForegroundProperty, "PanelMuted");
        bottom.Children.Add(_footer); Grid.SetRow(bottom, 3); root.Children.Add(bottom);
        Content = root;
        AddHandler(Keyboard.PreviewGotKeyboardFocusEvent, new KeyboardFocusChangedEventHandler(InputFocusChanging), true);
        AddHandler(Keyboard.GotKeyboardFocusEvent, new KeyboardFocusChangedEventHandler(InputFocusChanging), true);
        AddHandler(Keyboard.LostKeyboardFocusEvent, new KeyboardFocusChangedEventHandler(InputFocusChanging), true);
        Unloaded += (_, _) => ClearInputFocus();
        AddRow("");
        _active = _rows[0]; SetTheme(true);
    }

    /// <summary>跟踪本表输入框的目标焦点，属性和值均能获得键盘输入或复制。</summary>
    private void InputFocusChanging(object sender, KeyboardFocusChangedEventArgs args) => TrackInputFocus(args.NewFocus);

    private void TrackInputFocus(IInputElement? target)
    {
        _focusedInput = target is WpfTextBox box && _rows.Any(r => ReferenceEquals(r.Input, box) || ReferenceEquals(r.Value, box))
            ? box : null;
        PublishInputFocus();
    }

    /// <summary>进入 CAD 命令上下文前允许宿主取回焦点，不改变选择集。</summary>
    internal void SuspendInputFocus() { _focusSuspended = true; PublishInputFocus(); }
    /// <summary>提交或拒绝结束后恢复正常输入；不主动激活或移动系统焦点。</summary>
    internal void ResumeInputFocus() { _focusSuspended = false; PublishInputFocus(); }
    /// <summary>隐藏、关闭或卸载时释放焦点保留，防止原生面板一直占有键盘。</summary>
    internal void ClearInputFocus() { _focusedInput = null; _focusSuspended = false; PublishInputFocus(); }

    private void PublishInputFocus()
    {
        bool retain = HasInputFocus && !_focusSuspended;
        if (retain == _retainingFocus) return;
        _retainingFocus = retain; InputFocusChanged?.Invoke(retain);
    }

    /// <summary>输入中的行不交给 SDK；提交时才采用新路径。</summary>
    internal IReadOnlyList<PnPropertySpec> ReadSpecs() => _rows.Select(r => new PnPropertySpec(r.Id, r.Revision,
        r.PendingPath ? "" : r.Committed)).ToArray();

    /// <summary>仅更新原有单元格；路径修订不同的结果直接丢弃。</summary>
    internal void Show(PnPanelSnapshot snapshot)
    {
        _objectType.Text = snapshot.Header; _objectType.ToolTip = Tip(snapshot.Header);
        ShowStatus(snapshot.Status);
        _updating = true;
        try
        {
            foreach (var cell in snapshot.Cells)
            {
                var row = _rows.FirstOrDefault(r => r.Id == cell.Spec.Id);
                if (row == null || row.Revision != cell.Spec.Revision || row.PendingPath) continue;
                row.Cell = cell; row.Editing = false;
                row.Value.IsReadOnly = !cell.Permission.Allowed || cell.EditToken == Guid.Empty;
                row.Value.Text = !row.Value.IsReadOnly && ReferenceEquals(_focusedInput, row.Value) ? cell.Value.Raw : cell.Value.Display;
                row.Value.ToolTip = Tip(cell.Value.Display + (string.IsNullOrWhiteSpace(cell.Value.Detail) ? "" : "\n" + cell.Value.Detail)
                    + "\n" + cell.Permission.Reason);
            }
        }
        finally { _updating = false; }
    }

    /// <summary>状态变化立即撤销编辑许可，保留路径输入；读取权限重新确认后再开放。</summary>
    internal void SuspendEditing()
    {
        _updating = true;
        try
        {
            foreach (var row in _rows)
            {
                row.Value.IsReadOnly = true;
                if (row.Editing) row.Value.Text = row.Cell?.Value.Display ?? "—";
                row.Cell = null; row.Editing = false;
            }
        }
        finally { _updating = false; }
    }

    internal void ShowStatus(string message) { _footer.Text = message; _footer.ToolTip = Tip(message); }

    /// <summary>连续行不含分组，属性名和只读值均能选择复制。</summary>
    private void AddRow(string name)
    {
        SuspendEditing();
        var input = CellBox(); var value = CellBox(); value.IsReadOnly = true; value.Text = "—";
        var grid = Columns(); grid.Children.Add(input); Grid.SetColumn(value, 1); grid.Children.Add(value);
        var split = new Border { BorderThickness = new Thickness(1, 0, 0, 0), IsHitTestVisible = false };
        split.SetResourceReference(Border.BorderBrushProperty, "PanelBorder"); Grid.SetColumn(split, 1); grid.Children.Add(split);
        var container = Surface(grid); container.BorderThickness = new Thickness(1, 0, 1, 1);
        var row = new PropertyRow { Input = input, Value = value, Container = container, Committed = name };
        input.Text = name;
        input.TextChanged += (_, _) =>
        {
            if (_updating) return;
            row.Revision++; row.PendingPath = true; SuspendEditing();
            row.Value.Text = "—"; RowsChanging?.Invoke();
        };
        input.GotKeyboardFocus += (_, _) => _active = row;
        input.LostKeyboardFocus += (_, e) => { TrackInputFocus(e.NewFocus); CommitPath(row); };
        input.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { e.Handled = true; CommitPath(row); }
            else if (e.Key == Key.Escape)
            {
                _updating = true; input.Text = row.Committed; _updating = false;
                row.Revision++; row.PendingPath = false; RowsChanging?.Invoke(); RowsCommitted?.Invoke(); e.Handled = true;
            }
        };
        value.GotKeyboardFocus += (_, _) =>
        {
            _active = row;
            if (value.IsReadOnly || row.Cell == null) return;
            row.Editing = true; value.Text = row.Cell.Value.Raw;
        };
        value.TextChanged += (_, _) =>
        {
            if (!_updating && ReferenceEquals(_focusedInput, value) && !value.IsReadOnly && row.Cell != null) row.Editing = true;
        };
        value.LostKeyboardFocus += (_, e) =>
        {
            TrackInputFocus(e.NewFocus); CommitValue(row);
            // 未提交的空值在离开后恢复占位；不将占位文本作为输入再次提交。
            if (row.Cell != null)
            {
                _updating = true;
                try { value.Text = row.Cell.Value.Display; }
                finally { _updating = false; }
            }
        };
        value.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { e.Handled = true; CommitValue(row); }
            else if (e.Key == Key.Escape)
            {
                e.Handled = true;
                if (row.Cell != null) value.Text = row.Cell.Value.Raw;
            }
        };
        _rows.Add(row); _table.Children.Add(container); _active = row;
        RowsChanging?.Invoke(); RowsCommitted?.Invoke();
        if (string.IsNullOrEmpty(name)) input.Focus();
    }

    /// <summary>删除选中输入行不会写入对象，移除前先废弃该行票据。</summary>
    private void RemoveActive()
    {
        var row = _active;
        if (row == null) return;
        ClearInputFocus(); SuspendEditing(); RowsChanging?.Invoke();
        _rows.Remove(row); _table.Children.Remove(row.Container);
        _active = _rows.LastOrDefault(); RowsCommitted?.Invoke();
    }

    /// <summary>回车或离开路径单元格才触发读取，编辑过程中不查询新路径。</summary>
    private void CommitPath(PropertyRow row)
    {
        if (!row.PendingPath) return;
        row.Committed = row.Input.Text; row.PendingPath = false;
        string? path = PnPropertyPolicy.Normalize(row.Committed, out string reason);
        row.Input.ToolTip = Tip(path ?? reason); RowsCommitted?.Invoke();
    }

    /// <summary>提交当前票据及原值；视图不操作 SDK，拒绝过期由宿主再次验证。</summary>
    private void CommitValue(PropertyRow row)
    {
        if (_updating || !row.Editing || row.Value.IsReadOnly || row.Cell == null) return;
        var cell = row.Cell; row.Editing = false;
        string value = row.Value.Text;
        if (value == cell.Value.Raw)
        {
            _updating = true;
            try { row.Value.Text = ReferenceEquals(_focusedInput, row.Value) ? cell.Value.Raw : cell.Value.Display; }
            finally { _updating = false; }
            return;
        }
        string? path = PnPropertyPolicy.Normalize(cell.Spec.Input, out _);
        if (path == null || cell.EditToken == Guid.Empty) return;
        var request = new PnEditRequest(cell.EditToken, row.Id, row.Revision, path, cell.Value.Raw, value);
        SuspendEditing(); ShowStatus("正在提交；不自动保存图纸…");
        EditRequested?.Invoke(request);
    }

    /// <summary>面板自身使用蓝灰／灰白层次，原生边框停靠仍由 CAD 管理。</summary>
    internal void SetTheme(bool dark)
    {
        string[] keys = ["PanelBackground", "PanelSurface", "PanelForeground", "PanelMuted", "PanelBorder", "PanelHeader", "PanelSelection"];
        string[] colors = dark ? ["#384252", "#3E4959", "#EDF1F6", "#C1CAD6", "#505D70", "#2D3644", "#6686AB"]
            : ["#EDF0F4", "#F9FAFC", "#263244", "#59697D", "#CBD3DD", "#DEE4EC", "#A8C8E8"];
        for (int i = 0; i < keys.Length; i++)
        {
            var brush = new SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(colors[i]));
            brush.Freeze(); Resources[keys[i]] = brush;
        }
    }

    private static Grid Columns()
    {
        var grid = new Grid { MinHeight = 32 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        return grid;
    }
    private static WpfTextBox CellBox()
    {
        var box = new WpfTextBox { Padding = new Thickness(8, 6, 6, 6), BorderThickness = new Thickness(0),
            VerticalContentAlignment = VerticalAlignment.Center, IsReadOnlyCaretVisible = true,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden, MinWidth = 0 };
        box.SetResourceReference(BackgroundProperty, "PanelSurface"); box.SetResourceReference(ForegroundProperty, "PanelForeground");
        box.SetResourceReference(WpfTextBox.CaretBrushProperty, "PanelForeground"); box.SetResourceReference(WpfTextBox.SelectionBrushProperty, "PanelSelection");
        ToolTipService.SetShowDuration(box, 60000); return box;
    }
    private static WpfButton Button(string title)
    {
        var button = new WpfButton { Content = title, Padding = new Thickness(9, 4, 9, 4), Margin = new Thickness(0, 0, 8, 0) };
        button.SetResourceReference(BackgroundProperty, "PanelHeader"); button.SetResourceReference(ForegroundProperty, "PanelForeground");
        button.SetResourceReference(BorderBrushProperty, "PanelBorder"); return button;
    }
    private static TextBlock Text(string text, Thickness margin)
    {
        var result = new TextBlock { Text = text, Margin = margin, VerticalAlignment = VerticalAlignment.Center };
        result.SetResourceReference(TextBlock.ForegroundProperty, "PanelForeground"); return result;
    }
    private static Border Surface(UIElement child)
    {
        var result = new Border { Child = child, BorderThickness = new Thickness(1) };
        result.SetResourceReference(BackgroundProperty, "PanelSurface"); result.SetResourceReference(BorderBrushProperty, "PanelBorder"); return result;
    }
    private static System.Windows.Controls.ToolTip Tip(string text) => new()
    { Content = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, MaxWidth = 640 }, Padding = new Thickness(8) };
}
