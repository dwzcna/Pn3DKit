using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Markup;
using WpfButton = System.Windows.Controls.Button;
using WpfTextBox = System.Windows.Controls.TextBox;
using WpfComboBox = System.Windows.Controls.ComboBox;

namespace Pn3DKit.UI;

/// <summary>统一键值的纯文本编辑器；只发布确认或取消，不访问 CAD、不写入属性。</summary>
internal sealed class UnifiedValueView : System.Windows.Controls.UserControl
{
    private readonly WpfComboBox _input;
    private bool _finished;
    internal event Action<string>? Confirmed;
    internal event Action? Canceled;

    /// <summary>只用完整票据的候选列表；没有草稿时输入为空，不默认选择第一项。</summary>
    /// <param name="property">当前内部属性名，仅用于普通文本提示。</param>
    /// <param name="options">已经由 Ordinal 全量去重排序的真实值，不从 Tooltip 反解析。</param>
    /// <param name="draft">null 表示无草稿；空字符串仍为明确空草稿，不修剪内容。</param>
    /// <param name="theme">面板局部主题字典；弹出列表亦桥接同一资源。</param>
    internal UnifiedValueView(string property, IReadOnlyList<string> options, string? draft, ResourceDictionary theme)
    {
        Resources.MergedDictionaries.Add(theme);
        Resources.MergedDictionaries.Add(FastPanelControlStyles.CreateResources());
        Resources.MergedDictionaries.Add(ComboResources());
        SetResourceReference(BackgroundProperty, "PanelBackground");
        SetResourceReference(ForegroundProperty, "PanelForeground");
        FontFamily = new System.Windows.Media.FontFamily("Segoe UI, Microsoft YaHei UI"); FontSize = 12;
        UseLayoutRounding = true; SnapsToDevicePixels = true;
        var root = new StackPanel { Margin = new Thickness(16) };
        var prompt = new TextBlock { Text = "请输入或选择需要统一的 " + property + " 值：",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12) };
        prompt.SetResourceReference(TextBlock.ForegroundProperty, "PanelForeground"); root.Children.Add(prompt);
        _input = new WpfComboBox { IsEditable = true, IsTextSearchEnabled = false, SelectedIndex = -1,
            ItemsSource = options, Text = draft ?? "", MinHeight = 30,
            Style = (Style)FindResource(typeof(WpfComboBox)) };
        root.Children.Add(_input);
        var explanation = new TextBlock { Text = "确定只回填草稿；返回值格后，再按 Enter 统一提交。",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 12) };
        explanation.SetResourceReference(TextBlock.ForegroundProperty, "PanelMuted"); root.Children.Add(explanation);
        var buttons = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Right };
        var accept = ActionButton("确定"); var cancel = ActionButton("取消");
        accept.Click += (_, _) => Confirm(); cancel.Click += (_, _) => Cancel();
        buttons.Children.Add(accept); buttons.Children.Add(cancel); root.Children.Add(buttons); Content = root;
        PreviewKeyDown += (_, args) =>
        {
            if (args.Key == Key.Enter) { args.Handled = true; Confirm(); }
            else if (args.Key == Key.Escape) { args.Handled = true; Cancel(); }
        };
        Loaded += (_, _) => FocusEditor();
    }

    /// <summary>确认先消费窗口按键，原样返回文本；同一窗口只发一次结果。</summary>
    private void Confirm()
    {
        if (_finished) return;
        _finished = true; _input.IsDropDownOpen = false; Confirmed?.Invoke(_input.Text);
    }

    /// <summary>取消不返回新文本，也不要求视图刷新或丢弃原草稿。</summary>
    private void Cancel()
    {
        if (_finished) return;
        _finished = true; _input.IsDropDownOpen = false; Canceled?.Invoke();
    }

    /// <summary>应用局部模板并桥接 Popup 资源；不创建 CAD 或全局资源。</summary>
    internal void PrepareTemplate()
    {
        _input.ApplyTemplate();
        if (_input.Template.FindName("PART_Popup", _input) is Popup popup && !popup.Resources.MergedDictionaries.Contains(Resources))
            popup.Resources.MergedDictionaries.Add(Resources);
    }

    /// <summary>窗口显示后将焦点交给可编辑文本框，不自动选择候选。</summary>
    internal void FocusEditor()
    {
        PrepareTemplate();
        if (_input.Template.FindName("PART_EditableTextBox", _input) is WpfTextBox editor) editor.Focus();
    }

    private WpfButton ActionButton(string title) => new() { Content = title, MinWidth = 72,
        Padding = new Thickness(12, 5, 12, 5), Margin = new Thickness(8, 0, 0, 0),
        Style = (Style)FindResource(typeof(WpfButton)) };

    /// <summary>完整覆盖编辑框、箭头、Popup 和选项，避免默认白色下拉背景。</summary>
    private static ResourceDictionary ComboResources() => (ResourceDictionary)XamlReader.Parse("""
        <ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                            xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
          <Style TargetType="ComboBoxItem">
            <Setter Property="Foreground" Value="{DynamicResource PanelForeground}"/>
            <Setter Property="Background" Value="{DynamicResource PanelSurface}"/>
            <Setter Property="Padding" Value="8,5"/>
            <Setter Property="ToolTip" Value="{Binding}"/>
            <Setter Property="HorizontalContentAlignment" Value="Stretch"/>
            <Setter Property="Template"><Setter.Value>
              <ControlTemplate TargetType="ComboBoxItem">
                <Border x:Name="Item" Background="{TemplateBinding Background}" Padding="{TemplateBinding Padding}">
                  <ContentPresenter/>
                </Border>
                <ControlTemplate.Triggers>
                  <Trigger Property="IsHighlighted" Value="True">
                    <Setter TargetName="Item" Property="Background" Value="{DynamicResource PanelMenuHover}"/>
                  </Trigger>
                  <Trigger Property="IsSelected" Value="True">
                    <Setter TargetName="Item" Property="Background" Value="{DynamicResource PanelSelection}"/>
                  </Trigger>
                </ControlTemplate.Triggers>
              </ControlTemplate>
            </Setter.Value></Setter>
          </Style>
          <Style TargetType="ComboBox">
            <Setter Property="Foreground" Value="{DynamicResource PanelForeground}"/>
            <Setter Property="Background" Value="{DynamicResource PanelSurface}"/>
            <Setter Property="BorderBrush" Value="{DynamicResource PanelBorder}"/>
            <Setter Property="BorderThickness" Value="1"/>
            <Setter Property="Template"><Setter.Value>
              <ControlTemplate TargetType="ComboBox">
                <Grid>
                  <Grid.ColumnDefinitions><ColumnDefinition Width="*"/><ColumnDefinition Width="28"/></Grid.ColumnDefinitions>
                  <Border Grid.ColumnSpan="2" Background="{TemplateBinding Background}"
                          BorderBrush="{TemplateBinding BorderBrush}" BorderThickness="{TemplateBinding BorderThickness}"/>
                  <TextBox x:Name="PART_EditableTextBox" Margin="1" Padding="7,4" BorderThickness="0"
                           Background="Transparent" Foreground="{TemplateBinding Foreground}"
                           CaretBrush="{DynamicResource PanelForeground}" SelectionBrush="{DynamicResource PanelSelection}"
                           VerticalContentAlignment="Center" IsReadOnly="{TemplateBinding IsReadOnly}"/>
                  <ToggleButton Grid.Column="1" Focusable="False" Foreground="{TemplateBinding Foreground}"
                      IsChecked="{Binding IsDropDownOpen, RelativeSource={RelativeSource TemplatedParent}, Mode=TwoWay}">
                    <ToggleButton.Template><ControlTemplate TargetType="ToggleButton">
                      <Border x:Name="Arrow" Background="{DynamicResource PanelHeader}">
                        <TextBlock Text="▾" Foreground="{TemplateBinding Foreground}" HorizontalAlignment="Center" VerticalAlignment="Center"/>
                      </Border>
                      <ControlTemplate.Triggers><Trigger Property="IsMouseOver" Value="True">
                        <Setter TargetName="Arrow" Property="Background" Value="{DynamicResource PanelMenuHover}"/>
                      </Trigger></ControlTemplate.Triggers>
                    </ControlTemplate></ToggleButton.Template>
                  </ToggleButton>
                  <Popup x:Name="PART_Popup" Placement="Bottom" AllowsTransparency="True" Focusable="False"
                         IsOpen="{TemplateBinding IsDropDownOpen}" MinWidth="{TemplateBinding ActualWidth}">
                    <Border Background="{DynamicResource PanelSurface}" BorderBrush="{DynamicResource PanelBorder}" BorderThickness="1">
                      <ScrollViewer MaxHeight="260" CanContentScroll="True" HorizontalScrollBarVisibility="Auto"
                                    VerticalScrollBarVisibility="Auto" Background="{DynamicResource PanelSurface}">
                        <ItemsPresenter KeyboardNavigation.DirectionalNavigation="Contained"/>
                      </ScrollViewer>
                    </Border>
                  </Popup>
                </Grid>
              </ControlTemplate>
            </Setter.Value></Setter>
          </Style>
        </ResourceDictionary>
        """);
}

/// <summary>由 CAD ShowModalWindow 设置所有者；确认仅返回文本，关闭等同取消。</summary>
internal sealed class UnifiedValueWindow : System.Windows.Window
{
    internal string Value { get; private set; } = "";

    /// <summary>在 CAD 主线程显示，文档锁和事务必须先释放。</summary>
    internal UnifiedValueWindow(string property, IReadOnlyList<string> options, string? draft, ResourceDictionary theme)
    {
        Title = "统一键值"; Width = 480; MinWidth = 380; SizeToContent = SizeToContent.Height;
        ShowInTaskbar = false; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var view = new UnifiedValueView(property, options, draft, theme);
        view.Confirmed += value => { Value = value; DialogResult = true; };
        view.Canceled += () => DialogResult = false;
        Content = view;
    }
}
