using System.Windows;
using StardewLauncher.App.Views;

namespace StardewLauncher.App.Windows;

/// <summary>官网 Wiki 的独立窗口：只是 <see cref="WikiView"/> 的宿主。</summary>
public partial class WikiWindow : LauncherWindow
{
    private readonly WikiView _view;

    public WikiWindow()
    {
        InitializeComponent();

        _view = new WikiView();
        ViewHost.Child = _view;
    }

    private void OnWindowClosed(object? sender, EventArgs e) => _view.Shutdown();
}
