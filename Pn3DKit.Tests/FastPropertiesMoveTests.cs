using System.Collections;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Pn3DKit.Models;
using Pn3DKit.UI;
using Pn3DKit.Utils;

namespace Pn3DKit.Tests;

/// <summary>验证本地行重排、菜单收尾、票据及草稿；纯 STA 路由不操作 CAD。</summary>
public sealed partial class FastPropertiesViewTests
{
    private const BindingFlags RowLocal = BindingFlags.Instance | BindingFlags.NonPublic;

    /// <summary>提供三个已确认普通行与固定尾空行，实际许可及票据由视图正常消费。</summary>
    private static FastPropertiesView MovableRows()
    {
        var view = new FastPropertiesView();
        view.AppendCatalogProperties(["Comment", "Area", "Size"]);
        view.Show(MoveSnapshot(view));
        return view;
    }

    private static PnPanelSnapshot MoveSnapshot(FastPropertiesView view) => new("离线对象", 2,
        view.ReadSpecs().Select(s => new PnPropertyCell(s, s.Input.Length == 0
            ? FastPropertyValue.Missing() : FastPropertyValue.Value(s.Input + "原值"),
            s.Input.Length == 0 ? PnEditPermission.ReadOnly("尾空行") : PnEditPermission.Writable,
            s.Input.Length == 0 ? Guid.Empty : Guid.NewGuid())).ToArray(), "离线测试");

    private static object RowAt(FastPropertiesView view, int index) =>
        ((IList)typeof(FastPropertiesView).GetField("_rows", RowLocal)!.GetValue(view)!)[index]!;

    private static T RowField<T>(object row, string name) => (T)row.GetType().GetField(name, RowLocal)!.GetValue(row)!;
    private static TextBox MoveBox(FastPropertiesView view, int row, bool value) =>
        Children<TextBox>(view).ElementAt(row * 2 + (value ? 1 : 0));

    /// <summary>记录与编辑身份有关的整行状态，验证重排没有偷偷创建新行或刷新票据。</summary>
    private static object?[] MoveState(object row) => new[]
    {
        "Id", "Revision", "Committed", "PendingPath", "Editing", "Dirty", "Draft", "DraftRevision", "Cell"
    }.Select(n => row.GetType().GetField(n, RowLocal)!.GetValue(row)).ToArray();

    private static RoutedCommand MoveCommand(TextBox box, bool down) =>
        (RoutedCommand)box.InputBindings.OfType<KeyBinding>().Single(b => b.Key == (down ? System.Windows.Input.Key.Down : System.Windows.Input.Key.Up)
            && b.Modifiers == ModifierKeys.Alt).Command;

    private static void Move(TextBox box, bool down)
    {
        MoveCommand(box, down).Execute(null, box);
        PumpMenuCleanup();
    }

    /// <summary>显式提供修饰键验证路由；SystemKey 使用 WPF 自身内部转换，不模拟系统键盘。</summary>
    private static KeyEventArgs MoveKey(FastPropertiesView view, TextBox box, System.Windows.Input.Key key,
        ModifierKeys modifiers, bool system = false)
    {
        var args = new KeyEventArgs(Keyboard.PrimaryDevice, new TestSource(), 0, key)
            { RoutedEvent = Keyboard.PreviewKeyDownEvent };
        if (system) typeof(KeyEventArgs).GetMethod("MarkSystem", RowLocal)!.Invoke(args, null);
        typeof(FastPropertiesView).GetMethod("HandleMoveShortcut", RowLocal)!.Invoke(view, [args, box, modifiers]);
        return args;
    }

    /// <summary>两列菜单与按键共用所属行命令；面板和统一键值窗口不绑定 Alt 方向键。</summary>
    [Fact]
    public void MoveBindingsAreCellLocalAndShareMenuCommands() => Sta(() =>
    {
        var view = MovableRows();
        foreach (var box in Children<TextBox>(view))
        foreach (bool down in new[] { false, true })
        {
            var binding = box.InputBindings.OfType<KeyBinding>().Single(b => b.Key == (down ? System.Windows.Input.Key.Down : System.Windows.Input.Key.Up));
            var item = box.ContextMenu.Items.OfType<MenuItem>().Single(i => Equals(i.Header, down ? "下移" : "上移"));
            Assert.Same(box, binding.CommandTarget); Assert.Same(box, item.CommandTarget);
            Assert.Same(binding.Command, item.Command); Assert.Equal(ModifierKeys.Alt, binding.Modifiers);
            Assert.Equal(down ? "Alt+↓" : "Alt+↑", item.InputGestureText);
        }
        Assert.Empty(view.InputBindings);
        var dialog = new UnifiedValueView("Area", ["A", "B"], null, view.Resources);
        Assert.DoesNotContain(Children<ComboBox>(dialog).Single().InputBindings.OfType<KeyBinding>(),
            b => b.Key is System.Windows.Input.Key.Down or System.Windows.Input.Key.Up);
    });

    /// <summary>整行移动复用原控件，保留票据、路径、独立草稿修订及选区。</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MovingPreservesRowControlsAndEditingIdentity(bool valueCell) => Sta(() =>
    {
        var view = MovableRows(); var row = RowAt(view, 1);
        var path = MoveBox(view, 1, false); var value = MoveBox(view, 1, true);
        var box = valueCell ? value : path; Focus(box, true); box.Select(1, 2);
        var state = MoveState(row); int notifications = 0;
        view.RowsChanging += () => notifications++; view.RowsCommitted += () => notifications++;
        view.EditRequested += _ => notifications++;
        Move(box, false);
        Assert.Same(row, RowAt(view, 0)); Assert.Same(path, MoveBox(view, 0, false)); Assert.Same(value, MoveBox(view, 0, true));
        Assert.Same(RowField<Border>(row, "Container"), ((System.Windows.Controls.StackPanel)typeof(FastPropertiesView)
            .GetField("_table", RowLocal)!.GetValue(view)!).Children[0]);
        Assert.Equal(state, MoveState(row)); Assert.Equal(1, box.SelectionStart); Assert.Equal(2, box.SelectionLength);
        Assert.Equal(new[] { "Area", "Comment", "Size", "" }, view.ReadSpecs().Select(s => s.Input));
        Assert.Equal(0, notifications);
    });

    /// <summary>两列在上下边界及固定尾行都消费快捷键，强制执行也不能越界。</summary>
    [Theory]
    [InlineData(0, false, false)]
    [InlineData(0, true, false)]
    [InlineData(2, false, true)]
    [InlineData(2, true, true)]
    [InlineData(3, false, false)]
    [InlineData(3, true, false)]
    [InlineData(3, false, true)]
    [InlineData(3, true, true)]
    public void MoveBoundariesKeepTrailingPlaceholderAndConsumeKeys(int index, bool valueCell, bool down) => Sta(() =>
    {
        var view = MovableRows(); var before = view.ReadSpecs().ToArray(); var box = MoveBox(view, index, valueCell);
        Assert.False(MoveCommand(box, down).CanExecute(null, box));
        Move(box, down);
        var key = MoveKey(view, box, down ? System.Windows.Input.Key.Down : System.Windows.Input.Key.Up, ModifierKeys.Alt, true);
        PumpMenuCleanup(); Assert.True(key.Handled); Assert.Equal(before, view.ReadSpecs());
    });

    /// <summary>准确 Alt 和 WPF SystemKey 移动一格，额外修饰键不接管。</summary>
    [Theory]
    [InlineData(ModifierKeys.Alt, true)]
    [InlineData(ModifierKeys.None, false)]
    [InlineData(ModifierKeys.Control, false)]
    [InlineData(ModifierKeys.Alt | ModifierKeys.Shift, false)]
    [InlineData(ModifierKeys.Alt | ModifierKeys.Control, false)]
    public void MoveShortcutUsesExactAltAndSystemKey(ModifierKeys modifiers, bool accepted) => Sta(() =>
    {
        var view = MovableRows(); var box = MoveBox(view, 1, false);
        var key = MoveKey(view, box, System.Windows.Input.Key.Up, modifiers, true);
        Assert.Equal(System.Windows.Input.Key.System, key.Key); Assert.Equal(System.Windows.Input.Key.Up, key.SystemKey);
        PumpMenuCleanup(); Assert.Equal(accepted, key.Handled);
        Assert.Equal(accepted ? "Area" : "Comment", view.ReadSpecs()[0].Input);
    });

    /// <summary>中间空行是正常配置行，只有最终占位不能移动。</summary>
    [Fact]
    public void IntermediateEmptyRowCanMoveWithoutCreatingExtraPlaceholders() => Sta(() =>
    {
        var view = MovableRows(); var row = RowAt(view, 1); var path = MoveBox(view, 1, false);
        path.Text = ""; Key(path, System.Windows.Input.Key.Enter);
        var specs = view.ReadSpecs().ToArray(); Move(path, false);
        Assert.Same(row, RowAt(view, 0)); Assert.Equal(4, view.ReadSpecs().Count);
        Assert.Equal(specs.Select(s => s.Id).Order(), view.ReadSpecs().Select(s => s.Id).Order());
        Assert.Equal(new[] { "", "Comment", "Size", "" }, view.ReadSpecs().Select(s => s.Input));
    });

    /// <summary>无选择、只读、读取中、忙碌和缺少票据均不影响本地配置重排。</summary>
    [Theory]
    [InlineData("none")]
    [InlineData("readonly")]
    [InlineData("reading")]
    [InlineData("busy")]
    [InlineData("ticket")]
    public void MovingDoesNotRequireCadSelectionOrWritePermission(string state) => Sta(() =>
    {
        var view = MovableRows();
        if (state == "none") view.Show(PnPropertyPolicy.Empty(view.ReadSpecs(), "未选择对象"));
        if (state == "busy") view.SuspendEditing();
        if (state is "readonly" or "reading" or "ticket")
        {
            var snapshot = MoveSnapshot(view);
            view.Show(snapshot with { Cells = snapshot.Cells.Select(c => c with
            {
                Value = state == "reading" ? FastPropertyValue.Missing() : c.Value,
                Permission = state == "readonly" ? PnEditPermission.ReadOnly("图层锁定") : c.Permission,
                EditToken = Guid.Empty
            }).ToArray() });
        }
        var path = MoveBox(view, 1, false); var value = MoveBox(view, 1, true);
        Assert.True(value.IsReadOnly); Assert.True(MoveCommand(value, false).CanExecute(null, value));
        Move(value, false);
        Assert.Same(path, MoveBox(view, 0, false)); Assert.True(value.IsReadOnly);
    });

    /// <summary>菜单已关闭或仍开启时，重排都废弃旧收尾回调，不确认未完成路径。</summary>
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void MenuMoveDoesNotCommitPendingPath(bool valueMenu, bool alreadyClosed) => Sta(() =>
    {
        var view = MovableRows(); var path = MoveBox(view, 1, false); var box = MoveBox(view, 1, valueMenu);
        Focus(path, true); path.Text = "AreaPending";
        var row = RowAt(view, 1); var state = MoveState(row); int notifications = 0;
        view.RowsCommitted += () => notifications++; view.RowsChanging += () => notifications++;
        view.EditRequested += _ => notifications++;
        box.ContextMenu.RaiseEvent(new RoutedEventArgs(ContextMenu.OpenedEvent));
        Focus(path, false);
        if (alreadyClosed) box.ContextMenu.RaiseEvent(new RoutedEventArgs(ContextMenu.ClosedEvent));
        var item = box.ContextMenu.Items.OfType<MenuItem>().Single(i => Equals(i.Header, "上移"));
        ((RoutedCommand)item.Command).Execute(null, item.CommandTarget);
        Focus(path, false); // 模拟重挂控件期间的临时失焦。
        if (!alreadyClosed) box.ContextMenu.RaiseEvent(new RoutedEventArgs(ContextMenu.ClosedEvent));
        PumpMenuCleanup();
        Assert.Same(row, RowAt(view, 0)); Assert.Equal(state, MoveState(row));
        Assert.Equal("AreaPending", path.Text); Assert.Equal("", view.ReadSpecs()[0].Input); Assert.Equal(0, notifications);
        Focus(path, true); Focus(path, false);
        Assert.Equal("AreaPending", view.ReadSpecs()[0].Input); Assert.Equal(1, notifications);
    });

    /// <summary>右键所属行独立于上次活动行，移动方向不会误作用到其他行。</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MoveMenuTargetsOwnRowInsteadOfActiveRow(bool valueCell) => Sta(() =>
    {
        var view = MovableRows(); var target = MoveBox(view, 1, valueCell);
        Focus(MoveBox(view, 0, false), true);
        var item = target.ContextMenu.Items.OfType<MenuItem>().Single(i => Equals(i.Header, "上移"));
        ((RoutedCommand)item.Command).Execute(null, item.CommandTarget); PumpMenuCleanup();
        Assert.Equal("Area", view.ReadSpecs()[0].Input);
    });

    /// <summary>移动保留多行草稿；回车仍使用原行 ID、票据、路径和原值，仅提交一次。</summary>
    [Fact]
    public void MoveKeepsAllDraftsAndEnterUsesOriginalRowTicket() => Sta(() =>
    {
        var view = MovableRows(); var first = MoveBox(view, 0, true); var value = MoveBox(view, 1, true);
        Focus(first, true); first.Text = "第一行草稿"; Focus(first, false);
        Focus(value, true); value.Text = "目标行草稿"; value.CaretIndex = 3;
        var row = RowAt(view, 1); var original = RowField<PnPropertyCell>(row, "Cell"); var state = MoveState(row);
        var edits = new List<PnEditRequest>(); view.EditRequested += edits.Add;
        Move(value, false); Assert.Equal(state, MoveState(row)); Assert.Equal(3, value.CaretIndex);
        Assert.Equal("第一行草稿", first.Text); Assert.Equal("目标行草稿", value.Text); Assert.Empty(edits);
        Focus(value, true); Key(value, System.Windows.Input.Key.Enter); Key(value, System.Windows.Input.Key.Enter);
        var edit = Assert.Single(edits); Assert.Equal(original.Spec.Id, edit.RowId); Assert.Equal(original.EditToken, edit.Token);
        Assert.Equal("TargetObject.Area", edit.Path); Assert.Equal("Area原值", edit.Original); Assert.Equal("目标行草稿", edit.Value);
        Assert.DoesNotContain("第一行草稿", first.Text); // 真实提交沿用全局票据失效。
    });

    /// <summary>半透明结构化占位及真实同文值移动后仍区分；Esc 不写入。</summary>
    [Theory]
    [InlineData("mixed")]
    [InlineData("empty")]
    [InlineData("null")]
    [InlineData("literal")]
    public void MovePreservesPlaceholderStateAndEscape(string state) => Sta(() =>
    {
        var view = MovableRows(); var snapshot = MoveSnapshot(view);
        var original = state switch
        {
            "mixed" => new FastPropertyValue(FastValueKind.Mixed),
            "empty" => FastPropertyValue.Value(""),
            "null" => FastPropertyValue.Value(null),
            _ => FastPropertyValue.Value("*(多种)")
        };
        view.Show(snapshot with { Cells = snapshot.Cells.Select(c => c.Spec.Input == "Area" ? c with { Value = original } : c).ToArray() });
        var path = MoveBox(view, 1, false); var value = MoveBox(view, 1, true);
        string text = value.Text; double opacity = value.Foreground.Opacity;
        int writes = 0; view.EditRequested += _ => writes++;
        Move(path, false); Assert.Equal(text, value.Text); Assert.Equal(opacity, value.Foreground.Opacity);
        Focus(value, true); value.Text = "放弃草稿"; Move(value, true); Focus(value, true);
        Key(value, System.Windows.Input.Key.Escape); Focus(value, false);
        Assert.Equal(text, value.Text); Assert.Equal(0, writes);
    });

    /// <summary>旧顺序的读取结果按行 ID 回填，不能因为当前位置变化而串值。</summary>
    [Fact]
    public void OldOrderSnapshotMapsByRowIdentityAfterMove() => Sta(() =>
    {
        var view = MovableRows(); var snapshot = MoveSnapshot(view);
        var comment = MoveBox(view, 0, true); var area = MoveBox(view, 1, true); var size = MoveBox(view, 2, true);
        Move(MoveBox(view, 1, false), false); view.Show(snapshot);
        Assert.Same(area, MoveBox(view, 0, true)); Assert.Equal("Area原值", area.Text);
        Assert.Same(comment, MoveBox(view, 1, true)); Assert.Equal("Comment原值", comment.Text);
        Assert.Same(size, MoveBox(view, 2, true)); Assert.Equal("Size原值", size.Text);
        Assert.Equal(snapshot.Cells[1].EditToken, RowField<PnPropertyCell>(RowAt(view, 0), "Cell").EditToken);
    });

    /// <summary>统一键值请求排队后仍可移动同一行，排序不使有效请求过期。</summary>
    [Fact]
    public void QueuedUnifiedValueSurvivesMovingItsRow() => Sta(() =>
    {
        var view = MovableRows(); var snapshot = MoveSnapshot(view);
        view.Show(snapshot with { Cells = snapshot.Cells.Select(c => c.Spec.Input == "Area"
            ? c with { Value = new FastPropertyValue(FastValueKind.Mixed) } : c).ToArray() });
        var value = MoveBox(view, 1, true); var requests = new List<PnUnifiedValueRequest>();
        view.UnifyValueRequested += requests.Add; int writes = 0; view.EditRequested += _ => writes++;
        ExecuteUnify(value); Move(value, false);
        var request = Assert.Single(requests); Assert.Equal(view.ReadSpecs()[0].Id, request.RowId);
        Assert.True(view.IsUnifiedRequestCurrent(request)); Assert.True(view.TryApplyUnifiedDraft(request, "统一草稿"));
        PumpMenuCleanup(); Assert.Equal("统一草稿", value.Text); Assert.Equal(0, writes);
    });

    /// <summary>真实失效取消尚未执行的菜单移动和恢复焦点，草稿不能重新挂回。</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void InvalidationCancelsQueuedMoveAndDraftRestoration(bool hide) => Sta(() =>
    {
        var view = MovableRows(); var value = MoveBox(view, 1, true); var specs = view.ReadSpecs().ToArray();
        Focus(value, true); value.Text = "废弃草稿";
        value.ContextMenu.RaiseEvent(new RoutedEventArgs(ContextMenu.OpenedEvent));
        MoveCommand(value, false).Execute(null, value);
        if (hide) view.ClearInputFocus(); else view.SuspendEditing();
        PumpMenuCleanup(); Assert.Equal(specs, view.ReadSpecs()); Assert.True(value.IsReadOnly);
        Assert.DoesNotContain("废弃草稿", value.Text);
        view.Show(MoveSnapshot(view)); Focus(value, true); int writes = 0; view.EditRequested += _ => writes++;
        Key(value, System.Windows.Input.Key.Enter); Assert.Equal(0, writes);
    });

    /// <summary>移动排队期间真实输入更新修订，旧请求不得覆盖新的草稿及光标。</summary>
    [Fact]
    public void NewDraftDuringQueuedMoveIsNotOverwrittenByOldCaret() => Sta(() =>
    {
        var view = MovableRows(); var value = MoveBox(view, 1, true);
        Focus(value, true); value.Text = "旧草稿"; value.CaretIndex = 1;
        MoveCommand(value, false).Execute(null, value);
        value.Text = "排队期间的新草稿"; value.Select(4, 3);
        PumpMenuCleanup(); Assert.Same(value, MoveBox(view, 0, true));
        Assert.Equal("排队期间的新草稿", value.Text); Assert.Equal(4, value.SelectionStart); Assert.Equal(3, value.SelectionLength);
    });

    /// <summary>排队期间真正修改路径会撤销本轮延迟操作，保留新输入而不乱序回填。</summary>
    [Fact]
    public void PathChangeCancelsQueuedMove() => Sta(() =>
    {
        var view = MovableRows(); var path = MoveBox(view, 1, false); var ids = view.ReadSpecs().Select(s => s.Id).ToArray();
        MoveCommand(path, false).Execute(null, path); path.Text = "AreaUpdated"; PumpMenuCleanup();
        Assert.Equal(ids, view.ReadSpecs().Select(s => s.Id)); Assert.Equal("AreaUpdated", path.Text);
        Assert.Equal("", view.ReadSpecs()[1].Input);
    });

    /// <summary>同一原控件可重复相邻移动；Ctrl+D 仍删除该行并废弃草稿，不写对象。</summary>
    [Fact]
    public void RepeatedMoveKeepsOriginalRowAndCtrlDRemovalSemantics() => Sta(() =>
    {
        var view = MovableRows(); var row = RowAt(view, 0); var value = MoveBox(view, 0, true);
        Focus(value, true); value.Text = "移除草稿"; int writes = 0; view.EditRequested += _ => writes++;
        Move(value, true); Move(value, true);
        Assert.Same(row, RowAt(view, 2)); Assert.False(MoveCommand(value, true).CanExecute(null, value));
        Move(value, false); Assert.Same(row, RowAt(view, 1));
        var remove = value.InputBindings.OfType<KeyBinding>().Single(b => b.Key == System.Windows.Input.Key.D);
        ((RoutedCommand)remove.Command).Execute(null, value); PumpMenuCleanup();
        Assert.Equal(new[] { "Area", "Size", "" }, view.ReadSpecs().Select(s => s.Input)); Assert.Equal(0, writes);
    });
}
