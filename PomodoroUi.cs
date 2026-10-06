using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace PomodoroIsland;

/// <summary>共用的配色与小控件工厂（浅色 / 深色两套都看得清，见 references/sdk-api.md §18）。</summary>
internal static class PomodoroUi
{
    /// <summary>
    /// 界面字体：微软雅黑 UI —— Windows 原生的中文界面字体，笔画均匀、小字号下不发虚。
    /// （试过圆角的「幼圆」，但它笔画偏细，10~14px 时看着糊。）
    /// </summary>
    public static readonly FontFamily UiFont = new("Microsoft YaHei UI");

    /// <summary>
    /// 数字/时间也统一用「微软雅黑 UI」：和中文标签同一套字体，
    /// 之前中文雅黑 + 数字 Segoe 混着用，看着不协调。
    /// </summary>
    public static readonly FontFamily NumberFont = new("Microsoft YaHei UI");

    /// <summary>给颜色加透明度（卡片里的浅色底、描边都用它调出来）。</summary>
    public static Windows.UI.Color WithAlpha(Windows.UI.Color color, byte alpha) =>
        Windows.UI.Color.FromArgb(alpha, color.R, color.G, color.B);

    /// <summary>阶段主色（参照 iOS 灵动岛那张图的蓝 + 番茄钟自己的阶段色）。</summary>
    public static Windows.UI.Color Accent(bool isLight, PomodoroPhase phase) => phase switch
    {
        PomodoroPhase.Focus => isLight
            ? Windows.UI.Color.FromArgb(255, 0xD7, 0x00, 0x15)   // iOS red（浅色）
            : Windows.UI.Color.FromArgb(255, 0xFF, 0x45, 0x3A),  // iOS red（深色）
        PomodoroPhase.ShortBreak => isLight
            ? Windows.UI.Color.FromArgb(255, 0x24, 0x8A, 0x3D)
            : Windows.UI.Color.FromArgb(255, 0x30, 0xD1, 0x58),
        _ => isLight
            ? Windows.UI.Color.FromArgb(255, 0x00, 0x7A, 0xFF)
            : Windows.UI.Color.FromArgb(255, 0x0A, 0x84, 0xFF),
    };

    /// <summary>「任意时长倒计时」的主色：参照图里那个蓝（iOS system blue）。</summary>
    public static Windows.UI.Color CountdownAccent(bool isLight) => isLight
        ? Windows.UI.Color.FromArgb(255, 0x00, 0x7A, 0xFF)
        : Windows.UI.Color.FromArgb(255, 0x0A, 0x84, 0xFF);

    /// <summary>
    /// 岛（一级卡片）专用色：**恒为参照图那个蓝**，不随阶段变 —— 一级卡片必须和原图一模一样。
    /// 阶段的颜色差异放到卡片里表达。
    /// </summary>
    public static Windows.UI.Color IslandAccent(bool isLight) => CountdownAccent(isLight);

    /// <summary>
    /// 小按钮：把它用到的主题画刷全部换成我们自己的共享画刷。
    /// 不这么做的话，悬停 / 按下时 WinUI 默认模板会用「应用主题」的颜色，
    /// 出现「浅色应用 + 深色岛」组合下悬停变白底白字的问题。
    /// </summary>
    public static Button Chip(string text, SolidColorBrush fill, SolidColorBrush text_, SolidColorBrush border, double fontSize = 12)
    {
        var button = new Button
        {
            Content = text,
            FontSize = fontSize,
            FontFamily = UiFont,
            Padding = new Thickness(12, 4, 12, 4),
            MinWidth = 0,
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(1),
            Background = fill,
            Foreground = text_,
            BorderBrush = border,
        };

        button.Resources["ButtonBackgroundPointerOver"] = fill;
        button.Resources["ButtonBackgroundPressed"] = fill;
        button.Resources["ButtonBackgroundDisabled"] = fill;
        button.Resources["ButtonForegroundPointerOver"] = text_;
        button.Resources["ButtonForegroundPressed"] = text_;
        button.Resources["ButtonBorderBrushPointerOver"] = border;
        button.Resources["ButtonBorderBrushPressed"] = border;
        return button;
    }

    /// <summary>一根 1px 的分隔线。</summary>
    public static Border Divider(SolidColorBrush brush) => new()
    {
        Height = 1,
        Background = brush,
        HorizontalAlignment = HorizontalAlignment.Stretch,
    };
}
