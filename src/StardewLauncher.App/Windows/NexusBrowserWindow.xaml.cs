using System.Windows;
using StardewLauncher.App.Views;

namespace StardewLauncher.App.Windows;

/// <summary>
/// 「在线下载 Mod」独立窗口：只是 <see cref="NexusBrowserView"/> 的宿主。
/// 资源中心页内嵌同一个视图；这里保留窗口形态，供 Mod 管理的「补齐依赖」站内搜索与设置页入口使用。
/// </summary>
public partial class NexusBrowserWindow : LauncherWindow
{
    private readonly NexusBrowserView _view;

    /// <summary>不指定初始地址时打开默认站点（Nexus Mods）；指定时直接导航过去（例如站内搜索页）。</summary>
    public NexusBrowserWindow(string? initialUrl = null)
    {
        InitializeComponent();

        _view = new NexusBrowserView(initialUrl);
        ViewHost.Child = _view;
    }

    private void OnWindowClosed(object? sender, EventArgs e) => _view.Shutdown();
}
