using System.Windows.Controls;

namespace StardewLauncher.App.Controls;

/// <summary>
/// 内容页基类。页面进入 / 离开时由外壳调用，页面自身可在此刷新数据。
/// </summary>
public class LauncherPage : UserControl
{
    public virtual void OnEnter()
    {
    }

    public virtual void OnLeave()
    {
    }

    /// <summary>
    /// 页面内部可切换的子视图数量（比如子标签页）。默认 1，表示没有子视图。
    /// 只给自检用：折叠起来的子视图不会被 WPF 布局，不切出来就查不出重叠。
    /// </summary>
    public virtual int SubViewCount => 1;

    /// <summary>切换到第 index 个子视图。只给自检用。</summary>
    public virtual void SelectSubView(int index)
    {
    }
}
