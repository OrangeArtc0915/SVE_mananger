using System.Windows;
using StardewLauncher.App.Controls;
using StardewLauncher.Core.Logging;

namespace StardewLauncher.App.Pages;

/// <summary>
/// 工具箱：把零散的小工具收在一页里，点进去在页内切换到具体工具。
/// 加工具时在 XAML 里补一张卡片、补一个子视图，再在下面的编号与映射里登记一次即可。
/// </summary>
public partial class PageToolbox : LauncherPage
{
    private const int SubTools = 0;
    private const int SubSaveManager = 1;
    private const int SubHealth = 2;
    private const int SubConflict = 3;
    private const int SubSmapiLog = 4;
    private const int SubGameRestore = 5;

    private int _subView = SubTools;

    public PageToolbox()
    {
        InitializeComponent();
        Log.SetModule("工具箱");
    }

    public override void OnEnter()
    {
        // 停在工具里时重新进入页面，按最新状态刷新一次
        ActivateCurrent();
    }

    /// <summary>工具列表 + 五个工具的子视图。只给自检用。</summary>
    public override int SubViewCount => 6;

    public override void SelectSubView(int index) => SwitchSubView(index);

    private void OnOpenToolClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string toolId }) return;

        SwitchSubView(toolId switch
        {
            "saves" => SubSaveManager,
            "health" => SubHealth,
            "conflict" => SubConflict,
            "log" => SubSmapiLog,
            "restore" => SubGameRestore,
            _ => SubTools
        });
    }

    private void OnBackClick(object sender, RoutedEventArgs e) => SwitchSubView(SubTools);

    private void SwitchSubView(int index)
    {
        _subView = index is >= SubSaveManager and <= SubGameRestore ? index : SubTools;

        var listing = _subView == SubTools;

        PanTools.Visibility = listing ? Visibility.Visible : Visibility.Collapsed;
        SaveManager.Visibility = _subView == SubSaveManager ? Visibility.Visible : Visibility.Collapsed;
        HealthCheck.Visibility = _subView == SubHealth ? Visibility.Visible : Visibility.Collapsed;
        ModConflict.Visibility = _subView == SubConflict ? Visibility.Visible : Visibility.Collapsed;
        SmapiLog.Visibility = _subView == SubSmapiLog ? Visibility.Visible : Visibility.Collapsed;
        GameRestore.Visibility = _subView == SubGameRestore ? Visibility.Visible : Visibility.Collapsed;

        BtnBack.Visibility = listing ? Visibility.Collapsed : Visibility.Visible;

        LabTitle.Text = listing ? "工具箱" : $"工具箱 · {TitleOf(_subView)}";
        LabSubtitle.Text = listing
            ? "不属于日常流程、但偶尔要用的小工具都收在这里"
            : SubtitleOf(_subView);

        ActivateCurrent();
    }

    /// <summary>切进工具（或重新进入页面）时让当前工具刷新一次。</summary>
    private void ActivateCurrent()
    {
        switch (_subView)
        {
            case SubSaveManager:
                SaveManager.Activate();
                break;

            case SubHealth:
                HealthCheck.Activate();
                break;

            case SubConflict:
                ModConflict.Activate();
                break;

            case SubSmapiLog:
                SmapiLog.Activate();
                break;

            case SubGameRestore:
                GameRestore.Activate();
                break;
        }
    }

    private static string TitleOf(int subView) => subView switch
    {
        SubSaveManager => "存档管理",
        SubHealth => "环境体检",
        SubConflict => "Mod 冲突检查",
        SubSmapiLog => "SMAPI 日志分析",
        SubGameRestore => "原版文件还原",
        _ => string.Empty
    };

    private static string SubtitleOf(int subView) => subView switch
    {
        SubSaveManager => "读游戏自己的存档目录，摘要、备份与回滚都在这里；启动器不会删改你的存档",
        SubHealth => "把启动器、游戏目录、SMAPI、Mod 与磁盘扫一遍，结论能复制或导成反馈包",
        SubConflict => "只看磁盘上已有的信息：重复 ID、加载不了的 Mod、放错层级的目录",
        SubSmapiLog => "读最近一份 SMAPI 日志，按来源找出在报错的 Mod，关键的原文可以整段复制",
        SubGameRestore => "覆盖前的备份就留在原文件旁边，这里按索引判断哪些能确定是原版，并支持还原",
        _ => string.Empty
    };
}
