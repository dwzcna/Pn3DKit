using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Pn3DKit.Models;
using Pn3DKit.Utils;
using WpfTextBox = System.Windows.Controls.TextBox;
using WpfButton = System.Windows.Controls.Button;

namespace Pn3DKit.UI;

/// <summary>可配置的两列 Plant 属性表；路径始终可输入，值按完整选择票据开放共有可写属性。</summary>
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
        internal bool PendingPath, Editing, Dirty, MenuOpen, MenuOpening;
        internal long MenuRevision;
        internal required TextBlock MixedHint;
        internal PnPropertyCell? Cell;
    }
    private readonly List<PropertyRow> _rows = [];
    private readonly StackPanel _table = new();
    private readonly ResourceDictionary _controlStyles = FastPanelControlStyles.CreateResources();
    private readonly RoutedUICommand _removePropertyRow = new("移除此属性行", "RemovePropertyRow", typeof(FastPropertiesView));
    private readonly TextBlock _objectType;
    private readonly TextBlock _footer;
    private readonly WpfButton _catalog;
    private PropertyRow? _active;
    private bool _updating, _focusSuspended, _retainingFocus, _clearingMenus;
    private WpfTextBox? _focusedInput;
    private IInputElement? _observedFocus;
    internal bool HasInputFocus => _focusedInput != null;
    internal event Action<bool>? InputFocusChanged;
    internal event Action? RowsChanging;
    internal event Action? RowsCommitted;
    internal event Action<PnEditRequest>? EditRequested;
    internal event Action? CatalogRequested;

    /// <summary>默认一空行；无对象、缺少项目或忙碌时仍允许修改属性列及增删行。</summary>
    internal FastPropertiesView()
    {
        Resources.MergedDictionaries.Add(_controlStyles);
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
        var propertyName = Text("Pn属性", new Thickness(9, 3, 6, 3)); propertyName.FontWeight = FontWeights.SemiBold;
        var valueName = Text("值", new Thickness(9, 3, 6, 3)); valueName.FontWeight = FontWeights.SemiBold;
        header.Children.Add(propertyName); Grid.SetColumn(valueName, 1); header.Children.Add(valueName);
        var heading = Surface(header); heading.SetResourceReference(BackgroundProperty, "PanelHeader");
        Grid.SetRow(heading, 1); root.Children.Add(heading);
        var scroll = new ScrollViewer { Content = _table, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        Grid.SetRow(scroll, 2); root.Children.Add(scroll);

        var bottom = new StackPanel { Margin = new Thickness(0, 10, 0, 0) };
        var tools = new WrapPanel { Orientation = System.Windows.Controls.Orientation.Horizontal };
        var add = Button("添加属性"); var remove = Button("删除属性");
        add.Click += (_, _) => FocusTrailingRow();
        // 在鼠标引发失焦前停用草稿提交；实际点击移除时才撤销票据及行修订。
        remove.PreviewMouseLeftButtonDown += (_, _) => { if (_active != null) _active.Editing = false; };
        remove.Click += (_, _) => { if (_active != null) RemoveRow(_active); };
        _catalog = Button("检索属性");
        _catalog.Click += (_, _) => { if (_catalog.IsEnabled) CatalogRequested?.Invoke(); };
        ToolTipService.SetShowOnDisabled(_catalog, true);
        SetCatalogAvailability(false, "请选择一个有效的 Plant 对象，等待读取完成。");
        tools.Children.Add(add); tools.Children.Add(remove); tools.Children.Add(_catalog); bottom.Children.Add(tools);
        _footer = Text("只读 · 未选择对象", new Thickness(0, 8, 0, 2));
        _footer.FontSize = 11; _footer.TextWrapping = TextWrapping.Wrap; _footer.MaxHeight = 64;
        _footer.SetResourceReference(TextBlock.ForegroundProperty, "PanelMuted");
        bottom.Children.Add(_footer); Grid.SetRow(bottom, 3); root.Children.Add(bottom);
        Content = root;
        AddHandler(Keyboard.PreviewGotKeyboardFocusEvent, new KeyboardFocusChangedEventHandler(InputFocusChanging), true);
        AddHandler(Keyboard.GotKeyboardFocusEvent, new KeyboardFocusChangedEventHandler(InputFocusChanging), true);
        AddHandler(Keyboard.LostKeyboardFocusEvent, new KeyboardFocusChangedEventHandler(InputFocusChanging), true);
        Unloaded += (_, _) => ClearInputFocus();
        _active = EnsureTrailingRow(); SetTheme(true);
    }

    /// <summary>跟踪本表输入框的目标焦点，属性和值均能获得键盘输入或复制。</summary>
    private void InputFocusChanging(object sender, KeyboardFocusChangedEventArgs args)
    {
        _observedFocus = args.NewFocus; TrackInputFocus(args.NewFocus);
    }

    private void TrackInputFocus(IInputElement? target)
    {
        _observedFocus = target;
        if (_rows.Any(r => r.MenuOpen || r.MenuOpening)) return; // 临时开启状态有 Dispatcher 收尾，不能永久阻断。
        _focusedInput = target is WpfTextBox box && _rows.Any(r => ReferenceEquals(r.Input, box) || ReferenceEquals(r.Value, box))
            ? box : null;
        PublishInputFocus();
    }

    /// <summary>进入 CAD 命令上下文前允许宿主取回焦点，不改变选择集。</summary>
    internal void SuspendInputFocus() { _focusSuspended = true; PublishInputFocus(); }
    /// <summary>提交或拒绝结束后恢复正常输入；不主动激活或移动系统焦点。</summary>
    internal void ResumeInputFocus() { _focusSuspended = false; PublishInputFocus(); }
    /// <summary>隐藏、关闭或卸载时释放焦点保留，防止原生面板一直占有键盘。</summary>
    internal void ClearInputFocus()
    {
        _clearingMenus = true;
        try
        {
            foreach (var row in _rows)
            {
                row.MenuRevision++; row.MenuOpen = false; row.MenuOpening = false;
                row.Input.ContextMenu.IsOpen = false; row.Value.ContextMenu.IsOpen = false;
            }
        }
        finally { _clearingMenus = false; }
        _focusedInput = null; _observedFocus = null; _focusSuspended = false; PublishInputFocus();
    }

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
                row.Cell = cell; row.Dirty = false;
                row.Value.IsReadOnly = !cell.Permission.Allowed || cell.EditToken == Guid.Empty;
                row.Editing = !row.Value.IsReadOnly && ReferenceEquals(_focusedInput, row.Value);
                row.Value.Text = row.Editing ? cell.Value.Raw : cell.Value.Display;
                UpdateMixedHint(row);
                row.Value.ToolTip = Tip(cell.Value.Display + (string.IsNullOrWhiteSpace(cell.Value.Detail) ? "" : "\n" + cell.Value.Detail)
                    + "\n" + cell.Permission.Reason);
            }
        }
        finally { _updating = false; }
    }

    /// <summary>状态变化立即撤销编辑许可，保留路径输入；读取权限重新确认后再开放。</summary>
    internal void SuspendEditing()
    {
        SetCatalogAvailability(false, "正在刷新或忙碌，暂不可检索。");
        _updating = true;
        try
        {
            foreach (var row in _rows)
            {
                row.Value.IsReadOnly = true;
                if (row.Editing) row.Value.Text = row.Cell?.Value.Display ?? "";
                row.Cell = null; row.Editing = false; row.Dirty = false; UpdateMixedHint(row);
            }
        }
        finally { _updating = false; }
    }

    internal void ShowStatus(string message) { _footer.Text = message; _footer.ToolTip = Tip(message); }

    /// <summary>宿主在完整选择读取后开放检索；禁用状态仍显示具体原因。</summary>
    internal void SetCatalogAvailability(bool allowed, string reason)
    {
        _catalog.IsEnabled = allowed; _catalog.ToolTip = Tip(reason);
    }

    /// <summary>只追加规范化后缺失的属性；不移除已有行、不赋值或写入对象。</summary>
    /// <returns>本次追加行数；零项时不改票据和行修订。</returns>
    internal int AppendCatalogProperties(IEnumerable<string> selected)
    {
        var additions = PnPropertyCatalogPolicy.MissingPaths(_rows.Select(x => x.Input.Text), selected);
        if (additions.Count == 0) return 0;
        SuspendEditing(); RowsChanging?.Invoke();
        foreach (string name in additions) AddRow(name, beforeTail: true);
        EnsureTrailingRow(); RowsCommitted?.Invoke();
        return additions.Count;
    }

    /// <summary>连续行不含分组，属性名和只读值均能选择复制。</summary>
    private PropertyRow AddRow(string name, bool beforeTail = false)
    {
        var input = CellBox(); var value = CellBox(valueCell: true); value.IsReadOnly = true; value.Text = "";
        var grid = Columns(); grid.Children.Add(input); Grid.SetColumn(value, 1); grid.Children.Add(value);
        var mixedHint = Text("多种值", new Thickness(8, 3, 6, 3)); mixedHint.IsHitTestVisible = false;
        mixedHint.Visibility = Visibility.Collapsed; mixedHint.SetResourceReference(TextBlock.ForegroundProperty, "PanelMuted");
        Grid.SetColumn(mixedHint, 1); grid.Children.Add(mixedHint);
        var split = new Border { BorderThickness = new Thickness(1, 0, 0, 0), IsHitTestVisible = false };
        split.SetResourceReference(Border.BorderBrushProperty, "PanelBorder"); Grid.SetColumn(split, 1); grid.Children.Add(split);
        var container = Surface(grid); container.BorderThickness = new Thickness(1, 0, 1, 1);
        var row = new PropertyRow { Input = input, Value = value, Container = container, Committed = name, MixedHint = mixedHint };
        input.Text = name;
        input.TextChanged += (_, _) =>
        {
            if (_updating) return;
            row.Revision++; row.PendingPath = true; SuspendEditing();
            row.Value.Text = ""; RowsChanging?.Invoke();
        };
        input.GotKeyboardFocus += (_, _) => _active = row;
        input.LostKeyboardFocus += (_, e) => { TrackInputFocus(e.NewFocus); if (!row.MenuOpen && !row.MenuOpening) CommitPath(row); };
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
            if (value.IsReadOnly || row.Cell == null || row.Editing) return;
            row.Editing = true; row.Dirty = false;
            _updating = true;
            try { value.Text = row.Cell.Value.Raw; UpdateMixedHint(row); }
            finally { _updating = false; }
        };
        value.TextChanged += (_, _) =>
        {
            if (!_updating && ReferenceEquals(_focusedInput, value) && !value.IsReadOnly && row.Cell != null)
            { row.Editing = true; row.Dirty = true; UpdateMixedHint(row); }
        };
        value.LostKeyboardFocus += (_, e) =>
        {
            TrackInputFocus(e.NewFocus);
            if (row.MenuOpen || row.MenuOpening) return;
            CommitValue(row); RestoreDisplay(row);
        };
        value.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { e.Handled = true; CommitValue(row); }
            else if (e.Key == Key.Escape)
            {
                e.Handled = true; row.Dirty = false; row.Editing = !value.IsReadOnly && row.Cell != null;
                if (row.Cell != null)
                {
                    _updating = true;
                    try { value.Text = row.Cell.Value.Raw; UpdateMixedHint(row); }
                    finally { _updating = false; }
                }
            }
            else if (e.Key is Key.Delete or Key.Back && !value.IsReadOnly && row.Cell?.Value.Kind == FastValueKind.Mixed)
            {
                // 混合值的输入缓冲本来为空；明确 Delete/Backspace 仍代表用户要求清空。
                row.Editing = true; row.Dirty = true; UpdateMixedHint(row);
            }
        };
        BindRemoveShortcut(row, input); BindRemoveShortcut(row, value);
        input.ContextMenu = RowMenu(row, input); value.ContextMenu = RowMenu(row, value);
        int index = beforeTail && _rows.Count != 0 && IsPlaceholder(_rows[^1]) ? _rows.Count - 1 : _rows.Count;
        _rows.Insert(index, row); _table.Children.Insert(index, container);
        return row;
    }

    /// <summary>只补末尾占位，不改变已有行、焦点及滚动位置。</summary>
    private PropertyRow EnsureTrailingRow() => _rows.Count != 0 && string.IsNullOrWhiteSpace(_rows[^1].Input.Text)
        ? _rows[^1] : AddRow("");

    /// <summary>手动添加入口定位已有空行，提交末行后也不重复创建。</summary>
    private void FocusTrailingRow()
    {
        if (_rows.Count != 0) CommitPath(_rows[^1]);
        var row = EnsureTrailingRow(); _active = row;
        row.Input.Focus(); row.Input.BringIntoView();
    }

    private bool IsPlaceholder(PropertyRow row) => _rows.Count != 0 && ReferenceEquals(row, _rows[^1])
        && string.IsNullOrWhiteSpace(row.Input.Text);

    /// <summary>移除入口在焦点离开前废弃未提交值；不触发 EditRequested。</summary>
    private void DiscardForRemoval(PropertyRow? row)
    {
        if (row == null || !_rows.Contains(row) || IsPlaceholder(row)) return;
        row.Editing = false; row.Dirty = false; row.Cell = null; row.MenuRevision++; row.Revision++;
        SuspendEditing(); RowsChanging?.Invoke();
    }

    /// <summary>移除明确指定的行，右键不依赖先前活动行；失焦不能提交已移除行。</summary>
    private void RemoveRow(PropertyRow row)
    {
        if (!_rows.Contains(row) || IsPlaceholder(row)) return;
        DiscardForRemoval(row);
        if (ReferenceEquals(_focusedInput, row.Input) || ReferenceEquals(_focusedInput, row.Value)) ClearInputFocus();
        _rows.Remove(row); _table.Children.Remove(row.Container);
        var tail = EnsureTrailingRow();
        if (ReferenceEquals(_active, row)) _active = tail;
        RowsCommitted?.Invoke();
    }

    /// <summary>快捷键只绑定到本表单元格，不注册 CAD 全局命令或快捷键。</summary>
    private void BindRemoveShortcut(PropertyRow row, WpfTextBox box)
    {
        box.CommandBindings.Add(new CommandBinding(_removePropertyRow,
            (_, e) => { e.Handled = true; RemoveRow(row); },
            (_, e) => { e.CanExecute = _rows.Contains(row) && !IsPlaceholder(row); e.Handled = true; }));
        box.InputBindings.Add(new KeyBinding(_removePropertyRow, new KeyGesture(Key.D, ModifierKeys.Control))
            { CommandTarget = box });
        box.PreviewKeyDown += (_, e) => HandleRemoveShortcut(e, box);
    }

    /// <summary>在移除导致焦点变化前消费 Ctrl+D，防止按键泄漏到 CAD；尾空行只消费不移除。</summary>
    private bool HandleRemoveShortcut(System.Windows.Input.KeyEventArgs args, WpfTextBox target)
    {
        if (args.Key != Key.D || args.KeyboardDevice.Modifiers != ModifierKeys.Control) return false;
        args.Handled = true;
        if (_removePropertyRow.CanExecute(null, target)) _removePropertyRow.Execute(null, target);
        return true;
    }

    /// <summary>保留文本菜单，增加行级移除；弹出菜单借用焦点不隐式写入值。</summary>
    private ContextMenu RowMenu(PropertyRow row, WpfTextBox box)
    {
        var menu = new ContextMenu { Style = (Style)_controlStyles[typeof(ContextMenu)] };
        // Popup 不在原视图的可视树中，显式桥接资源，主题切换才能同步覆盖菜单内部。
        menu.Resources.MergedDictionaries.Add(Resources);
        var itemStyle = (Style)_controlStyles[typeof(MenuItem)];
        foreach (var pair in new[] { ("撤销", ApplicationCommands.Undo), ("剪切", ApplicationCommands.Cut),
            ("复制", ApplicationCommands.Copy), ("粘贴", ApplicationCommands.Paste), ("全选", ApplicationCommands.SelectAll) })
            menu.Items.Add(new MenuItem { Header = pair.Item1, Command = pair.Item2, CommandTarget = box, Style = itemStyle });
        menu.Items.Add(new Separator { Style = (Style)_controlStyles[typeof(Separator)] });
        var remove = new MenuItem
        {
            Header = "移除此属性行", Style = itemStyle, Command = _removePropertyRow,
            CommandTarget = box, InputGestureText = "Ctrl+D"
        };
        menu.Items.Add(remove);
        menu.PreviewKeyDown += (_, e) => { if (HandleRemoveShortcut(e, box)) menu.IsOpen = false; };
        box.PreviewMouseRightButtonDown += (_, _) => _active = row;
        box.ContextMenuOpening += (_, _) =>
        {
            row.MenuOpening = true; _active = row; remove.IsEnabled = !IsPlaceholder(row);
            long revision = ++row.MenuRevision;
            Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() =>
            {
                if (!_rows.Contains(row) || revision != row.MenuRevision || _clearingMenus) return;
                row.MenuOpening = false;
                if (menu.IsOpen) row.MenuOpen = true;
                else FinishMenu(row);
            }));
        };
        menu.Opened += (_, _) =>
        {
            row.MenuRevision++; row.MenuOpening = false; row.MenuOpen = true;
            _active = row; remove.IsEnabled = !IsPlaceholder(row);
        };
        menu.Closed += (_, _) =>
        {
            row.MenuOpening = false; row.MenuOpen = false;
            if (_clearingMenus) return;
            long revision = ++row.MenuRevision;
            Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() =>
            { if (_rows.Contains(row) && revision == row.MenuRevision && !_clearingMenus) FinishMenu(row); }));
        };
        return menu;
    }

    /// <summary>菜单没有打开或已经关闭后按实际焦点收尾，返回编辑器不丢草稿，离开才提交。</summary>
    private void FinishMenu(PropertyRow row)
    {
        row.MenuOpening = false; row.MenuOpen = false;
        TrackInputFocus(_observedFocus);
        if (!ReferenceEquals(_observedFocus, row.Input)) CommitPath(row);
        if (!ReferenceEquals(_observedFocus, row.Value)) { CommitValue(row); RestoreDisplay(row); }
    }

    private void RestoreDisplay(PropertyRow row)
    {
        if (row.Cell == null) return;
        _updating = true;
        try
        {
            row.Editing = !row.Value.IsReadOnly && ReferenceEquals(_observedFocus, row.Value);
            row.Value.Text = row.Editing ? row.Cell.Value.Raw : row.Cell.Value.Display;
            UpdateMixedHint(row);
        }
        finally { _updating = false; }
    }

    private static void UpdateMixedHint(PropertyRow row) => row.MixedHint.Visibility =
        row.Editing && !row.Dirty && row.Cell?.Value.Kind == FastValueKind.Mixed && row.Value.Text.Length == 0
            ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>回车或离开路径单元格才触发读取，编辑过程中不查询新路径。</summary>
    private void CommitPath(PropertyRow row)
    {
        if (_clearingMenus || !_rows.Contains(row) || !row.PendingPath) return;
        row.Committed = row.Input.Text; row.PendingPath = false;
        string? path = PnPropertyPolicy.Normalize(row.Committed, out string reason);
        row.Input.ToolTip = Tip(path ?? reason); EnsureTrailingRow(); RowsCommitted?.Invoke();
    }

    /// <summary>提交当前票据及原值；视图不操作 SDK，拒绝过期由宿主再次验证。</summary>
    private void CommitValue(PropertyRow row)
    {
        if (_updating || _clearingMenus || !_rows.Contains(row) || row.MenuOpen || row.MenuOpening || !row.Editing || !row.Dirty || row.Value.IsReadOnly || row.Cell == null) return;
        var cell = row.Cell; row.Editing = false; row.Dirty = false; UpdateMixedHint(row);
        string value = row.Value.Text;
        if (cell.Value.Kind == FastValueKind.Value && value == cell.Value.Raw)
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
        string[] keys = ["PanelBackground", "PanelSurface", "PanelForeground", "PanelMuted", "PanelBorder", "PanelHeader", "PanelSelection",
            "PanelReadOnlyBackground", "PanelReadOnlyForeground", "PanelMenuHover"];
        string[] colors = dark ? ["#384252", "#3E4959", "#EDF1F6", "#C1CAD6", "#505D70", "#2D3644", "#6686AB", "#354050", "#9FA9B7", "#4B617E"]
            : ["#EDF0F4", "#F9FAFC", "#263244", "#59697D", "#CBD3DD", "#DEE4EC", "#A8C8E8", "#E6E9EE", "#737C88", "#D5E2F2"];
        for (int i = 0; i < keys.Length; i++)
        {
            var brush = new SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(colors[i]));
            brush.Freeze(); Resources[keys[i]] = brush;
        }
    }

    private static Grid Columns()
    {
        var grid = new Grid { MinHeight = 24 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        return grid;
    }
    /// <summary>创建紧凑属性格；值格仅改变只读外观，仍启用复制、焦点及 Tooltip。</summary>
    /// <param name="valueCell">值列使用 IsReadOnly 样式触发器，属性列始终采用正常画刷。</param>
    /// <returns>仅在所属 WPF UI 线程使用的单行文本格。</returns>
    private static WpfTextBox CellBox(bool valueCell = false)
    {
        var box = new WpfTextBox { Padding = new Thickness(8, 3, 6, 3), BorderThickness = new Thickness(0),
            VerticalContentAlignment = VerticalAlignment.Center, IsReadOnlyCaretVisible = true,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden, MinWidth = 0 };
        if (valueCell)
        {
            // 正常画刷放入样式，避免局部动态资源的优先级覆盖只读触发器。
            var style = new Style(typeof(WpfTextBox));
            style.Setters.Add(new Setter(BackgroundProperty, new DynamicResourceExtension("PanelSurface")));
            style.Setters.Add(new Setter(ForegroundProperty, new DynamicResourceExtension("PanelForeground")));
            var readOnly = new Trigger { Property = WpfTextBox.IsReadOnlyProperty, Value = true };
            readOnly.Setters.Add(new Setter(BackgroundProperty, new DynamicResourceExtension("PanelReadOnlyBackground")));
            readOnly.Setters.Add(new Setter(ForegroundProperty, new DynamicResourceExtension("PanelReadOnlyForeground")));
            style.Triggers.Add(readOnly);
            box.Style = style;
        }
        else
        {
            box.SetResourceReference(BackgroundProperty, "PanelSurface");
            box.SetResourceReference(ForegroundProperty, "PanelForeground");
        }
        box.SetResourceReference(WpfTextBox.CaretBrushProperty, "PanelForeground"); box.SetResourceReference(WpfTextBox.SelectionBrushProperty, "PanelSelection");
        ToolTipService.SetShowDuration(box, 60000); return box;
    }
    private WpfButton Button(string title) => new()
    {
        Content = title, Padding = new Thickness(9, 4, 9, 4), Margin = new Thickness(0, 0, 8, 0),
        Style = (Style)_controlStyles[typeof(WpfButton)]
    };
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
