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
        Click(view, "添加属性"); Assert.Equal(2, view.ReadSpecs().Count);
        Click(view, "删除属性"); Assert.Single(view.ReadSpecs());
    });

    private static (FastPropertiesView View, TextBox Path, TextBox Value) Comment(string original = "原备注")
    {
        var view = new FastPropertiesView();
        var boxes = Children<TextBox>(view).ToArray();
        boxes[0].Text = "Comment"; Focus(boxes[0], false);
        ShowComment(view, original);
        return (view, boxes[0], boxes[1]);
    }

    /// <summary>通过生产元数据策略取得许可，不直接在烟测中硬编码 Writable。</summary>
    private static PnPanelSnapshot Snapshot(FastPropertiesView view, string original)
    {
        var specs = view.ReadSpecs();
        var permission = PnPropertyMetadataPolicy.Evaluate(new("Comment"), ["DisplayName", "UIOrder"], _ => "描述",
            () => false, () => throw new InvalidOperationException("非获取属性"), () => throw new InvalidOperationException("无关格式"));
        var snapshot = PnPropertyPolicy.Merge(specs, [new("PnId:Valve", "阀门",
            specs.Select(s => new PnPropertyCell(s, FastPropertyValue.Value(original), permission)).ToArray())]);
        return snapshot with { Cells = snapshot.Cells.Select(c => c with { EditToken = Guid.NewGuid() }).ToArray() };
    }
    private static void ShowComment(FastPropertiesView view, string original) => view.Show(Snapshot(view, original));

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

    /// <summary>回车只提交一次并在进入 CAD 上下文前释放保留；后续失焦不重复写入。</summary>
    [Fact]
    public void EnterSubmitsCommentOnceAndReleasesFocus() => Sta(() =>
    {
        var (view, _, value) = Comment();
        var requests = new List<PnEditRequest>(); bool retain = false;
        view.EditRequested += requests.Add; view.InputFocusChanged += keep => retain = keep;
        Focus(value, true); Assert.True(retain); value.Text = "新备注"; Key(value, System.Windows.Input.Key.Enter);
        Assert.Single(requests); Assert.False(retain); Assert.True(value.IsReadOnly);
        Assert.Equal("TargetObject.Comment", requests[0].Path);
        Assert.Equal("原备注", requests[0].Original); Assert.Equal("新备注", requests[0].Value);
        Focus(value, false); Assert.Single(requests);
        view.ResumeInputFocus(); ShowComment(view, "新备注"); Assert.False(value.IsReadOnly);
    });

    /// <summary>空 Comment 可编辑，Esc 后连续回车和失焦不把占位破折号写成备注。</summary>
    [Fact]
    public void EmptyCommentEscapeAndUnchangedEnterDoNotSubmitPlaceholder() => Sta(() =>
    {
        var (view, _, value) = Comment("");
        int submissions = 0; view.EditRequested += _ => submissions++;
        Assert.Equal("—", value.Text);
        Focus(value, true); Assert.Equal("", value.Text);
        value.Text = "撤销的输入"; Key(value, System.Windows.Input.Key.Escape);
        Assert.Equal("", value.Text);
        Key(value, System.Windows.Input.Key.Enter); Key(value, System.Windows.Input.Key.Enter);
        Focus(value, false); Assert.Equal(0, submissions);
        Assert.Equal("—", value.Text);
        Focus(value, true); value.Text = "首条备注"; Focus(value, false);
        Assert.Equal(1, submissions);
    });

    /// <summary>失焦提交保留路径、原值和修订，不能再次复用旧票据。</summary>
    [Fact]
    public void LostFocusSubmitsCommentOnce() => Sta(() =>
    {
        var (view, _, value) = Comment();
        var edits = new List<PnEditRequest>(); view.EditRequested += edits.Add;
        Focus(value, true); value.Text = "失焦备注"; Focus(value, false); Focus(value, false);
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
