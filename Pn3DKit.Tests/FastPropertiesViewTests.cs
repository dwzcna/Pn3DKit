using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Pn3DKit.Models;
using Pn3DKit.UI;
using Pn3DKit.Utils;

namespace Pn3DKit.Tests;

/// <summary>在独立 STA 上分发纯 WPF 路由事件，不激活窗口、不连接 CAD 或设置系统焦点。</summary>
public sealed class FastPropertiesViewTests
{
    private static void Sta(Action action)
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception ex) { error = ex; }
            finally { Dispatcher.CurrentDispatcher.InvokeShutdown(); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(20)), "WPF 离线烟测超时。");
        if (error != null) ExceptionDispatchInfo.Capture(error).Throw();
    }

    /// <summary>初始一行空属性；无选择时仍能输入并增删，不创建三个预置字段。</summary>
    [Fact]
    public void StartsWithOneEmptyEditablePropertyRow() => Sta(() =>
    {
        var view = new FastPropertiesView();
        Assert.Single(view.ReadSpecs()); Assert.Equal("", view.ReadSpecs()[0].Input);
        var boxes = Children<TextBox>(view).ToArray();
        Assert.Equal(2, boxes.Length); Assert.False(boxes[0].IsReadOnly); Assert.True(boxes[1].IsReadOnly);
        Assert.True(boxes[1].IsReadOnlyCaretVisible); Assert.Empty(Children<Expander>(view));
        Click(view, "添加属性"); Assert.Single(view.ReadSpecs());
        Click(view, "删除属性"); Assert.Single(view.ReadSpecs());
    });

    private static (FastPropertiesView View, TextBox Path, TextBox Value) Comment(string? original = "原备注")
    {
        var view = new FastPropertiesView();
        var boxes = Children<TextBox>(view).ToArray();
        boxes[0].Text = "Comment"; Focus(boxes[0], false);
        ShowComment(view, original);
        return (view, boxes[0], boxes[1]);
    }

    /// <summary>通过生产元数据策略取得许可，不直接在烟测中硬编码 Writable。</summary>
    private static PnPanelSnapshot Snapshot(FastPropertiesView view, string? original)
    {
        var specs = view.ReadSpecs();
        var permission = PnPropertyMetadataPolicy.Evaluate(new("Comment"), ["DisplayName", "UIOrder"], _ => "描述",
            () => false, () => throw new InvalidOperationException("非获取属性"), () => throw new InvalidOperationException("无关格式"));
        var snapshot = PnPropertyPolicy.Merge(specs, [new("PnId:Valve", "阀门",
            specs.Select(s => string.IsNullOrWhiteSpace(s.Input)
                ? new PnPropertyCell(s, FastPropertyValue.Missing(), PnEditPermission.ReadOnly("请输入属性。"))
                : new PnPropertyCell(s, FastPropertyValue.Value(original), permission)).ToArray())]);
        return snapshot with { Cells = snapshot.Cells.Select(c => c with { EditToken = Guid.NewGuid() }).ToArray() };
    }
    private static void ShowComment(FastPropertiesView view, string? original) => view.Show(Snapshot(view, original));

    /// <summary>Comment 取得 SDK 元数据许可后，值框可输入；焦点切换会申请和释放原生保留。</summary>
    [Fact]
    public void CommentValueAndPropertyInputRetainKeyboardFocus() => Sta(() =>
    {
        var (view, path, value) = Comment();
        var requests = new List<bool>(); view.InputFocusChanged += requests.Add;
        Assert.False(value.IsReadOnly);
        Focus(path, true); Assert.True(view.HasInputFocus); Assert.True(requests[^1]);
        Focus(path, false); Assert.False(view.HasInputFocus); Assert.False(requests[^1]);
        Focus(value, true); Assert.True(view.HasInputFocus); Assert.True(requests[^1]);
        view.SuspendInputFocus(); Assert.False(requests[^1]);
        view.ResumeInputFocus(); Assert.True(requests[^1]);
        view.ClearInputFocus(); Assert.False(view.HasInputFocus); Assert.False(requests[^1]);
    });

    /// <summary>回车只提交一次且保留焦点；后续失焦不重复写入，回读后继续编辑。</summary>
    [Fact]
    public void EnterSubmitsCommentOnceAndKeepsFocus() => Sta(() =>
    {
        var (view, _, value) = Comment();
        var requests = new List<PnEditRequest>(); bool retain = false;
        view.EditRequested += requests.Add; view.InputFocusChanged += keep => retain = keep;
        Focus(value, true); Assert.True(retain); value.Text = "新备注"; Key(value, System.Windows.Input.Key.Enter);
        Assert.Single(requests); Assert.True(retain); Assert.True(value.IsReadOnly);
        Assert.Equal("TargetObject.Comment", requests[0].Path);
        Assert.Equal("原备注", requests[0].Original); Assert.Equal("新备注", requests[0].Value);
        Focus(value, false); Assert.Single(requests);
        view.ResumeInputFocus(); ShowComment(view, "新备注"); Assert.False(value.IsReadOnly);
    });

    /// <summary>Enter 在发出编辑请求前已被消费，排队期间保留输入焦点，避免按键进入 CAD。</summary>
    [Fact]
    public void EnterIsHandledBeforeEditRequest() => Sta(() =>
    {
        var (view, _, value) = Comment();
        Focus(value, true); value.Text = "更新";
        var key = new KeyEventArgs(Keyboard.PrimaryDevice, new TestSource(), 0, System.Windows.Input.Key.Enter)
            { RoutedEvent = Keyboard.PreviewKeyDownEvent };
        int count = 0;
        view.EditRequested += _ => { count++; Assert.True(key.Handled); Assert.True(view.HasInputFocus); };
        value.RaiseEvent(key);
        Assert.True(key.Handled); Assert.Equal(1, count);
    });

    /// <summary>提交回读后焦点仍在原值格，可连续修改且每次使用新的原值和票据。</summary>
    [Fact]
    public void ReadbackAllowsAnotherEditWithoutLosingFocus() => Sta(() =>
    {
        var (view, _, value) = Comment();
        var edits = new List<PnEditRequest>(); view.EditRequested += edits.Add;
        Focus(value, true); value.Text = "第一次"; Key(value, System.Windows.Input.Key.Enter);
        ShowComment(view, "第一次");
        Assert.True(view.HasInputFocus); Assert.False(value.IsReadOnly); Assert.Equal("第一次", value.Text);
        value.Text = "第二次"; Key(value, System.Windows.Input.Key.Enter);
        Assert.Equal(2, edits.Count); Assert.Equal("第一次", edits[1].Original);
        Assert.NotEqual(edits[0].Token, edits[1].Token);
    });

    /// <summary>提交失败后回显 SDK 实际值，保持选择对应的表格及输入焦点，不伪造成功值。</summary>
    [Fact]
    public void FailedCommitReadbackKeepsFocusAndActualValue() => Sta(() =>
    {
        var (view, _, value) = Comment();
        Focus(value, true); value.Text = "失败输入"; Key(value, System.Windows.Input.Key.Enter);
        view.ShowStatus("提交失败：字段长度超限"); ShowComment(view, "原备注");
        Assert.True(view.HasInputFocus); Assert.Equal("原备注", value.Text); Assert.False(value.IsReadOnly);
    });

    /// <summary>空 Comment 使用占位提示，编辑缓冲仍为空，Esc 与未变回车不提交标记。</summary>
    [Fact]
    public void EmptyCommentEscapeAndUnchangedEnterDoNotSubmitPlaceholder() => Sta(() =>
    {
        var (view, _, value) = Comment("");
        int submissions = 0; view.EditRequested += _ => submissions++;
        Assert.Equal("(空白)", value.Text);
        Focus(value, true); Assert.Equal("", value.Text);
        value.Text = "撤销的输入"; Key(value, System.Windows.Input.Key.Escape);
        Assert.Equal("", value.Text);
        Key(value, System.Windows.Input.Key.Enter); Key(value, System.Windows.Input.Key.Enter);
        Focus(value, false); Assert.Equal(0, submissions);
        Assert.Equal("(空白)", value.Text);
        Focus(value, true); value.Text = "首条备注"; Focus(value, false);
        Assert.Equal(0, submissions); Assert.Equal("首条备注", value.Text);
        Focus(value, true); Key(value, System.Windows.Input.Key.Enter);
        Assert.Equal(1, submissions);
    });

    /// <summary>已有空 Comment 可编辑；确认缺少 LoopNumber 才显示双横线，标记不进入写入请求。</summary>
    [Fact]
    public void EmptyAndAbsentPropertiesHaveDistinctCellsAndOnlyRealInputIsSubmitted() => Sta(() =>
    {
        var view = new FastPropertiesView();
        view.AppendCatalogProperties(["Comment", "LoopNumber"]);
        var specs = view.ReadSpecs();
        view.Show(new("手动阀门", 1, specs.Select(spec => spec.Input switch
        {
            "Comment" => new PnPropertyCell(spec, FastPropertyValue.Value(""), PnEditPermission.Writable, Guid.NewGuid()),
            "LoopNumber" => new PnPropertyCell(spec, FastPropertyValue.AbsentProperty("对象不含 LoopNumber"),
                PnEditPermission.ReadOnly("属性不存在。")),
            _ => new PnPropertyCell(spec, FastPropertyValue.Missing(), PnEditPermission.ReadOnly("请输入属性。"))
        }).ToArray(), "已选 1 个对象"));
        var boxes = Children<TextBox>(view).ToArray();
        Assert.Equal("(空白)", boxes[1].Text); Assert.False(boxes[1].IsReadOnly);
        Assert.Equal("--", boxes[3].Text); Assert.True(boxes[3].IsReadOnly);
        Assert.Equal("", boxes[5].Text);
        var edits = new List<PnEditRequest>(); view.EditRequested += edits.Add;
        Focus(boxes[1], true); boxes[1].Text = "首条备注"; Key(boxes[1], System.Windows.Input.Key.Enter);
        Assert.Single(edits); Assert.Equal("", edits[0].Original); Assert.Equal("首条备注", edits[0].Value);
        Focus(boxes[3], true); Key(boxes[3], System.Windows.Input.Key.Enter);
        Assert.Single(edits);
    });

    /// <summary>失焦保留草稿和有效票据，回到值格后只能通过 Enter 提交一次。</summary>
    [Fact]
    public void LostFocusKeepsDraftUntilEnter() => Sta(() =>
    {
        var (view, _, value) = Comment();
        var edits = new List<PnEditRequest>(); view.EditRequested += edits.Add;
        Focus(value, true); value.Text = "失焦备注"; Focus(value, false); Focus(value, false);
        Assert.Empty(edits); Assert.Equal("失焦备注", value.Text); Assert.False(view.HasInputFocus);
        Focus(value, true); Assert.Equal("失焦备注", value.Text);
        Key(value, System.Windows.Input.Key.Enter); Key(value, System.Windows.Input.Key.Enter); Focus(value, false);
        Assert.Single(edits); Assert.Equal("失焦备注", edits[0].Value);
    });

    /// <summary>修改路径立即停用旧值，旧快照不恢复权限。</summary>
    [Fact]
    public void ChangingPropertyRejectsOldResultAndOldEditing() => Sta(() =>
    {
        var (view, path, value) = Comment();
        var old = Snapshot(view, "原备注"); view.Show(old);
        int submissions = 0; view.EditRequested += _ => submissions++;
        Focus(value, true); value.Text = "过期输入";
        path.Text = "Description"; Assert.True(value.IsReadOnly);
        view.Show(old); Assert.True(value.IsReadOnly);
        Key(value, System.Windows.Input.Key.Enter); Focus(value, false);
        Assert.Equal(0, submissions); Assert.Equal("Description", path.Text);
    });

    /// <summary>只读或多选结果仍可复制，不能进入写入流程。</summary>
    [Fact]
    public void MultiSelectionAndReadOnlyStillCannotWrite() => Sta(() =>
    {
        var (view, path, value) = Comment();
        var spec = view.ReadSpecs()[0];
        var item = new PnObjectProperties("x", "阀门", [new(spec, FastPropertyValue.Value("备注"), PnEditPermission.Writable)]);
        view.Show(PnPropertyPolicy.Merge([spec], [item, item]));
        Assert.True(value.IsReadOnly); Assert.True(value.IsReadOnlyCaretVisible); Assert.False(path.IsReadOnly);
        int submissions = 0; view.EditRequested += _ => submissions++;
        Focus(value, true); Key(value, System.Windows.Input.Key.Enter); Focus(value, false);
        Assert.Equal(0, submissions);
    });

    /// <summary>末行提交后补空行，重复 Enter／失焦不重复补行或移动输入焦点。</summary>
    [Fact]
    public void CommittingLastPathAddsOneTrailingRowWithoutTakingFocus() => Sta(() =>
    {
        var view = new FastPropertiesView(); var path = Children<TextBox>(view).First();
        Focus(path, true); path.Text = "Comment";
        Assert.Single(view.ReadSpecs());
        Key(path, System.Windows.Input.Key.Enter);
        Assert.Equal(new[] { "Comment", "" }, view.ReadSpecs().Select(x => x.Input));
        Assert.True(view.HasInputFocus);
        Key(path, System.Windows.Input.Key.Enter); Focus(path, false);
        Assert.Equal(2, view.ReadSpecs().Count);
        var tail = Children<TextBox>(view).Skip(2).First(); tail.Text = "Area";
        Key(tail, System.Windows.Input.Key.Escape);
        Assert.Equal(new[] { "Comment", "" }, view.ReadSpecs().Select(x => x.Input));
    });

    /// <summary>右键移除的是所在行；菜单失焦和移除均不提交未提交值，旧快照也不能恢复行。</summary>
    [Fact]
    public void ContextRemovalDiscardsDraftAndKeepsTrailingPlaceholder() => Sta(() =>
    {
        var (view, path, value) = Comment(); var old = Snapshot(view, "原备注");
        int writes = 0; view.EditRequested += _ => writes++;
        Focus(value, true); value.Text = "不应写入";
        var menu = value.ContextMenu;
        menu.RaiseEvent(new RoutedEventArgs(ContextMenu.OpenedEvent)); Focus(value, false);
        Assert.Equal(0, writes); Assert.Equal("不应写入", value.Text);
        Assert.Contains(menu.Items.OfType<MenuItem>(), x => x.Command == ApplicationCommands.Copy);
        InvokeRemoval(menu.Items.OfType<MenuItem>().Single(x => Equals(x.Header, "移除此属性行")));
        view.Show(old); Key(value, System.Windows.Input.Key.Enter);
        Assert.Equal(0, writes); Assert.Single(view.ReadSpecs()); Assert.Equal("", view.ReadSpecs()[0].Input);
        var tail = Children<TextBox>(view).First();
        tail.ContextMenu.RaiseEvent(new RoutedEventArgs(ContextMenu.OpenedEvent));
        Assert.False(tail.ContextMenu.Items.OfType<MenuItem>().Last().IsEnabled);
    });

    /// <summary>右键不同于当前活动行时仍精确移除右键所在行；剩余属性顺序保持。</summary>
    [Fact]
    public void ContextMenuTargetsItsOwnRowRatherThanPreviouslyActiveRow() => Sta(() =>
    {
        var (view, first, _) = Comment();
        var second = Children<TextBox>(view).Skip(2).First(); second.Text = "Area"; Focus(second, false);
        Focus(first, true);
        var menu = second.ContextMenu; menu.RaiseEvent(new RoutedEventArgs(ContextMenu.OpenedEvent));
        InvokeRemoval(menu.Items.OfType<MenuItem>().Last());
        Assert.Equal(new[] { "Comment", "" }, view.ReadSpecs().Select(x => x.Input));
    });

    /// <summary>底部移除按钮在鼠标造成失焦前撤销票据，保留至少一个空行。</summary>
    [Fact]
    public void BottomRemovalCancelsValueBeforeMouseFocusLoss() => Sta(() =>
    {
        var (view, _, value) = Comment(); int writes = 0; view.EditRequested += _ => writes++;
        Focus(value, true); value.Text = "草稿";
        var remove = Children<System.Windows.Controls.Button>(view).Single(x => Equals(x.Content, "删除属性"));
        remove.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
            { RoutedEvent = Mouse.PreviewMouseDownEvent });
        Focus(value, false); Click(view, "删除属性");
        Assert.Equal(0, writes); Assert.Single(view.ReadSpecs());
    });

    /// <summary>检索只排队按钮事件，禁用时不触发；刷新立即收回许可并保留原因提示。</summary>
    [Fact]
    public void CatalogButtonRequiresCurrentSinglePlantSnapshot() => Sta(() =>
    {
        var view = new FastPropertiesView(); int count = 0; view.CatalogRequested += () => count++;
        var button = Children<System.Windows.Controls.Button>(view).Single(x => Equals(x.Content, "检索属性"));
        Assert.False(button.IsEnabled); Click(view, "检索属性"); Assert.Equal(0, count);
        Assert.True(ToolTipService.GetShowOnDisabled(button)); Assert.NotNull(button.ToolTip);
        view.SetCatalogAvailability(true, "单个 Plant 对象"); Click(view, "检索属性"); Assert.Equal(1, count);
        view.SuspendEditing(); Assert.False(button.IsEnabled);
    });

    /// <summary>打开和勾选不改属性行；取消不发出追加，已有属性预勾选且不允许取消。</summary>
    [Fact]
    public void CatalogCancelKeepsPanelRowsAndExistingChecks() => Sta(() =>
    {
        var (panel, _, _) = Comment(); var specs = panel.ReadSpecs().ToArray();
        var view = new PropertyCatalogView(
            [new("Area", "绘图区域"), new("Comment", "注释"), new("Size", "尺寸")],
            specs.Select(x => x.Input).ToArray(), panel.Resources);
        int added = 0, canceled = 0; view.AddRequested += _ => added++; view.CancelRequested += () => canceled++;
        var existing = view.Choices.Single(x => x.PropertyPath == "Comment");
        Assert.True(existing.IsChecked); Assert.False(existing.CanSelect);
        existing.IsChecked = false; Assert.True(existing.IsChecked);
        view.Choices.Single(x => x.PropertyPath == "Area").IsChecked = true;
        Assert.Equal(specs, panel.ReadSpecs());
        Children<System.Windows.Controls.Button>(view).Single(x => Equals(x.Content, "取消"))
            .RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
        Assert.Equal(0, added); Assert.Equal(1, canceled); Assert.Equal(specs, panel.ReadSpecs());
    });

    /// <summary>确认只追加勾选缺失项，已有完整路径与简写去重，新增值等待读取且不写入对象。</summary>
    [Fact]
    public void CatalogConfirmAppendsOnlySelectedMissingPaths() => Sta(() =>
    {
        var (panel, _, _) = Comment(); int writes = 0; panel.EditRequested += _ => writes++;
        var view = new PropertyCatalogView([new("Area", "绘图区域"), new("Comment", "注释"), new("Size", "尺寸")],
            ["TargetObject.Comment"], panel.Resources);
        int count = 0; view.AddRequested += selected => count = panel.AppendCatalogProperties(selected);
        view.Choices.Single(x => x.PropertyPath == "Area").IsChecked = true;
        Children<System.Windows.Controls.Button>(view).Single(x => Equals(x.Content, "添加所选属性"))
            .RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
        Assert.Equal(1, count); Assert.Equal(new[] { "Comment", "Area", "" }, panel.ReadSpecs().Select(x => x.Input));
        Assert.Equal(0, panel.AppendCatalogProperties(["TargetObject.Area", "COMMENT"]));
        Assert.Equal(0, writes);
        Assert.All(Children<TextBox>(panel).Where((_, i) => i % 2 == 1), x => Assert.True(x.IsReadOnly));
    });

    /// <summary>实际复选框双向绑定有效，两列文本只读、可选择复制，长名称及读取失败保留提示。</summary>
    [Fact]
    public void CatalogGridCheckboxesBindAndTextColumnsStayReadOnly() => Sta(() =>
    {
        var panel = new FastPropertiesView();
        var view = new PropertyCatalogView([new("Area", "绘图区域"), new("Size", "Size", "显示名称读取失败：模拟 SDK")],
            [], panel.Resources) { Width = 640, Height = 380 };
        view.Measure(new System.Windows.Size(640, 380)); view.Arrange(new Rect(0, 0, 640, 380)); view.UpdateLayout();
        // DataGrid 的星号列宽由布局调度计算；等待完成后检查，不能只看首次 Measure。
        view.Dispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle); view.UpdateLayout();
        var grid = Children<System.Windows.Controls.DataGrid>(view).Single();
        Assert.Equal(3, grid.Columns.Count);
        Assert.All(grid.Columns.OfType<DataGridTextColumn>(), column => Assert.True(column.ActualWidth >= 160));
        Assert.All(grid.Columns.OfType<DataGridTextColumn>(), column => Assert.True(column.IsReadOnly));
        Assert.Equal(DataGridClipboardCopyMode.ExcludeHeader, grid.ClipboardCopyMode);
        var check = Visuals<System.Windows.Controls.CheckBox>(view).First(x => x.DataContext == view.Choices[0]);
        check.SetCurrentValue(System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty, true);
        Assert.True(view.Choices[0].IsChecked);
        Assert.Contains("模拟 SDK", view.Choices[1].Detail);
        Assert.True(Children<System.Windows.Controls.Button>(view).Single(x => Equals(x.Content, "添加所选属性")).IsEnabled);
    });

    /// <summary>只打开复制菜单再返回输入时，不把尚未提交的草稿重置为旧原值。</summary>
    [Fact]
    public void ReturningFromContextMenuKeepsDraftUntilEnter() => Sta(() =>
    {
        var (view, _, value) = Comment(); var writes = new List<PnEditRequest>(); view.EditRequested += writes.Add;
        Focus(value, true); value.Text = "菜单前草稿";
        value.ContextMenu.RaiseEvent(new RoutedEventArgs(ContextMenu.OpenedEvent)); Focus(value, false);
        value.ContextMenu.RaiseEvent(new RoutedEventArgs(ContextMenu.ClosedEvent)); Focus(value, true);
        Assert.Equal("菜单前草稿", value.Text); Assert.Empty(writes);
        Key(value, System.Windows.Input.Key.Enter); Assert.Single(writes);
        Assert.Equal("菜单前草稿", writes[0].Value);
    });

    /// <summary>删除按钮鼠标按下但未点击，不写草稿，也不使所有值永久停在等待读取。</summary>
    [Fact]
    public void CanceledRemovalMouseDownDoesNotInvalidateAllRows() => Sta(() =>
    {
        var (view, _, value) = Comment(); int writes = 0, changed = 0;
        view.EditRequested += _ => writes++; view.RowsChanging += () => changed++;
        Focus(value, true); value.Text = "撤销的删除草稿";
        var remove = Children<System.Windows.Controls.Button>(view).Single(x => Equals(x.Content, "删除属性"));
        remove.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
            { RoutedEvent = Mouse.PreviewMouseDownEvent });
        Focus(value, false);
        Assert.Equal(0, writes); Assert.Equal(0, changed); Assert.False(value.IsReadOnly);
        Assert.Equal("撤销的删除草稿", value.Text); Assert.Equal(2, view.ReadSpecs().Count);
    });

    /// <summary>Ctrl+D 绑定当前值格与菜单同一命令，移除未提交草稿不写对象，也不留下旧行。</summary>
    [Fact]
    public void CtrlDRowCommandDiscardsDraftWithoutWriting() => Sta(() =>
    {
        var (view, _, value) = Comment(); int writes = 0; view.EditRequested += _ => writes++;
        Focus(value, true); value.Text = "快捷键移除草稿";
        var binding = value.InputBindings.OfType<KeyBinding>().Single(x => x.Key == System.Windows.Input.Key.D);
        Assert.Equal(ModifierKeys.Control, binding.Modifiers); Assert.Same(value, binding.CommandTarget);
        var item = value.ContextMenu.Items.OfType<MenuItem>().Last();
        Assert.Same(binding.Command, item.Command); Assert.Equal("Ctrl+D", item.InputGestureText);
        Assert.True(((RoutedCommand)binding.Command).CanExecute(null, value));
        ((RoutedCommand)binding.Command).Execute(null, value);
        Focus(value, false); Assert.Equal(0, writes); Assert.Single(view.ReadSpecs());
        Assert.False(((RoutedCommand)binding.Command).CanExecute(null, value));
    });

    /// <summary>路径格快捷键绑定自身行，即使先前活动行不同也不误移除。</summary>
    [Fact]
    public void CtrlDPathCommandTargetsItsOwnRow() => Sta(() =>
    {
        var (view, first, _) = Comment();
        var second = Children<TextBox>(view).Skip(2).First(); second.Text = "Area"; Focus(second, false);
        Focus(first, true);
        var binding = second.InputBindings.OfType<KeyBinding>().Single();
        ((RoutedCommand)binding.Command).Execute(null, second);
        Assert.Equal(new[] { "Comment", "" }, view.ReadSpecs().Select(x => x.Input));
        Assert.Empty(view.InputBindings); // 只在行内注册，面板空白处及 CAD 不接管 Ctrl+D。
    });

    /// <summary>末尾占位格拒绝执行快捷键，强制路由也不能删除最后空行。</summary>
    [Fact]
    public void CtrlDPlaceholderCannotBeRemoved() => Sta(() =>
    {
        var view = new FastPropertiesView(); var tail = Children<TextBox>(view).First();
        var command = (RoutedCommand)tail.InputBindings.OfType<KeyBinding>().Single().Command;
        Assert.False(command.CanExecute(null, tail)); command.Execute(null, tail);
        Assert.Single(view.ReadSpecs()); Assert.Equal("", view.ReadSpecs()[0].Input);
    });

    /// <summary>右键按下但未弹菜单仍释放焦点、保留草稿，回到原格可正常回车提交。</summary>
    [Fact]
    public void RightPressWithoutOpeningMenuDoesNotBlockCommitOrFocusRelease() => Sta(() =>
    {
        var (view, _, value) = Comment(); var edits = new List<PnEditRequest>(); view.EditRequested += edits.Add;
        Focus(value, true); value.Text = "右键前草稿";
        value.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Right)
            { RoutedEvent = Mouse.PreviewMouseDownEvent });
        Assert.False(value.ContextMenu.IsOpen);
        Focus(value, false); Assert.False(view.HasInputFocus);
        Assert.Empty(edits); Assert.Equal("右键前草稿", value.Text);
        Focus(value, true);
        Key(value, System.Windows.Input.Key.Enter); Assert.Single(edits);
        Assert.Equal("右键前草稿", edits[0].Value);
    });

    /// <summary>ContextMenuOpening 被宿主取消时，Dispatcher 清理临时状态，不等不存在的 Closed。</summary>
    [Fact]
    public void CanceledMenuOpeningResumesDeferredBlurAndReleasesFocus() => Sta(() =>
    {
        var (view, _, value) = Comment(); var edits = new List<PnEditRequest>(); view.EditRequested += edits.Add;
        Focus(value, true); value.Text = "取消菜单草稿";
        value.ContextMenuOpening += (_, e) => e.Handled = true;
        var opening = (ContextMenuEventArgs)Activator.CreateInstance(typeof(ContextMenuEventArgs),
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic, null,
            [value, true], null)!;
        value.RaiseEvent(opening); Assert.True(opening.Handled);
        Focus(value, false); Assert.Empty(edits);
        PumpMenuCleanup();
        Assert.False(view.HasInputFocus); Assert.Empty(edits); Assert.Equal("取消菜单草稿", value.Text);
        Focus(value, true); Key(value, System.Windows.Input.Key.Enter);
        Assert.Single(edits); Assert.Equal("取消菜单草稿", edits[0].Value);
    });

    /// <summary>正常菜单关闭并返回原格，延迟收尾不重置草稿或提前提交。</summary>
    [Fact]
    public void MenuCloseReturningToSameEditorKeepsDraftAcrossDispatcherCleanup() => Sta(() =>
    {
        var (view, _, value) = Comment(); var edits = new List<PnEditRequest>(); view.EditRequested += edits.Add;
        Focus(value, true); value.Text = "保留的草稿";
        value.ContextMenu.RaiseEvent(new RoutedEventArgs(ContextMenu.OpenedEvent)); Focus(value, false);
        value.ContextMenu.RaiseEvent(new RoutedEventArgs(ContextMenu.ClosedEvent)); Focus(value, true);
        PumpMenuCleanup(); Assert.Empty(edits); Assert.Equal("保留的草稿", value.Text); Assert.True(view.HasInputFocus);
        Key(value, System.Windows.Input.Key.Enter); Assert.Single(edits);
    });

    /// <summary>菜单结束后离开值格不提交，Dispatcher 重复收尾也不丢失草稿。</summary>
    [Fact]
    public void MenuCloseOutsideEditorKeepsDraftWithoutWriting() => Sta(() =>
    {
        var (view, _, value) = Comment(); var edits = new List<PnEditRequest>(); view.EditRequested += edits.Add;
        Focus(value, true); value.Text = "离开菜单的草稿";
        value.ContextMenu.RaiseEvent(new RoutedEventArgs(ContextMenu.OpenedEvent)); Focus(value, false);
        value.ContextMenu.RaiseEvent(new RoutedEventArgs(ContextMenu.ClosedEvent)); PumpMenuCleanup();
        Assert.Empty(edits); Assert.False(view.HasInputFocus); Assert.Equal("离开菜单的草稿", value.Text);
        PumpMenuCleanup(); Assert.Empty(edits);
        Focus(value, true); Key(value, System.Windows.Input.Key.Enter);
        Assert.Single(edits); Assert.Equal("离开菜单的草稿", edits[0].Value);
    });

    /// <summary>路径格的取消菜单也恢复提交和自动尾空行，不把全表停在 PendingPath。</summary>
    [Fact]
    public void CanceledPathMenuRestoresCommitAndTrailingPlaceholder() => Sta(() =>
    {
        var view = new FastPropertiesView(); var path = Children<TextBox>(view).First();
        Focus(path, true); path.Text = "Comment";
        var opening = (ContextMenuEventArgs)Activator.CreateInstance(typeof(ContextMenuEventArgs),
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic, null,
            [path, true], null)!;
        path.RaiseEvent(opening); Focus(path, false); PumpMenuCleanup();
        Assert.Equal(new[] { "Comment", "" }, view.ReadSpecs().Select(x => x.Input));
        Assert.False(view.HasInputFocus);
    });

    /// <summary>隐藏时清除菜单借用状态且不提交草稿，重新读取后可正常编辑。</summary>
    [Fact]
    public void HidingClearsMenuStateWithoutWritingRemovedOrStaleDraft() => Sta(() =>
    {
        var (view, _, value) = Comment(); var edits = new List<PnEditRequest>(); view.EditRequested += edits.Add;
        Focus(value, true); value.Text = "隐藏前未提交";
        value.ContextMenu.RaiseEvent(new RoutedEventArgs(ContextMenu.OpenedEvent)); Focus(value, false);
        view.SuspendEditing(); view.ClearInputFocus(); PumpMenuCleanup();
        Assert.Empty(edits); Assert.False(view.HasInputFocus); Assert.True(value.IsReadOnly);
        ShowComment(view, "原备注"); Focus(value, true); value.Text = "重新打开"; Key(value, System.Windows.Input.Key.Enter);
        Assert.Single(edits); Assert.Equal("重新打开", edits[0].Value);
    });

    private static (FastPropertiesView View, TextBox Value) MixedComment()
    {
        var (view, _, value) = Comment("一层");
        var template = Snapshot(view, "一层");
        var objects = new[]
        {
            new PnObjectProperties("Valve", "手动阀", template.Cells, "PnId:1:Valve"),
            new PnObjectProperties("FlowMeter", "流量计", template.Cells.Select(c => string.IsNullOrWhiteSpace(c.Spec.Input)
                ? c : c with { Value = FastPropertyValue.Value("二层") }).ToArray(), "PnId:2:FlowMeter")
        };
        var snapshot = PnPropertyPolicy.Merge(view.ReadSpecs(), objects);
        view.Show(snapshot with { Cells = snapshot.Cells.Select(c => c.Permission.Allowed
            ? c with { EditToken = Guid.NewGuid() } : c).ToArray() });
        return (view, value);
    }

    /// <summary>混合值使用独立显示提示，未修改的 Enter、失焦和 Esc 不提交初始化空缓冲。</summary>
    [Fact]
    public void MixedValueFocusAndUnchangedEnterNeverWriteEmptyOrPlaceholder() => Sta(() =>
    {
        var (view, value) = MixedComment(); int writes = 0; view.EditRequested += _ => writes++;
        Assert.False(value.IsReadOnly); Assert.Equal("*(多种)", value.Text);
        Focus(value, true); Assert.Equal("", value.Text);
        Assert.Contains(Children<TextBlock>(view), x => x.Text == "*(多种)" && x.Visibility == Visibility.Visible);
        Key(value, System.Windows.Input.Key.Enter); Focus(value, false);
        Assert.Equal(0, writes); Assert.Equal("*(多种)", value.Text);
        Focus(value, true); value.Text = "撤销的多选输入"; Key(value, System.Windows.Input.Key.Escape);
        Key(value, System.Windows.Input.Key.Enter); Focus(value, false); Assert.Equal(0, writes);
    });

    /// <summary>用户实际输入的文字是新值，即使恰好等于多种值提示也能作为真实文字提交。</summary>
    [Theory]
    [InlineData("统一安装位置")]
    [InlineData("*(多种)")]
    public void MixedValueActualInputSubmitsOnce(string newValue) => Sta(() =>
    {
        var (view, value) = MixedComment(); var edits = new List<PnEditRequest>(); view.EditRequested += edits.Add;
        Focus(value, true); value.Text = newValue; Key(value, System.Windows.Input.Key.Enter); Focus(value, false);
        Assert.Single(edits); Assert.Equal(newValue, edits[0].Value);
    });

    /// <summary>混合值明确删除可跨越失焦保留空草稿，只有回车才提交清空请求。</summary>
    [Theory]
    [InlineData(System.Windows.Input.Key.Delete)]
    [InlineData(System.Windows.Input.Key.Back)]
    public void MixedValueExplicitDeleteSubmitsAnEmptyValue(System.Windows.Input.Key key) => Sta(() =>
    {
        var (view, value) = MixedComment(); var edits = new List<PnEditRequest>(); view.EditRequested += edits.Add;
        Focus(value, true); Key(value, key); Focus(value, false);
        Assert.Empty(edits); Assert.Equal("", value.Text); Assert.Equal(1, value.Foreground.Opacity);
        Focus(value, true); Key(value, System.Windows.Input.Key.Enter);
        Assert.Single(edits); Assert.Equal("", edits[0].Value);
    });

    /// <summary>混合提示只改变前景透明度，主题、只读、复制及光标选区都保持独立。</summary>
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void MixedPlaceholderUsesHalfOpaqueTextOnly(bool dark, bool readOnly) => Sta(() =>
    {
        var (view, value) = MixedComment();
        if (readOnly)
        {
            var specs = view.ReadSpecs();
            view.Show(new("多选", 2, specs.Select(s => new PnPropertyCell(s,
                new(FastValueKind.Mixed), PnEditPermission.ReadOnly("图层锁定"))).ToArray(), "只读"));
        }
        foreach (bool theme in new[] { dark, !dark, dark })
        {
            view.SetTheme(theme);
            Assert.Equal("*(多种)", value.Text); Assert.Equal(0.5, value.Foreground.Opacity);
            Assert.Equal(1, value.Opacity); Assert.Equal(1, value.Background.Opacity);
            Assert.Equal(1, value.CaretBrush.Opacity); Assert.Equal(1, value.SelectionBrush.Opacity);
            string key = readOnly ? "PanelReadOnlyForeground" : "PanelForeground";
            Assert.Equal(((SolidColorBrush)view.Resources[key]).Color, ((SolidColorBrush)value.Foreground).Color);
        }
        value.SelectAll(); Assert.Equal("*(多种)", value.SelectedText);
        Assert.True(value.IsEnabled); Assert.NotNull(value.ToolTip);
        Focus(value, true);
        if (!readOnly)
        {
            Assert.Equal("", value.Text);
            var hint = Children<TextBlock>(view).Single(x => x.Text == "*(多种)" && x.Visibility == Visibility.Visible);
            Assert.Equal(0.5, hint.Opacity); Assert.Equal(1, hint.Foreground.Opacity);
        }
        Focus(value, false); Assert.Equal("*(多种)", value.Text);
    });

    /// <summary>真实输入即使和提示同文也保持全不透明，失焦及主题变化不会改回占位文字。</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LiteralMixedTextDraftStaysOpaqueAndCanBeSubmitted(bool dark) => Sta(() =>
    {
        var (view, value) = MixedComment(); var edits = new List<PnEditRequest>(); view.EditRequested += edits.Add;
        Focus(value, true); value.Text = "*(多种)"; Focus(value, false); view.SetTheme(dark);
        Assert.Equal(1, value.Foreground.Opacity); Assert.Equal("*(多种)", value.Text); Assert.Empty(edits);
        Assert.DoesNotContain(Children<TextBlock>(view), x => x.Text == "*(多种)" && x.Visibility == Visibility.Visible);
        Focus(value, true); Key(value, System.Windows.Input.Key.Enter);
        Assert.Single(edits); Assert.Equal("*(多种)", edits[0].Value);
    });

    /// <summary>Esc 恢复结构化原值；空值用半透明提示，普通及真实同文值用正常前景。</summary>
    [Theory]
    [InlineData("原备注")]
    [InlineData("")]
    [InlineData("*(多种)")]
    public void EscapeDiscardsBlurredDraftAndRestoresOpaqueOriginal(string original) => Sta(() =>
    {
        var (view, _, value) = Comment(original); int writes = 0; view.EditRequested += _ => writes++;
        Assert.Equal(original.Length == 0 ? 0.5 : 1, value.Foreground.Opacity);
        Focus(value, true); value.Text = "等待提交"; Focus(value, false);
        Focus(value, true); Key(value, System.Windows.Input.Key.Escape);
        Assert.True(view.HasInputFocus); Assert.Equal(original, value.Text);
        Assert.Equal(original.Length == 0 ? 0.5 : 1, value.Foreground.Opacity);
        Key(value, System.Windows.Input.Key.Enter); Focus(value, false);
        Assert.Equal(0, writes); Assert.Equal(original.Length == 0 ? "(空白)" : original, value.Text);
    });

    /// <summary>同一票据的显示刷新保留草稿、光标选区及提交身份，不能以重复快照覆盖输入。</summary>
    [Fact]
    public void SameTicketRepaintKeepsDraftAndSelection() => Sta(() =>
    {
        var (view, _, value) = Comment(); var snapshot = Snapshot(view, "原备注"); view.Show(snapshot);
        var edits = new List<PnEditRequest>(); view.EditRequested += edits.Add;
        Focus(value, true); value.Text = "保留的草稿"; value.Select(1, 2);
        view.Show(snapshot with { Status = "显示刷新" });
        Assert.Equal("保留的草稿", value.Text); Assert.Equal(1, value.SelectionStart); Assert.Equal(2, value.SelectionLength);
        Focus(value, false); view.Show(snapshot); Assert.Equal("保留的草稿", value.Text);
        Focus(value, true); Key(value, System.Windows.Input.Key.Enter);
        Assert.Single(edits); Assert.Equal(snapshot.Cells[0].EditToken, edits[0].Token);
    });

    /// <summary>票据、原值、权限或有效性变化都废弃草稿，不能在后续 Enter 里写入旧输入。</summary>
    [Theory]
    [InlineData("ticket")]
    [InlineData("original")]
    [InlineData("permission")]
    [InlineData("emptyToken")]
    [InlineData("suspend")]
    [InlineData("hide")]
    public void InvalidatedDraftCannotLeakIntoAnotherContext(string change) => Sta(() =>
    {
        var (view, _, value) = Comment(); var snapshot = Snapshot(view, "原备注"); view.Show(snapshot);
        int writes = 0; view.EditRequested += _ => writes++;
        Focus(value, true); value.Text = "旧对象草稿"; Focus(value, false);
        var cell = snapshot.Cells[0];
        if (change == "suspend") view.SuspendEditing();
        else if (change == "hide") view.ClearInputFocus();
        else
        {
            cell = change switch
            {
                "ticket" => cell with { EditToken = Guid.NewGuid() },
                "original" => cell with { Value = FastPropertyValue.Value("外部更新") },
                "permission" => cell with { Permission = PnEditPermission.ReadOnly("权限已撤销") },
                _ => cell with { EditToken = Guid.Empty }
            };
            view.Show(snapshot with { Cells = snapshot.Cells.Select((c, i) => i == 0 ? cell : c).ToArray() });
        }
        Assert.NotEqual("旧对象草稿", value.Text);
        Focus(value, true); Key(value, System.Windows.Input.Key.Enter); Assert.Equal(0, writes);
        ShowComment(view, "新读取值"); Key(value, System.Windows.Input.Key.Enter); Assert.Equal(0, writes);
    });

    /// <summary>Tab 及 Shift+Tab 的焦点移动只保留草稿，进入其他行也不会间接写入。</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TabFocusTraversalDoesNotSubmit(bool backwards) => Sta(() =>
    {
        var (view, path, value) = Comment(); int writes = 0; view.EditRequested += _ => writes++;
        Focus(value, true); value.Text = "切换焦点草稿";
        Key(value, System.Windows.Input.Key.Tab);
        var target = backwards ? path : Children<TextBox>(view).Skip(2).First();
        value.RaiseEvent(new KeyboardFocusChangedEventArgs(Keyboard.PrimaryDevice, 0, value, target)
            { RoutedEvent = Keyboard.LostKeyboardFocusEvent });
        Focus(target, true); PumpMenuCleanup();
        Assert.Equal(0, writes); Assert.Equal("切换焦点草稿", value.Text);
        Focus(target, false); Focus(value, true); Key(value, System.Windows.Input.Key.Enter);
        Assert.Equal(1, writes);
    });

    /// <summary>多行草稿独立保存；某行提交刷新全局票据后，其他草稿必须废弃。</summary>
    [Fact]
    public void SubmittingOneRowDiscardsOtherDraftsWithGlobalTicketInvalidation() => Sta(() =>
    {
        var view = new FastPropertiesView(); view.AppendCatalogProperties(["Comment", "Area"]);
        var snapshot = new PnPanelSnapshot("阀门", 1, view.ReadSpecs().Select(s => new PnPropertyCell(s,
            FastPropertyValue.Value(s.Input + "原值"), PnEditPermission.Writable, Guid.NewGuid())).ToArray(), "可编辑");
        view.Show(snapshot); var boxes = Children<TextBox>(view).ToArray();
        var edits = new List<PnEditRequest>(); view.EditRequested += edits.Add;
        Focus(boxes[1], true); boxes[1].Text = "首行草稿"; Focus(boxes[1], false);
        Focus(boxes[3], true); boxes[3].Text = "次行草稿";
        Assert.Equal("首行草稿", boxes[1].Text); Key(boxes[3], System.Windows.Input.Key.Enter);
        Assert.Single(edits); Assert.Equal("次行草稿", edits[0].Value);
        Assert.Equal("Comment原值", boxes[1].Text); Assert.True(boxes[1].IsReadOnly);
        view.Show(snapshot with { Cells = snapshot.Cells.Select(c => c with { EditToken = Guid.NewGuid() }).ToArray() });
        Focus(boxes[3], false); Focus(boxes[1], true); Key(boxes[1], System.Windows.Input.Key.Enter);
        Assert.Single(edits);
    });

    /// <summary>暂停期间撤销草稿但仍灰显半透明混合提示；重复暂停不改变文字含义。</summary>
    [Fact]
    public void SuspendingMixedDraftRestoresHalfOpaquePlaceholder() => Sta(() =>
    {
        var (view, value) = MixedComment();
        Focus(value, true); value.Text = "未提交"; Focus(value, false);
        view.SuspendEditing(); view.SuspendEditing();
        Assert.True(value.IsReadOnly); Assert.Equal("*(多种)", value.Text); Assert.Equal(0.5, value.Foreground.Opacity);
    });

    /// <summary>空字符串及真实 null 使用独立提示，深浅与只读状态只改变文字前景。</summary>
    [Theory]
    [InlineData("", "(空白)", false, false)]
    [InlineData("", "(空白)", false, true)]
    [InlineData("", "(空白)", true, false)]
    [InlineData("", "(空白)", true, true)]
    [InlineData(null, "(null)", false, false)]
    [InlineData(null, "(null)", false, true)]
    [InlineData(null, "(null)", true, false)]
    [InlineData(null, "(null)", true, true)]
    public void EmptyAndNullUseHalfOpaquePlaceholders(string? raw, string placeholder, bool dark, bool readOnly) => Sta(() =>
    {
        var (view, _, value) = Comment(raw); int writes = 0; view.EditRequested += _ => writes++;
        if (readOnly)
            view.Show(new("阀门", 1, [new(view.ReadSpecs()[0], FastPropertyValue.Value(raw),
                PnEditPermission.ReadOnly("图层锁定"))], "只读"));
        view.SetTheme(dark);
        Assert.Equal(placeholder, value.Text); Assert.Equal(0.5, value.Foreground.Opacity);
        Assert.Equal(1, value.Opacity); Assert.Equal(1, value.Background.Opacity);
        Assert.Equal(1, value.CaretBrush.Opacity); Assert.Equal(1, value.SelectionBrush.Opacity);
        value.SelectAll(); Assert.Equal(placeholder, value.SelectedText); Assert.NotNull(value.ToolTip);
        Focus(value, true); Key(value, System.Windows.Input.Key.Enter); Assert.Equal(0, writes);
        if (!readOnly)
        {
            Assert.Equal("", value.Text);
            var hint = Children<TextBlock>(view).Single(t => t.Text == placeholder && t.Visibility == Visibility.Visible);
            Assert.Equal(0.5, hint.Opacity);
            value.Text = placeholder; Assert.Equal(1, value.Foreground.Opacity);
            Focus(value, false); Assert.Equal(0, writes); Assert.Equal(placeholder, value.Text);
            Focus(value, true); Key(value, System.Windows.Input.Key.Escape);
            Key(value, System.Windows.Input.Key.Enter); Assert.Equal(0, writes);
        }
        Focus(value, false); Assert.Equal(placeholder, value.Text); Assert.Equal(0.5, value.Foreground.Opacity);
        view.SetTheme(!dark); Assert.Equal(0.5, value.Foreground.Opacity);
    });

    /// <summary>真实提示同文、null 文本、空格和制表符都按普通值显示，不修剪或虚构占位。</summary>
    [Theory]
    [InlineData("*(多种)")]
    [InlineData("(空白)")]
    [InlineData("(null)")]
    [InlineData("null")]
    [InlineData(" ")]
    [InlineData("\t")]
    public void LiteralPlaceholderTextAndWhitespaceStayOpaque(string raw) => Sta(() =>
    {
        var (view, _, value) = Comment(raw); int writes = 0; view.EditRequested += _ => writes++;
        foreach (bool dark in new[] { true, false })
        {
            view.SetTheme(dark); Assert.Equal(raw, value.Text); Assert.Equal(1, value.Foreground.Opacity);
            Focus(value, true); Key(value, System.Windows.Input.Key.Enter); Focus(value, false);
            Assert.Equal(raw, value.Text); Assert.Equal(0, writes);
        }
    });

    /// <summary>明确清空 null 不与未修改混淆，失焦后等 Enter，并保留真实 null 原值。</summary>
    [Fact]
    public void ExplicitNullClearKeepsNullOriginalAndSubmitsOnlyOnEnter() => Sta(() =>
    {
        var (view, _, value) = Comment(null); var edits = new List<PnEditRequest>(); view.EditRequested += edits.Add;
        Focus(value, true); Key(value, System.Windows.Input.Key.Enter); Assert.Empty(edits);
        Key(value, System.Windows.Input.Key.Delete); Focus(value, false);
        Assert.Empty(edits); Assert.Equal("", value.Text); Assert.Equal(1, value.Foreground.Opacity);
        Focus(value, true); Key(value, System.Windows.Input.Key.Enter);
        Assert.Single(edits); Assert.Null(edits[0].Original); Assert.Equal("", edits[0].Value);
    });

    /// <summary>即使票据相同，null 和空字符串之间的原值变化也必须撤销草稿。</summary>
    [Theory]
    [InlineData(null, "")]
    [InlineData("", null)]
    public void NullEmptyOriginalTransitionInvalidatesDraft(string? before, string? after) => Sta(() =>
    {
        var (view, _, value) = Comment(before); var snapshot = Snapshot(view, before); view.Show(snapshot);
        int writes = 0; view.EditRequested += _ => writes++;
        Focus(value, true); value.Text = "旧草稿"; Focus(value, false);
        view.Show(snapshot with { Cells = snapshot.Cells.Select((c, i) => i == 0
            ? c with { Value = FastPropertyValue.Value(after) } : c).ToArray() });
        Assert.Equal(after == null ? "(null)" : "(空白)", value.Text);
        Focus(value, true); Key(value, System.Windows.Input.Key.Enter); Assert.Equal(0, writes);
    });

    /// <summary>草稿用主题背景及 Tooltip 表达，实际输入无后缀；失焦保持，撤销／提交清除。</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DraftStatusUsesBackgroundAndTooltipWithoutChangingValue(bool dark) => Sta(() =>
    {
        var (view, _, value) = Comment(); var edits = new List<PnEditRequest>(); view.EditRequested += edits.Add;
        view.SetTheme(dark); Focus(value, true); value.Text = "新备注"; Focus(value, false);
        Assert.Equal("新备注", value.Text); Assert.Empty(edits); Assert.Equal(1, value.Foreground.Opacity);
        Assert.Equal(((SolidColorBrush)view.Resources["PanelDraftBackground"]).Color, ((SolidColorBrush)value.Background).Color);
        Assert.Contains("尚未提交", ((TextBlock)((ToolTip)value.ToolTip).Content).Text);
        view.SetTheme(!dark);
        Assert.Equal(((SolidColorBrush)view.Resources["PanelDraftBackground"]).Color, ((SolidColorBrush)value.Background).Color);
        Focus(value, true); Key(value, System.Windows.Input.Key.Escape);
        Assert.Equal("原备注", value.Text);
        Assert.Equal(((SolidColorBrush)view.Resources["PanelSurface"]).Color, ((SolidColorBrush)value.Background).Color);
        Assert.DoesNotContain("尚未提交", ((TextBlock)((ToolTip)value.ToolTip).Content).Text);
        value.Text = "临时"; value.Text = "原备注"; Key(value, System.Windows.Input.Key.Enter);
        Assert.Empty(edits);
        Assert.Equal(((SolidColorBrush)view.Resources["PanelSurface"]).Color, ((SolidColorBrush)value.Background).Color);
        value.Text = "实际提交值"; Key(value, System.Windows.Input.Key.Enter);
        Assert.Single(edits); Assert.Equal("实际提交值", edits[0].Value);
        Assert.DoesNotContain("尚未提交", value.Text);
        Assert.DoesNotContain("尚未提交", ((TextBlock)((ToolTip)value.ToolTip).Content).Text);
    });

    /// <summary>值菜单和 Ctrl+U 指向同一本地命令，属性路径列及面板全局不绑定。</summary>
    [Fact]
    public void UnifyMenuAndKeyboardShareOnlyTheValueCellCommand() => Sta(() =>
    {
        var (view, value) = MixedComment(); var path = Children<TextBox>(view).First();
        var binding = value.InputBindings.OfType<KeyBinding>().Single(x => x.Key == System.Windows.Input.Key.U);
        var item = value.ContextMenu.Items.OfType<MenuItem>().Single(x => Equals(x.Header, "统一键值"));
        Assert.Equal(ModifierKeys.Control, binding.Modifiers); Assert.Same(binding.Command, item.Command);
        Assert.Same(value, item.CommandTarget); Assert.Equal("Ctrl+U", item.InputGestureText);
        Assert.DoesNotContain(path.InputBindings.OfType<KeyBinding>(), x => x.Key == System.Windows.Input.Key.U);
        Assert.DoesNotContain(path.ContextMenu.Items.OfType<MenuItem>(), x => Equals(x.Header, "统一键值"));
        Assert.Empty(view.InputBindings); Assert.True(((RoutedCommand)binding.Command).CanExecute(null, value));
        var requests = new List<PnUnifiedValueRequest>(); view.UnifyValueRequested += requests.Add;
        ((RoutedCommand)binding.Command).Execute(null, value); ((RoutedCommand)item.Command).Execute(null, value);
        PumpMenuCleanup(); Assert.Single(requests); Assert.Null(requests[0].Draft);
        Assert.False(((RoutedCommand)binding.Command).CanExecute(null, value));
        view.CompleteUnifiedRequest(requests[0], false);
        Assert.True(((RoutedCommand)binding.Command).CanExecute(null, value));
    });

    /// <summary>非混合、单选、失效、只读及读取中一律拒绝，禁用仍有原因提示。</summary>
    [Theory]
    [InlineData("single")]
    [InlineData("common")]
    [InlineData("literal")]
    [InlineData("readonly")]
    [InlineData("ticket")]
    [InlineData("missing")]
    [InlineData("reading")]
    [InlineData("path")]
    [InlineData("suspend")]
    public void UnifyRejectsEveryIneligibleCell(string state) => Sta(() =>
    {
        var (view, value) = MixedComment(); var spec = view.ReadSpecs()[0];
        var data = state switch
        {
            "common" => FastPropertyValue.Value("共同值"), "literal" => FastPropertyValue.Value("*(多种)"),
            "missing" => FastPropertyValue.AbsentProperty(), "reading" => FastPropertyValue.Missing(),
            _ => new FastPropertyValue(FastValueKind.Mixed)
        };
        view.Show(new("对象", state == "single" ? 1 : 2, [new(spec, data,
            state == "readonly" ? PnEditPermission.ReadOnly("图层锁定") : PnEditPermission.Writable,
            state == "ticket" ? Guid.Empty : Guid.NewGuid())], "测试"));
        if (state == "path") Children<TextBox>(view).First().Text = "Area";
        if (state == "suspend") view.SuspendEditing();
        var item = value.ContextMenu.Items.OfType<MenuItem>().Single(x => Equals(x.Header, "统一键值"));
        value.ContextMenu.RaiseEvent(new RoutedEventArgs(ContextMenu.OpenedEvent));
        Assert.False(((RoutedCommand)item.Command).CanExecute(null, value));
        Assert.True(ToolTipService.GetShowOnDisabled(item)); Assert.NotEmpty(((TextBlock)((ToolTip)item.ToolTip).Content).Text);
        int requests = 0; view.UnifyValueRequested += _ => requests++;
        ((RoutedCommand)item.Command).Execute(null, value); PumpMenuCleanup(); Assert.Equal(0, requests);
    });

    /// <summary>禁用时 Ctrl+U 也被消费，不因快捷键泄漏执行 CAD 的其他功能。</summary>
    [Fact]
    public void DisabledUnifyShortcutIsConsumed() => Sta(() =>
    {
        var (view, _, value) = Comment();
        var key = new KeyEventArgs(Keyboard.PrimaryDevice, new TestSource(), 0, System.Windows.Input.Key.U)
            { RoutedEvent = Keyboard.PreviewKeyDownEvent };
        var handler = typeof(FastPropertiesView).GetMethod("HandleUnifyShortcut",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        Assert.True((bool)handler.Invoke(view, [key, value, ModifierKeys.Control])!); Assert.True(key.Handled);
        Assert.False((bool)handler.Invoke(view, [key, value, ModifierKeys.None])!);
    });

    /// <summary>确定仅回填，另一次值格 Enter 才提交；不发路径或刷新事件。</summary>
    [Fact]
    public void UnifiedConfirmationCreatesDraftWithoutSubmittingOrRefreshing() => Sta(() =>
    {
        var (view, value) = MixedComment(); var requests = new List<PnUnifiedValueRequest>(); var edits = new List<PnEditRequest>();
        view.UnifyValueRequested += requests.Add; view.EditRequested += edits.Add;
        int rowEvents = 0; view.RowsChanging += () => rowEvents++; view.RowsCommitted += () => rowEvents++;
        Focus(value, true); ExecuteUnify(value); PumpMenuCleanup(); var request = Assert.Single(requests);
        var dialog = new UnifiedValueView("Comment", ["A", "B"], request.Draft, view.Resources);
        var combo = Children<ComboBox>(dialog).Single(); combo.Text = "统一实际值";
        var key = new KeyEventArgs(Keyboard.PrimaryDevice, new TestSource(), 0, System.Windows.Input.Key.Enter)
            { RoutedEvent = Keyboard.PreviewKeyDownEvent };
        dialog.Confirmed += text => { Assert.True(key.Handled); Assert.True(view.TryApplyUnifiedDraft(request, text)); };
        dialog.RaiseEvent(key); Assert.True(key.Handled); PumpMenuCleanup();
        Assert.Empty(edits); Assert.Equal(0, rowEvents); Assert.Equal("统一实际值", value.Text);
        Assert.Contains("尚未提交", ((TextBlock)((ToolTip)value.ToolTip).Content).Text);
        Focus(value, true); Key(value, System.Windows.Input.Key.Enter); Key(value, System.Windows.Input.Key.Enter);
        Assert.Single(edits); Assert.Equal("统一实际值", edits[0].Value); Assert.Equal(request.Token, edits[0].Token);
    });

    /// <summary>无草稿、明确空草稿和真实文本草稿在请求和窗口初始化中不混淆。</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" 草稿 ")]
    public void UnifyInitialTextPreservesDraftState(string? draft) => Sta(() =>
    {
        var (view, value) = MixedComment(); Focus(value, true);
        if (draft == "") Key(value, System.Windows.Input.Key.Delete); else if (draft != null) value.Text = draft;
        var requests = new List<PnUnifiedValueRequest>(); view.UnifyValueRequested += requests.Add;
        ExecuteUnify(value); PumpMenuCleanup(); var request = Assert.Single(requests); Assert.Equal(draft, request.Draft);
        var dialog = new UnifiedValueView("Size", ["A", "B"], request.Draft, view.Resources);
        dialog.PrepareTemplate(); var combo = Children<ComboBox>(dialog).Single();
        Assert.True(combo.IsEditable); Assert.False(combo.IsTextSearchEnabled); Assert.Equal(-1, combo.SelectedIndex);
        Assert.Equal(draft ?? "", combo.Text);
        var editor = Assert.IsType<TextBox>(combo.Template.FindName("PART_EditableTextBox", combo));
        Assert.Equal(draft ?? "", editor.Text);
        editor.Text = " 手输新值 "; string? confirmed = null; dialog.Confirmed += text => confirmed = text;
        Children<System.Windows.Controls.Button>(dialog).Single(b => Equals(b.Content, "确定"))
            .RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
        Assert.Equal(" 手输新值 ", confirmed);
    });

    /// <summary>取消保持原票据和草稿，不主动刷新；确认空输入也只形成草稿。</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UnifyCancelOrEmptyConfirmationNeverWrites(bool acceptEmpty) => Sta(() =>
    {
        var (view, value) = MixedComment(); Focus(value, true); value.Text = "原草稿";
        var requests = new List<PnUnifiedValueRequest>(); view.UnifyValueRequested += requests.Add;
        var edits = new List<PnEditRequest>(); view.EditRequested += edits.Add;
        ExecuteUnify(value); PumpMenuCleanup(); var request = Assert.Single(requests);
        if (acceptEmpty) Assert.True(view.TryApplyUnifiedDraft(request, "")); else view.CompleteUnifiedRequest(request);
        PumpMenuCleanup(); Assert.Empty(edits); Assert.Equal(acceptEmpty ? "" : "原草稿", value.Text);
        Focus(value, true); Key(value, System.Windows.Input.Key.Enter);
        Assert.Single(edits); Assert.Equal(acceptEmpty ? "" : "原草稿", edits[0].Value);
    });

    /// <summary>旧弹窗请求不得覆盖新输入、Esc、换选、权限、路径、隐藏和行移除后的状态。</summary>
    [Theory]
    [InlineData("draft")]
    [InlineData("escape")]
    [InlineData("ticket")]
    [InlineData("readonly")]
    [InlineData("single")]
    [InlineData("path")]
    [InlineData("hide")]
    [InlineData("remove")]
    public void StaleUnifiedResultNeverOverwritesCurrentState(string change) => Sta(() =>
    {
        var (view, value) = MixedComment(); Focus(value, true);
        var requests = new List<PnUnifiedValueRequest>(); view.UnifyValueRequested += requests.Add;
        int writes = 0; view.EditRequested += _ => writes++;
        ExecuteUnify(value); PumpMenuCleanup(); var request = Assert.Single(requests);
        if (change == "draft") value.Text = "更晚输入";
        else if (change == "escape") Key(value, System.Windows.Input.Key.Escape);
        else if (change == "path") Children<TextBox>(view).First().Text = "Area";
        else if (change == "hide") view.ClearInputFocus();
        else if (change == "remove") InvokeRemoval(value.ContextMenu.Items.OfType<MenuItem>().Last());
        else view.Show(new("更新", change == "single" ? 1 : 2, [new(view.ReadSpecs()[0], new(FastValueKind.Mixed),
            change == "readonly" ? PnEditPermission.ReadOnly("锁层") : PnEditPermission.Writable,
            change == "ticket" ? Guid.NewGuid() : request.Token)], "更新"));
        string current = value.Text;
        Assert.False(view.TryApplyUnifiedDraft(request, "过期窗口的结果"));
        view.CompleteUnifiedRequest(request, false); PumpMenuCleanup();
        Assert.Equal(current, value.Text); Assert.Equal(0, writes);
        if (change == "draft") { Key(value, System.Windows.Input.Key.Enter); Assert.Equal(1, writes); }
    });

    /// <summary>相同票据的提示重绘不改变草稿修订，正常请求仍可回填。</summary>
    [Fact]
    public void EquivalentSnapshotRepaintDoesNotExpireUnifiedRequest() => Sta(() =>
    {
        var (view, value) = MixedComment(); var requests = new List<PnUnifiedValueRequest>(); view.UnifyValueRequested += requests.Add;
        ExecuteUnify(value); PumpMenuCleanup(); var request = Assert.Single(requests);
        view.Show(new("刷新显示", 2, [new(view.ReadSpecs()[0], new(FastValueKind.Mixed, Detail: "更新提示"),
            PnEditPermission.Writable, request.Token)], "只重绘"));
        Assert.True(view.IsUnifiedRequestCurrent(request)); Assert.True(view.TryApplyUnifiedDraft(request, "新草稿"));
        Assert.Equal("新草稿", value.Text);
    });

    /// <summary>右键行不同于活动行时必须固定右键所在的行、属性路径和票据。</summary>
    [Fact]
    public void UnifyTargetsRightClickedValueInsteadOfActiveRow() => Sta(() =>
    {
        var view = new FastPropertiesView(); view.AppendCatalogProperties(["Comment", "Size"]);
        var cells = view.ReadSpecs().Select(s => new PnPropertyCell(s, new(FastValueKind.Mixed),
            PnEditPermission.Writable, Guid.NewGuid())).ToArray(); view.Show(new("多选", 2, cells, "可编辑"));
        var boxes = Children<TextBox>(view).ToArray(); Focus(boxes[1], true);
        var requests = new List<PnUnifiedValueRequest>(); view.UnifyValueRequested += requests.Add;
        ExecuteUnify(boxes[3]); PumpMenuCleanup(); var request = Assert.Single(requests);
        Assert.Equal(cells[1].Spec.Id, request.RowId); Assert.Equal("TargetObject.Size", request.Path);
        Assert.Equal(cells[1].EditToken, request.Token);
    });

    /// <summary>模态 Esc 只取消一次且被消费；确认按键不能从窗口继续传到值格。</summary>
    [Fact]
    public void UnifiedDialogEscapeConsumesKeyAndDoesNotConfirm() => Sta(() =>
    {
        var (panel, _) = MixedComment(); var dialog = new UnifiedValueView("Size", ["A"], "原草稿", panel.Resources);
        int confirms = 0, cancels = 0; dialog.Confirmed += _ => confirms++; dialog.Canceled += () => cancels++;
        var key = new KeyEventArgs(Keyboard.PrimaryDevice, new TestSource(), 0, System.Windows.Input.Key.Escape)
            { RoutedEvent = Keyboard.PreviewKeyDownEvent };
        dialog.RaiseEvent(key); dialog.RaiseEvent(key);
        Assert.True(key.Handled); Assert.Equal(1, cancels); Assert.Equal(0, confirms);
        Assert.Equal("原草稿", Children<ComboBox>(dialog).Single().Text);
    });

    /// <summary>编辑框及 Popup／选项模板均使用局部主题，正常输入和长项提示不变白。</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UnifiedComboThemesCoverEditorPopupAndItems(bool dark) => Sta(() =>
    {
        var panel = new FastPropertiesView(); panel.SetTheme(dark);
        var dialog = new UnifiedValueView("TargetObject.Size", ["长值一", "长值二"], null, panel.Resources);
        dialog.Measure(new Size(480, 220)); dialog.Arrange(new Rect(0, 0, 480, 220)); dialog.UpdateLayout(); dialog.PrepareTemplate();
        var combo = Children<ComboBox>(dialog).Single();
        var editor = Assert.IsType<TextBox>(combo.Template.FindName("PART_EditableTextBox", combo));
        var popup = Assert.IsType<System.Windows.Controls.Primitives.Popup>(combo.Template.FindName("PART_Popup", combo));
        var border = Assert.IsType<Border>(popup.Child);
        Assert.Equal(((SolidColorBrush)panel.Resources["PanelSurface"]).Color, ((SolidColorBrush)border.Background).Color);
        Assert.Equal(((SolidColorBrush)panel.Resources["PanelForeground"]).Color, ((SolidColorBrush)editor.Foreground).Color);
        Assert.NotNull(dialog.TryFindResource(typeof(ComboBoxItem))); Assert.Equal(-1, combo.SelectedIndex);
        panel.SetTheme(!dark);
        Assert.Equal(((SolidColorBrush)panel.Resources["PanelSurface"]).Color, ((SolidColorBrush)border.Background).Color);
        Assert.Equal(((SolidColorBrush)panel.Resources["PanelForeground"]).Color, ((SolidColorBrush)editor.Foreground).Color);
    });

    private static void ExecuteUnify(TextBox value)
    {
        var binding = value.InputBindings.OfType<KeyBinding>().Single(x => x.Key == System.Windows.Input.Key.U);
        ((RoutedCommand)binding.Command).Execute(null, value);
    }

    private static void PumpMenuCleanup() => Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);

    private static void InvokeRemoval(MenuItem item) => ((RoutedCommand)item.Command).Execute(null, item.CommandTarget);

    private static IEnumerable<T> Visuals<T>(DependencyObject root) where T : DependencyObject
    {
        if (root is T value) yield return value;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (var item in Visuals<T>(VisualTreeHelper.GetChild(root, i))) yield return item;
    }

    private static void Focus(TextBox box, bool enter) => box.RaiseEvent(new KeyboardFocusChangedEventArgs(
        Keyboard.PrimaryDevice, 0, enter ? null : box, enter ? box : null)
        { RoutedEvent = enter ? Keyboard.GotKeyboardFocusEvent : Keyboard.LostKeyboardFocusEvent });
    private static void Key(TextBox box, System.Windows.Input.Key key) => box.RaiseEvent(new KeyEventArgs(
        Keyboard.PrimaryDevice, new TestSource(), 0, key) { RoutedEvent = Keyboard.PreviewKeyDownEvent });
    private static void Click(FastPropertiesView view, string title) => Children<System.Windows.Controls.Button>(view)
        .Single(b => Equals(b.Content, title)).RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
    private sealed class TestSource : PresentationSource
    {
        public override Visual RootVisual { get; set; } = null!;
        public override bool IsDisposed => false;
        protected override CompositionTarget GetCompositionTargetCore() => null!;
    }
    private static IEnumerable<T> Children<T>(DependencyObject root) where T : DependencyObject
    {
        if (root is T value) yield return value;
        foreach (object child in LogicalTreeHelper.GetChildren(root))
            if (child is DependencyObject dependency)
                foreach (var found in Children<T>(dependency)) yield return found;
    }
}
