using System.Windows;
using System.Windows.Markup;

namespace Pn3DKit.UI;

/// <summary>仅用于 PN快速特性的交互控件模板，统一菜单和按钮的主题及禁用外观。</summary>
internal static class FastPanelControlStyles
{
    /// <summary>每个视图在所属 UI 线程创建一次；不修改 CAD 或全局 WPF 资源。</summary>
    /// <returns>包含菜单、菜单项、分隔线及按钮的局部资源，实际启用及命令状态保持不变。</returns>
    internal static ResourceDictionary CreateResources() => (ResourceDictionary)XamlReader.Parse("""
        <ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                            xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
          <Style TargetType="{x:Type Button}">
            <Setter Property="Background" Value="{DynamicResource PanelHeader}"/>
            <Setter Property="Foreground" Value="{DynamicResource PanelForeground}"/>
            <Setter Property="BorderBrush" Value="{DynamicResource PanelBorder}"/>
            <Setter Property="BorderThickness" Value="1"/>
            <Setter Property="HorizontalContentAlignment" Value="Center"/>
            <Setter Property="VerticalContentAlignment" Value="Center"/>
            <Setter Property="Template">
              <Setter.Value>
                <ControlTemplate TargetType="{x:Type Button}">
                  <Border x:Name="Chrome" Background="{TemplateBinding Background}" BorderBrush="{TemplateBinding BorderBrush}"
                          BorderThickness="{TemplateBinding BorderThickness}" Padding="{TemplateBinding Padding}"
                          SnapsToDevicePixels="True">
                    <ContentPresenter RecognizesAccessKey="True" HorizontalAlignment="{TemplateBinding HorizontalContentAlignment}"
                                      VerticalAlignment="{TemplateBinding VerticalContentAlignment}"/>
                  </Border>
                  <ControlTemplate.Triggers>
                    <Trigger Property="IsMouseOver" Value="True">
                      <Setter TargetName="Chrome" Property="Background" Value="{DynamicResource PanelMenuHover}"/>
                    </Trigger>
                    <Trigger Property="IsKeyboardFocused" Value="True">
                      <Setter TargetName="Chrome" Property="BorderBrush" Value="{DynamicResource PanelSelection}"/>
                    </Trigger>
                    <Trigger Property="IsPressed" Value="True">
                      <Setter TargetName="Chrome" Property="Background" Value="{DynamicResource PanelMenuHover}"/>
                      <Setter TargetName="Chrome" Property="BorderBrush" Value="{DynamicResource PanelSelection}"/>
                    </Trigger>
                    <Trigger Property="IsEnabled" Value="False">
                      <Setter Property="Foreground" Value="{DynamicResource PanelReadOnlyForeground}"/>
                      <Setter TargetName="Chrome" Property="Background" Value="{DynamicResource PanelReadOnlyBackground}"/>
                      <Setter TargetName="Chrome" Property="BorderBrush" Value="{DynamicResource PanelBorder}"/>
                    </Trigger>
                  </ControlTemplate.Triggers>
                </ControlTemplate>
              </Setter.Value>
            </Setter>
          </Style>
          <Style TargetType="{x:Type ContextMenu}">
            <Setter Property="Background" Value="{DynamicResource PanelSurface}"/>
            <Setter Property="Foreground" Value="{DynamicResource PanelForeground}"/>
            <Setter Property="BorderBrush" Value="{DynamicResource PanelBorder}"/>
            <Setter Property="BorderThickness" Value="1"/>
            <Setter Property="Padding" Value="2"/>
            <Setter Property="MinWidth" Value="220"/>
            <Setter Property="FontFamily" Value="Segoe UI, Microsoft YaHei UI"/>
            <Setter Property="FontSize" Value="12"/>
            <Setter Property="UseLayoutRounding" Value="True"/>
            <Setter Property="SnapsToDevicePixels" Value="True"/>
            <Setter Property="Template">
              <Setter.Value>
                <ControlTemplate TargetType="{x:Type ContextMenu}">
                  <Border Background="{TemplateBinding Background}" BorderBrush="{TemplateBinding BorderBrush}"
                          BorderThickness="{TemplateBinding BorderThickness}" Padding="{TemplateBinding Padding}">
                    <ScrollViewer CanContentScroll="True" HorizontalScrollBarVisibility="Disabled"
                                  VerticalScrollBarVisibility="Auto">
                      <ItemsPresenter KeyboardNavigation.DirectionalNavigation="Cycle"/>
                    </ScrollViewer>
                  </Border>
                </ControlTemplate>
              </Setter.Value>
            </Setter>
          </Style>
          <Style TargetType="{x:Type MenuItem}">
            <Setter Property="Background" Value="{DynamicResource PanelSurface}"/>
            <Setter Property="Foreground" Value="{DynamicResource PanelForeground}"/>
            <Setter Property="BorderBrush" Value="Transparent"/>
            <Setter Property="BorderThickness" Value="1"/>
            <Setter Property="Padding" Value="9,3"/>
            <Setter Property="MinHeight" Value="24"/>
            <Setter Property="HorizontalContentAlignment" Value="Stretch"/>
            <Setter Property="Template">
              <Setter.Value>
                <ControlTemplate TargetType="{x:Type MenuItem}">
                  <Border x:Name="Row" Background="{TemplateBinding Background}"
                          BorderBrush="{TemplateBinding BorderBrush}" BorderThickness="{TemplateBinding BorderThickness}"
                          Padding="{TemplateBinding Padding}" SnapsToDevicePixels="True">
                    <Grid>
                      <Grid.ColumnDefinitions>
                        <ColumnDefinition Width="*"/>
                        <ColumnDefinition Width="Auto"/>
                      </Grid.ColumnDefinitions>
                      <ContentPresenter ContentSource="Header" RecognizesAccessKey="True"
                                        VerticalAlignment="Center"/>
                      <TextBlock Grid.Column="1" Text="{TemplateBinding InputGestureText}" Margin="24,0,0,0"
                                 Foreground="{TemplateBinding Foreground}" VerticalAlignment="Center"/>
                    </Grid>
                  </Border>
                  <ControlTemplate.Triggers>
                    <Trigger Property="IsHighlighted" Value="True">
                      <Setter TargetName="Row" Property="Background" Value="{DynamicResource PanelMenuHover}"/>
                      <Setter TargetName="Row" Property="BorderBrush" Value="{DynamicResource PanelBorder}"/>
                    </Trigger>
                    <Trigger Property="IsEnabled" Value="False">
                      <Setter Property="Foreground" Value="{DynamicResource PanelReadOnlyForeground}"/>
                      <Setter TargetName="Row" Property="Background" Value="{DynamicResource PanelSurface}"/>
                      <Setter TargetName="Row" Property="BorderBrush" Value="Transparent"/>
                    </Trigger>
                  </ControlTemplate.Triggers>
                </ControlTemplate>
              </Setter.Value>
            </Setter>
          </Style>
          <Style TargetType="{x:Type Separator}">
            <Setter Property="Margin" Value="6,4"/>
            <Setter Property="Template">
              <Setter.Value>
                <ControlTemplate TargetType="{x:Type Separator}">
                  <Border Height="1" Background="{DynamicResource PanelBorder}" SnapsToDevicePixels="True"/>
                </ControlTemplate>
              </Setter.Value>
            </Setter>
          </Style>
        </ResourceDictionary>
        """);
}
