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
}
