using System.Windows;
using StardewLauncher.App.Controls;
using StardewLauncher.Core.Logging;

namespace StardewLauncher.App.Pages;

/// <summary>
/// 工具箱：把零散的小工具收在一页里，点进去在页内切换到具体工具。
/// 目前只有「存档管理」，后续加工具时在 XAML 里补一张卡片即可。
/// </summary>
public partial class PageToolbox : LauncherPage
{
    private const int SubTools = 0;
    private const int SubSaveManager = 1;

    private int _subView = SubTools;

    public PageToolbox()
    {
        InitializeComponent();
        Log.SetModule("工具箱");
    }

    public override void OnEnter()
    {
        // 停在工具里时重新进入页面，按最新存档刷新一次
        if (_subView == SubSaveManager) SaveManager.Activate();
    }

    /// <summary>两套子视图：工具列表与具体的工具内容。只给自检用。</summary>
    public override int SubViewCount => 2;

    public override void SelectSubView(int index) => SwitchSubView(index);

    private void OnOpenToolClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string toolId }) return;

        if (string.Equals(toolId, "saves", StringComparison.Ordinal)) SwitchSubView(SubSaveManager);
    }

    private void OnBackClick(object sender, RoutedEventArgs e) => SwitchSubView(SubTools);

    private void SwitchSubView(int index)
    {
        _subView = index == SubSaveManager ? SubSaveManager : SubTools;

        var inTool = _subView == SubSaveManager;

        PanTools.Visibility = inTool ? Visibility.Collapsed : Visibility.Visible;
        SaveManager.Visibility = inTool ? Visibility.Visible : Visibility.Collapsed;
        BtnBack.Visibility = inTool ? Visibility.Visible : Visibility.Collapsed;

        LabTitle.Text = inTool ? "工具箱 · 存档管理" : "工具箱";
        LabSubtitle.Text = inTool
            ? "读游戏自己的存档目录，摘要、备份与回滚都在这里；启动器不会删改你的存档"
            : "不属于日常流程、但偶尔要用的小工具都收在这里";

        if (inTool) SaveManager.Activate();
    }
}
