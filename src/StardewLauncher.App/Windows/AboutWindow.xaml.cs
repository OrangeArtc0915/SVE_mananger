using System.Diagnostics;
using System.IO;
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

    private const string WebsiteRelativePath = @"docs\website\index.html";

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

    private void OnOpenWebsiteClick(object sender, RoutedEventArgs e)
    {
        var path = FindWebsiteFile();

        if (path is null)
        {
            ShowHint($"没有找到 {WebsiteRelativePath}，请从仓库根目录运行本程序。", true);
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            ShowHint($"已在默认浏览器中打开 {path}", false);
        }
        catch (Exception ex)
        {
            ShowHint($"打开官网页面失败：{ex.Message}", true);
            Log.Warn($"打开本地官网页面失败：{ex.Message}");
        }
    }

    /// <summary>从程序目录逐级向上找仓库里的离线官网页面，找不到返回 null。</summary>
    private static string? FindWebsiteFile()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        for (var depth = 0; depth < 8 && directory is not null; depth++)
        {
            var candidate = Path.Combine(directory.FullName, WebsiteRelativePath);

            if (File.Exists(candidate)) return candidate;

            directory = directory.Parent;
        }

        return null;
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
