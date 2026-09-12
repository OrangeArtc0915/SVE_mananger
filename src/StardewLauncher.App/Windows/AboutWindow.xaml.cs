using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using StardewLauncher.Core.IO;
using StardewLauncher.Core.Logging;
using CoreApp = StardewLauncher.Core.App;

namespace StardewLauncher.App.Windows;

/// <summary>「关于」独立窗口。内容与设置页解耦，只能由左侧导航打开。</summary>
public partial class AboutWindow : Window
{
    private const string CopyHint = "复制后可直接在 QQ 里搜索群号。";

    private readonly DispatcherTimer _hintTimer;

    public AboutWindow()
    {
        InitializeComponent();

        _hintTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
        _hintTimer.Tick += (_, _) =>
        {
            _hintTimer.Stop();
            ResetHint();
        };

        ResetHint();
    }

    private void OnOpenGitHubClick(object sender, MouseButtonEventArgs e)
        => ShellHelper.OpenUrl(CoreApp.AppInfo.GitHubUrl);

    private void OnOpenGiteeClick(object sender, MouseButtonEventArgs e)
        => ShellHelper.OpenUrl(CoreApp.AppInfo.GiteeUrl);

    private void OnCopyQqGroupClick(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(CoreApp.AppInfo.QqGroup);
            ShowHint($"已复制群号 {CoreApp.AppInfo.QqGroup}", false);
        }
        catch (Exception ex)
        {
            ShowHint($"复制失败，请手动记录群号 {CoreApp.AppInfo.QqGroup}", true);
            Log.Warn($"复制 QQ 群号失败：{ex.Message}");
        }
    }

    private void OnOpenWebsiteClick(object sender, MouseButtonEventArgs e)
    {
        ShellHelper.OpenUrl(CoreApp.AppInfo.WebsiteUrl);
        ShowHint("已在默认浏览器中打开项目官网。", false);
    }

    /// <summary>提示一次，几秒后自动恢复成默认文案。</summary>
    private void ShowHint(string text, bool isError)
    {
        if (LabCopyHint is null) return;

        LabCopyHint.Text = text;
        LabCopyHint.SetResourceReference(TextBlock.ForegroundProperty,
            isError ? "Status.Danger" : "Text.Secondary");

        _hintTimer.Stop();
        _hintTimer.Start();
    }

    private void ResetHint()
    {
        if (LabCopyHint is null) return;

        LabCopyHint.Text = CopyHint;
        LabCopyHint.SetResourceReference(TextBlock.ForegroundProperty, "Text.Tertiary");
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();
}
