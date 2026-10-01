using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using StardewLauncher.App.Controls.Svg;
using StardewLauncher.Core.Folders;
using StardewLauncher.Core.IO;
using StardewLauncher.Core.Logging;

namespace StardewLauncher.App.Views;

/// <summary>
/// 常用文件夹（工具箱里的一个工具）：把经常要翻的几个目录收在一页里，点一下直接在资源管理器打开。
/// 内置目录跟着当前实例与设置现算，收藏目录存在设置文件里。
/// 作为工具箱的子视图存在，进入时由宿主调用 <see cref="Activate"/>。
/// </summary>
public partial class QuickFoldersView : UserControl
{
    public QuickFoldersView()
    {
        InitializeComponent();
        Log.SetModule("常用文件夹");
    }

    /// <summary>宿主切到这个工具时调用：重新读一遍设置与当前实例。</summary>
    public void Activate() => Refresh();

    private void Refresh()
    {
        PanBuiltIn.ItemsSource = QuickFolderCatalog.BuiltIn();

        var custom = QuickFolderCatalog.Custom();
        PanCustom.ItemsSource = custom;
        LabCustomEmpty.Visibility = custom.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    // ————— 操作 —————

    private void OnOpenClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: QuickFolder folder }) return;

        if (!Directory.Exists(folder.Path))
        {
            ShowNotice($"目录不存在：{folder.Path}", true);
            return;
        }

        ShellHelper.OpenFolder(folder.Path);
    }

    private void OnRemoveClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: QuickFolder folder }) return;

        QuickFolderCatalog.Remove(folder.Path);
        Refresh();
        ShowNotice($"已移除「{folder.Name}」。");
    }

    private void OnAddClick(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "选择要收藏的目录",
            Multiselect = false
        };

        var owner = Window.GetWindow(this);
        var confirmed = owner is null ? dialog.ShowDialog() : dialog.ShowDialog(owner);
        if (confirmed != true) return;

        if (QuickFolderCatalog.Add(dialog.FolderName, out var error))
        {
            Refresh();
            ShowNotice($"已收藏「{dialog.FolderName}」。");
            return;
        }

        ShowNotice(error ?? "收藏失败。", true);
    }

    // ————— 提示条 —————

    private void ShowNotice(string text, bool warn = false)
    {
        LabNotice.Text = text;
        BarNotice.Background = (Brush)FindResource(warn ? "Status.WarnSoft" : "Accent.Faint");
        IconNotice.IconBrush = (Brush)FindResource(warn ? "Status.Warn" : "Accent.Base");
        IconNotice.Icon = warn ? "lucide/triangle-alert" : "lucide/info";
        BarNotice.Visibility = Visibility.Visible;
    }

    private void OnDismissNoticeClick(object sender, RoutedEventArgs e)
        => BarNotice.Visibility = Visibility.Collapsed;
}
