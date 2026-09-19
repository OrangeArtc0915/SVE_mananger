using System.Windows;
using System.Windows.Controls;
using StardewLauncher.Core.Logging;

namespace StardewLauncher.App.Windows;

/// <summary>
/// 「分享给队友」窗口：把组网分享文本显示出来，方便复制或照着念。
/// 文本由调用方按当前房间配置生成，这里只负责展示与复制。
/// </summary>
public partial class ShareWindow : Window
{
    private readonly string _shareText;

    public ShareWindow(string shareText)
    {
        InitializeComponent();

        _shareText = shareText;
        TxtShare.Text = shareText;
        TxtShare.CaretIndex = 0;

        Loaded += (_, _) => TxtShare.Focus();
    }

    private void OnCopyClick(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(_shareText);
            LabStatus.Text = "已复制，发给队友吧";
        }
        catch (Exception ex)
        {
            LabStatus.SetResourceReference(TextBlock.ForegroundProperty, "Status.Danger");
            LabStatus.Text = $"复制失败：{ex.Message}";
            Log.Warn($"复制分享文本失败：{ex.Message}");
        }
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();
}
