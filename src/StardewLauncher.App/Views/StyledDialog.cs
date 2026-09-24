using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using StardewLauncher.App.Controls;
using StardewLauncher.App.Controls.Svg;
using StardewLauncher.App.Windows;

namespace StardewLauncher.App.Views;

/// <summary>提示的语气，决定图标与配色。</summary>
internal enum DialogTone
{
    Info = 0,
    Question = 1,
    Warning = 2,
    Error = 3
}

/// <summary>
/// 自绘的提示框。系统的 <c>MessageBox</c> 是灰底方角 + 系统字体 + 系统按钮，
/// 弹在启动器上非常突兀，所以全项目统一改用这个：卡片圆角、主题配色、启动器自己的按钮控件。
///
/// <para>外框直接复用 <see cref="LauncherWindow"/> 那套（WindowChrome + 自绘投影），
/// 这里只负责标题、正文与按钮。</para>
///
/// <para>不要再用 <c>MessageBox.Show</c>，新增提示一律走 <see cref="Dialogs"/>。</para>
/// </summary>
internal sealed class StyledDialog : LauncherWindow
{
    private StyledDialog(string caption, string message, DialogTone tone, string primaryText, string? secondaryText)
    {
        SizeToContent = SizeToContent.Height;
        Width = 470;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Title = caption;

        var layout = new Grid { Margin = new Thickness(20, 4, 20, 16) };
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        // 标题：图标 + 文字
        var header = new StackPanel { Orientation = Orientation.Horizontal };

        var icon = new SvgIcon
        {
            Icon = IconOf(tone),
            Width = 18,
            Height = 18,
            StrokeThickness = 1.8,
            VerticalAlignment = VerticalAlignment.Center
        };
        icon.SetResourceReference(SvgIcon.IconBrushProperty, BrushOf(tone));
        header.Children.Add(icon);

        var captionText = new TextBlock
        {
            Text = caption,
            FontSize = 14.5,
            FontWeight = FontWeights.Bold,
            Margin = new Thickness(9, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.Wrap
        };
        captionText.SetResourceReference(TextBlock.ForegroundProperty, "Text.Primary");
        header.Children.Add(captionText);

        Grid.SetRow(header, 0);
        layout.Children.Add(header);

        // 正文
        var body = new TextBlock
        {
            Text = message,
            FontSize = 12.5,
            LineHeight = 19,
            TextWrapping = TextWrapping.Wrap
        };
        body.SetResourceReference(TextBlock.ForegroundProperty, "Text.Secondary");

        var scroller = new ScrollViewer
        {
            Margin = new Thickness(0, 12, 0, 0),
            MaxHeight = 340,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = body
        };

        Grid.SetRow(scroller, 1);
        layout.Children.Add(scroller);

        // 按钮：左边次操作、右边主操作（单个按钮时只有主操作）
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 18, 0, 0)
        };

        if (!string.IsNullOrWhiteSpace(secondaryText))
        {
            var secondary = new OutlineButton
            {
                Content = secondaryText,
                Tone = ButtonTone.Plain,
                Width = 92,
                Height = 34,
                IsCancel = true
            };
            secondary.Click += (_, _) => DialogResult = false;
            buttons.Children.Add(secondary);
        }

        var confirm = new OutlineButton
        {
            Content = primaryText,
            Tone = ButtonTone.Solid,
            Width = 92,
            Height = 34,
            IsDefault = true,
            Margin = string.IsNullOrWhiteSpace(secondaryText) ? new Thickness(0) : new Thickness(8, 0, 0, 0)
        };
        confirm.Click += (_, _) => DialogResult = true;
        buttons.Children.Add(confirm);

        Grid.SetRow(buttons, 2);
        layout.Children.Add(buttons);

        // 外框（圆角 + 投影 + 拖动条）由 LauncherWindow 负责
        Content = layout;

        Loaded += (_, _) => confirm.Focus();
    }

    private static string IconOf(DialogTone tone) => tone switch
    {
        DialogTone.Question => "lucide/circle-help",
        DialogTone.Warning => "lucide/triangle-alert",
        DialogTone.Error => "lucide/triangle-alert",
        _ => "lucide/info"
    };

    private static string BrushOf(DialogTone tone) => tone switch
    {
        DialogTone.Warning => "Status.Warn",
        DialogTone.Error => "Status.Danger",
        _ => "Accent.Base"
    };

    internal static bool Show(Window? owner, string message, string caption, DialogTone tone,
        string primaryText, string? secondaryText)
    {
        var dialog = new StyledDialog(caption, message, tone, primaryText, secondaryText);

        if (owner is { IsLoaded: true })
        {
            dialog.Owner = owner;
        }
        else
        {
            dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }

        return dialog.ShowDialog() == true;
    }
}

/// <summary>
/// 提示框的统一入口。全项目不要再用 <c>MessageBox.Show</c>：那个是系统样式，和启动器风格不一致。
/// </summary>
internal static class Dialogs
{
    /// <summary>只有「知道了」的信息提示。</summary>
    public static void Info(Window? owner, string message, string caption = "提示")
        => StyledDialog.Show(owner, message, caption, DialogTone.Info, "知道了", null);

    /// <summary>只有「知道了」的警告提示。</summary>
    public static void Warn(Window? owner, string message, string caption = "注意")
        => StyledDialog.Show(owner, message, caption, DialogTone.Warning, "知道了", null);

    /// <summary>只有「知道了」的错误提示。</summary>
    public static void Error(Window? owner, string message, string caption = "出错了")
        => StyledDialog.Show(owner, message, caption, DialogTone.Error, "知道了", null);

    /// <summary>「确定 / 取消」，确认返回 true。</summary>
    public static bool Confirm(Window? owner, string message, string caption,
        string confirmText = "确定", string cancelText = "取消")
        => StyledDialog.Show(owner, message, caption, DialogTone.Question, confirmText, cancelText);

    /// <summary>「是 / 否」，选是返回 true。</summary>
    public static bool Ask(Window? owner, string message, string caption, string yesText = "是", string noText = "否")
        => StyledDialog.Show(owner, message, caption, DialogTone.Question, yesText, noText);
}
