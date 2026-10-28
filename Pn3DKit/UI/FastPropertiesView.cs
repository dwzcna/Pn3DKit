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
    // 仅给结构化混合、空白和 null 提示设置半透明前景，不改变控件、光标或选区透明度。
    private static readonly DependencyProperty PlaceholderProperty = DependencyProperty.RegisterAttached(
        "ValuePlaceholder", typeof(bool), typeof(FastPropertiesView), new FrameworkPropertyMetadata(false));
    // 草稿以背景及提示表达，不把状态文字拼入实际输入。
    private static readonly DependencyProperty DraftProperty = DependencyProperty.RegisterAttached(
        "ValueDraft", typeof(bool), typeof(FastPropertiesView), new FrameworkPropertyMetadata(false));

    private sealed class PropertyRow
    {
        internal readonly Guid Id = Guid.NewGuid();
        internal long Revision;
        internal required WpfTextBox Input;
        internal required WpfTextBox Value;
        internal required Border Container;
        internal string Committed = "";
        internal string Draft = "";
        internal bool PendingPath, Editing, Dirty, MenuOpen, MenuOpening;
        internal long MenuRevision, DraftRevision;
        internal required TextBlock PlaceholderHint;
        internal PnPropertyCell? Cell;
    }
    private readonly List<PropertyRow> _rows = [];
    private readonly StackPanel _table = new();
    private readonly ResourceDictionary _controlStyles = FastPanelControlStyles.CreateResources();
    private readonly RoutedUICommand _removePropertyRow = new("移除此属性行", "RemovePropertyRow", typeof(FastPropertiesView));
    private readonly RoutedUICommand _unifyValue = new("统一键值", "UnifyValue", typeof(FastPropertiesView));
    private PnUnifiedValueRequest? _unifyRequest;
    private int _selectionCount;
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
    internal event Action<PnUnifiedValueRequest>? UnifyValueRequested;

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
        SuspendEditing();
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
        _selectionCount = snapshot.Count;
        _objectType.Text = snapshot.Header; _objectType.ToolTip = Tip(snapshot.Header);
        ShowStatus(snapshot.Status);
        _updating = true;
        try
        {
            foreach (var cell in snapshot.Cells)
            {
                var row = _rows.FirstOrDefault(r => r.Id == cell.Spec.Id);
                if (row == null || row.Revision != cell.Spec.Revision || row.PendingPath) continue;
                bool keepDraft = row.Dirty && cell.Permission.Allowed && cell.EditToken != Guid.Empty
                    && row.Cell is { } previous && previous.EditToken == cell.EditToken
                    && previous.Spec == cell.Spec && previous.Value.Kind == cell.Value.Kind
                    && previous.Value.Raw == cell.Value.Raw;
                if (!keepDraft)
                {
                    if (row.Dirty || row.Cell?.EditToken != cell.EditToken || row.Cell?.Value.Kind != cell.Value.Kind
                        || row.Cell?.Value.Raw != cell.Value.Raw) row.DraftRevision++;
                    row.Dirty = false; row.Draft = "";
                }
                row.Cell = cell;
                row.Value.IsReadOnly = !cell.Permission.Allowed || cell.EditToken == Guid.Empty;
                RestoreDisplay(row);
            }
        }
        finally { _updating = false; }
    }

    /// <summary>状态变化立即撤销编辑许可，保留路径输入；读取权限重新确认后再开放。</summary>
    internal void SuspendEditing()
    {
        _unifyRequest = null;
        SetCatalogAvailability(false, "正在刷新或忙碌，暂不可检索。");
        _updating = true;
        try
        {
            foreach (var row in _rows)
            {
                row.Value.IsReadOnly = true;
                if (row.Cell != null) row.Value.Text = row.Cell.Value.Display;
                row.Editing = false; row.Dirty = false; row.Draft = ""; row.DraftRevision++;
                if (row.Cell != null) UpdatePlaceholder(row);
                else row.PlaceholderHint.Visibility = Visibility.Collapsed;
                row.Cell = null;
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
        var mixedHint = Text("*(多种)", new Thickness(8, 3, 6, 3)); mixedHint.IsHitTestVisible = false;
        mixedHint.Visibility = Visibility.Collapsed; mixedHint.Opacity = 0.5;
        Grid.SetColumn(mixedHint, 1); grid.Children.Add(mixedHint);
        var split = new Border { BorderThickness = new Thickness(1, 0, 0, 0), IsHitTestVisible = false };
        split.SetResourceReference(Border.BorderBrushProperty, "PanelBorder"); Grid.SetColumn(split, 1); grid.Children.Add(split);
        var container = Surface(grid); container.BorderThickness = new Thickness(1, 0, 1, 1);
        var row = new PropertyRow { Input = input, Value = value, Container = container, Committed = name, PlaceholderHint = mixedHint };
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
            row.Editing = true;
            _updating = true;
            try { value.Text = row.Dirty ? row.Draft : row.Cell.Value.Raw ?? ""; UpdatePlaceholder(row); }
            finally { _updating = false; }
        };
        value.TextChanged += (_, _) =>
        {
            if (!_updating && ReferenceEquals(_focusedInput, value) && !value.IsReadOnly && row.Cell != null)
            { row.Editing = true; row.Dirty = true; row.Draft = value.Text; row.DraftRevision++; UpdatePlaceholder(row); }
        };
        value.LostKeyboardFocus += (_, e) =>
        {
            TrackInputFocus(e.NewFocus);
            if (row.MenuOpen || row.MenuOpening) return;
            RestoreDisplay(row);
        };
        value.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { e.Handled = true; CommitValue(row); }
            else if (e.Key == Key.Escape)
            {
                e.Handled = true; row.Dirty = false; row.Draft = ""; row.DraftRevision++; RestoreDisplay(row);
            }
            else if (e.Key is Key.Delete or Key.Back && !value.IsReadOnly && row.Cell?.Value.Placeholder != null)
            {
                // 占位状态的空缓冲也需要区分明确清空和未修改；不把提示文字作为输入。
                row.Editing = true; row.Dirty = true; row.Draft = value.Text; row.DraftRevision++; UpdatePlaceholder(row);
            }
        };
        BindRemoveShortcut(row, input); BindRemoveShortcut(row, value);
        BindUnifyShortcut(row, value);
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

    /// <summary>混合值且全部可写时才开放；只查询面板快照，不在菜单开启时访问 SDK。</summary>
    private string? UnifyReason(PropertyRow row, bool includePending = true)
    {
        if (!_rows.Contains(row) || row.PendingPath) return "属性行已移除或路径尚未确认。";
        if (_selectionCount < 2) return "请选择多个 Plant 对象并等待读取完成。";
        if (row.Cell?.Value.Kind != FastValueKind.Mixed) return "仅不同值的共有属性可以统一键值。";
        if (!row.Cell.Permission.Allowed) return row.Cell.Permission.Reason;
        if (row.Cell.EditToken == Guid.Empty || row.Value.IsReadOnly) return "编辑票据已失效或环境忙碌，请等待读取完成。";
        if (includePending && _unifyRequest != null) return "正在打开或使用统一键值窗口，请勿重复操作。";
        return null;
    }

    /// <summary>只绑定值格；禁用和长按也消费 Ctrl+U，避免传到 CAD 全局快捷键。</summary>
    private void BindUnifyShortcut(PropertyRow row, WpfTextBox box)
    {
        box.CommandBindings.Add(new CommandBinding(_unifyValue,
            (_, e) => { e.Handled = true; QueueUnify(row); },
            (_, e) => { e.Handled = true; e.CanExecute = UnifyReason(row) == null; }));
        box.InputBindings.Add(new KeyBinding(_unifyValue, new KeyGesture(Key.U, ModifierKeys.Control)) { CommandTarget = box });
        box.PreviewKeyDown += (_, e) => HandleUnifyShortcut(e, box, e.KeyboardDevice.Modifiers);
    }

    /// <summary>统一键值与菜单复用同一命令；参数化修饰键只供本地路由，不注册宿主快捷键。</summary>
    private bool HandleUnifyShortcut(System.Windows.Input.KeyEventArgs args, WpfTextBox box, ModifierKeys modifiers)
    {
        if (args.Key != Key.U || modifiers != ModifierKeys.Control) return false;
        args.Handled = true;
        if (!args.IsRepeat && _unifyValue.CanExecute(null, box)) _unifyValue.Execute(null, box);
        return true;
    }

    /// <summary>先关闭右键菜单，再延迟固定文本请求；重复命令只排队一次。</summary>
    private void QueueUnify(PropertyRow row)
    {
        if (UnifyReason(row) != null || row.Cell == null) return;
        string? path = PnPropertyPolicy.Normalize(row.Committed, out _);
        if (path == null) return;
        var request = new PnUnifiedValueRequest(row.Cell.EditToken, row.Id, row.Revision, path,
            row.DraftRevision, row.Dirty ? row.Draft : null);
        _unifyRequest = request;
        row.Value.ContextMenu.IsOpen = false;
        Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() =>
        {
            if (IsUnifiedRequestCurrent(request) && UnifyValueRequested is { } handler) handler(request);
            else CompleteUnifiedRequest(request, restoreFocus: false);
        }));
    }

    /// <summary>行、路径、非空票据、权限和独立草稿修订必须全部保持；不读取 CAD。</summary>
    internal bool IsUnifiedRequestCurrent(PnUnifiedValueRequest request) => UnifiedRow(request) != null;

    private PropertyRow? UnifiedRow(PnUnifiedValueRequest request)
    {
        if (_unifyRequest != request) return null;
        var row = _rows.FirstOrDefault(x => x.Id == request.RowId);
        return row != null && UnifyReason(row, includePending: false) == null
            && row.Revision == request.RowRevision && row.DraftRevision == request.DraftRevision
            && row.Cell!.EditToken == request.Token && PnPropertyPolicy.Normalize(row.Committed, out _) == request.Path
            ? row : null;
    }

    /// <summary>确认仅回填当前票据的草稿；不提交、改路径、刷新票据或触发 SDK 写入。</summary>
    internal bool TryApplyUnifiedDraft(PnUnifiedValueRequest request, string value)
    {
        var row = UnifiedRow(request);
        if (row == null) return false;
        bool updating = _updating; _updating = true;
        try
        {
            row.Draft = value; row.Dirty = true; row.DraftRevision++;
            row.Value.Text = value; UpdatePlaceholder(row);
        }
        finally { _updating = updating; }
        _unifyRequest = null; RestoreUnifiedFocus(row, request.Token, row.DraftRevision);
        return true;
    }

    /// <summary>取消及异常只结束请求；有效时延迟恢复原格焦点，不能触发提交。</summary>
    internal void CompleteUnifiedRequest(PnUnifiedValueRequest request, bool restoreFocus = true)
    {
        if (_unifyRequest != request) return;
        var row = UnifiedRow(request); _unifyRequest = null;
        if (restoreFocus && row != null) RestoreUnifiedFocus(row, request.Token, row.DraftRevision);
    }

    /// <summary>等模态键盘事件结束后再恢复；新输入或上下文变化后不抢焦点。</summary>
    private void RestoreUnifiedFocus(PropertyRow row, Guid token, long draftRevision) =>
        Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() =>
        {
            if (_rows.Contains(row) && !row.Value.IsReadOnly && row.Cell?.EditToken == token && row.DraftRevision == draftRevision)
                row.Value.Focus();
        }));

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
        MenuItem? unify = null;
        if (ReferenceEquals(box, row.Value))
        {
            unify = new MenuItem { Header = "统一键值", Style = itemStyle, Command = _unifyValue,
                CommandTarget = box, InputGestureText = "Ctrl+U" };
            ToolTipService.SetShowOnDisabled(unify, true); menu.Items.Add(unify);
        }
        void UpdateUnifyTip()
        {
            if (unify != null) unify.ToolTip = Tip(UnifyReason(row) ?? "选择现有值或输入新值，确定只回填草稿，再按 Enter 提交。");
        }
        UpdateUnifyTip();
        var remove = new MenuItem
        {
            Header = "移除此属性行", Style = itemStyle, Command = _removePropertyRow,
            CommandTarget = box, InputGestureText = "Ctrl+D"
        };
        menu.Items.Add(remove);
        menu.PreviewKeyDown += (_, e) =>
        {
            if (ReferenceEquals(box, row.Value) && HandleUnifyShortcut(e, box, e.KeyboardDevice.Modifiers)) menu.IsOpen = false;
            else if (HandleRemoveShortcut(e, box)) menu.IsOpen = false;
        };
        box.PreviewMouseRightButtonDown += (_, _) => _active = row;
        box.ContextMenuOpening += (_, _) =>
        {
            row.MenuOpening = true; _active = row; remove.IsEnabled = !IsPlaceholder(row); UpdateUnifyTip();
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
            _active = row; remove.IsEnabled = !IsPlaceholder(row); UpdateUnifyTip();
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

    /// <summary>菜单结束后释放借用焦点；值草稿保留到回车，路径仍按失焦确认。</summary>
    private void FinishMenu(PropertyRow row)
    {
        row.MenuOpening = false; row.MenuOpen = false;
        TrackInputFocus(_observedFocus);
        if (!ReferenceEquals(_observedFocus, row.Input)) CommitPath(row);
        RestoreDisplay(row);
    }

    /// <summary>焦点切换只切换原值、混合提示和当前票据的草稿显示，不产生提交。</summary>
    private void RestoreDisplay(PropertyRow row)
    {
        if (row.Cell == null) return;
        bool updating = _updating;
        _updating = true;
        try
        {
            row.Editing = !row.Value.IsReadOnly && ReferenceEquals(_observedFocus, row.Value);
            string text = row.Dirty ? row.Draft : row.Editing ? row.Cell.Value.Raw ?? "" : row.Cell.Value.Display;
            if (row.Value.Text != text) row.Value.Text = text;
            UpdatePlaceholder(row);
        }
        finally { _updating = updating; }
    }

    /// <summary>按结构化状态显示混合、空白或 null 占位；真实同文输入仍使用正常前景。</summary>
    private static void UpdatePlaceholder(PropertyRow row)
    {
        string? placeholder = row.Cell?.Value.Placeholder;
        bool draft = row.Dirty && !row.Value.IsReadOnly && row.Cell?.EditToken is { } token && token != Guid.Empty;
        row.Value.SetValue(DraftProperty, draft);
        row.Value.SetValue(PlaceholderProperty, !row.Dirty && placeholder != null);
        row.PlaceholderHint.Text = placeholder ?? "";
        row.PlaceholderHint.Visibility = row.Editing && !row.Dirty && placeholder != null && string.IsNullOrWhiteSpace(row.Value.Text)
            ? Visibility.Visible : Visibility.Collapsed;
        if (row.Cell is { } cell)
            row.Value.ToolTip = Tip(cell.Value.Display + (string.IsNullOrWhiteSpace(cell.Value.Detail) ? "" : "\n" + cell.Value.Detail)
                + "\n" + cell.Permission.Reason + (draft ? "\n尚未提交；按 Enter 提交，Esc 撤销。" : ""));
    }

    /// <summary>回车或离开路径单元格才触发读取，编辑过程中不查询新路径。</summary>
    private void CommitPath(PropertyRow row)
    {
        if (_clearingMenus || !_rows.Contains(row) || !row.PendingPath) return;
        row.Committed = row.Input.Text; row.PendingPath = false;
        string? path = PnPropertyPolicy.Normalize(row.Committed, out string reason);
        row.Input.ToolTip = Tip(path ?? reason); EnsureTrailingRow(); RowsCommitted?.Invoke();
    }

    /// <summary>仅回车提交当前票据绑定的草稿；视图不操作 SDK，过期由宿主再次验证。</summary>
    private void CommitValue(PropertyRow row)
    {
        if (_updating || _clearingMenus || _unifyRequest != null || !_rows.Contains(row) || row.MenuOpen || row.MenuOpening || !row.Editing || !row.Dirty || row.Value.IsReadOnly || row.Cell == null) return;
        var cell = row.Cell; string value = row.Draft;
        row.Editing = false; row.Dirty = false; row.Draft = ""; row.DraftRevision++; UpdatePlaceholder(row);
        if (cell.Value.Kind == FastValueKind.Value && value == cell.Value.Raw)
        {
            RestoreDisplay(row);
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
            "PanelReadOnlyBackground", "PanelReadOnlyForeground", "PanelMenuHover", "PanelDraftBackground"];
        string[] colors = dark ? ["#384252", "#3E4959", "#EDF1F6", "#C1CAD6", "#505D70", "#2D3644", "#6686AB", "#354050", "#9FA9B7", "#4B617E", "#5A492E"]
            : ["#EDF0F4", "#F9FAFC", "#263244", "#59697D", "#CBD3DD", "#DEE4EC", "#A8C8E8", "#E6E9EE", "#737C88", "#D5E2F2", "#FFF1CE"];
        for (int i = 0; i < keys.Length; i++)
        {
            var brush = new SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(colors[i]));
            brush.Freeze(); Resources[keys[i]] = brush;
        }
        foreach (string key in new[] { "PanelForeground", "PanelReadOnlyForeground" })
        {
            var mixed = ((SolidColorBrush)Resources[key]).Clone();
            mixed.Opacity = 0.5; mixed.Freeze();
            Resources[key.Replace("Foreground", "PlaceholderForeground")] = mixed;
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
            var mixed = new Trigger { Property = PlaceholderProperty, Value = true };
            mixed.Setters.Add(new Setter(ForegroundProperty, new DynamicResourceExtension("PanelPlaceholderForeground")));
            style.Triggers.Add(mixed);
            var mixedReadOnly = new MultiTrigger();
            mixedReadOnly.Conditions.Add(new Condition(PlaceholderProperty, true));
            mixedReadOnly.Conditions.Add(new Condition(WpfTextBox.IsReadOnlyProperty, true));
            mixedReadOnly.Setters.Add(new Setter(ForegroundProperty, new DynamicResourceExtension("PanelReadOnlyPlaceholderForeground")));
            style.Triggers.Add(mixedReadOnly);
            var draft = new MultiTrigger();
            draft.Conditions.Add(new Condition(DraftProperty, true));
            draft.Conditions.Add(new Condition(WpfTextBox.IsReadOnlyProperty, false));
            draft.Setters.Add(new Setter(BackgroundProperty, new DynamicResourceExtension("PanelDraftBackground")));
            style.Triggers.Add(draft);
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
