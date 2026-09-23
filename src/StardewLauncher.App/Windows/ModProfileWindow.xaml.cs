using System.IO;
using System.Windows;
using Microsoft.Win32;
using StardewLauncher.App.Controls;
using StardewLauncher.Core.Mods;
using StardewLauncher.Core.Logging;

namespace StardewLauncher.App.Windows;

/// <summary>
/// Mod 配置档管理：把当前启停组合存成一套档（如「美化包」「剧情包」），需要时一键切换。
/// 只改名不搬文件，与 ModEnabler 同一套机制。
/// </summary>
public partial class ModProfileWindow : Window
{
    private readonly string _modsDirectory;
    private bool _busy;

    /// <summary>是否应用过配置档（调用方据此决定要不要重扫 Mod 列表）。</summary>
    public bool Applied { get; private set; }

    public ModProfileWindow(string modsDirectory)
    {
        InitializeComponent();

        _modsDirectory = modsDirectory ?? string.Empty;

        LabPath.Text = string.IsNullOrWhiteSpace(_modsDirectory)
            ? "还没有可用的 Mods 目录"
            : $"当前 Mods 目录：{_modsDirectory}";

        Loaded += (_, _) => Refresh();
        ModProfileStore.Changed += OnStoreChanged;
        Closed += (_, _) => ModProfileStore.Changed -= OnStoreChanged;
    }

    private void OnStoreChanged()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(Refresh);
            return;
        }

        Refresh();
    }

    // ————— 列表 —————

    private void Refresh()
    {
        var profiles = ModProfileStore.ForDirectory(_modsDirectory);
        PanProfiles.ItemsSource = profiles;

        var empty = profiles.Count == 0;
        LabEmpty.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        LabEmpty.Text = "还没有配置档。\n\n把当前 Mod 的启停状态调成你想要的样子，再点右上角「保存当前状态」，以后就能一键切回来。";
    }

    // ————— 新建 / 导入 —————

    private async void OnSaveCurrentClick(object sender, RoutedEventArgs e)
    {
        if (_busy) return;

        if (string.IsNullOrWhiteSpace(_modsDirectory) || !Directory.Exists(_modsDirectory))
        {
            ShowNotice("还没有可用的 Mods 目录，先去「游戏实例」建一个 Mod 端实例。", true);
            return;
        }

        var name = Prompt.Show(this, "把当前的 Mod 启停状态存成一份配置档", "配置档名字", $"配置档 {DateTime.Now:MM-dd HH:mm}");
        if (name is null) return;

        _busy = true;
        SetButtonsEnabled(false);

        try
        {
            var scan = await Task.Run(() => ModScanner.Scan(_modsDirectory));

            if (scan.Mods.Count == 0)
            {
                ShowNotice("这个 Mods 目录里没有扫到 Mod，没什么可保存的。", true);
                return;
            }

            var profile = ModProfileStore.Create(name, "", _modsDirectory, scan.Mods);
            ShowNotice($"已保存配置档「{profile.Name}」（{profile.MetaText}）。");
        }
        catch (Exception ex)
        {
            Log.Error("保存配置档失败", ex);
            ShowNotice($"保存失败：{ex.Message}", true);
        }
        finally
        {
            _busy = false;
            SetButtonsEnabled(true);
        }
    }

    private void OnImportClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "选择配置档文件",
            Filter = "配置档 (*.json)|*.json|所有文件 (*.*)|*.*",
            Multiselect = false,
            CheckFileExists = true
        };

        var owner = Window.GetWindow(this);
        var confirmed = owner is null ? dialog.ShowDialog() : dialog.ShowDialog(owner);
        if (confirmed != true) return;

        var profile = ModProfileStore.Import(dialog.FileName, _modsDirectory, out var error);

        ShowNotice(profile is null
            ? $"导入失败：{error}"
            : $"已导入配置档「{profile.Name}」（{profile.MetaText}）。", profile is null);
    }

    // ————— 应用 / 覆盖 —————

    private async void OnApplyClick(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        if (sender is not FrameworkElement { Tag: ModProfile profile }) return;

        var answer = MessageBox.Show(this,
            $"应用配置档「{profile.Name}」？\n\n会按这份档逐个启用 / 禁用 Mod（{profile.MetaText}），"
            + "文件名只会加 / 去开头的点，不会删改任何 Mod 文件。\n\n如果游戏正在运行，改完请重开游戏才会生效。",
            "应用配置档", MessageBoxButton.OKCancel, MessageBoxImage.Question);

        if (answer != MessageBoxResult.OK) return;

        _busy = true;
        SetButtonsEnabled(false);

        try
        {
            var result = await Task.Run(() =>
            {
                var scan = ModScanner.Scan(_modsDirectory);
                return ModProfileStore.Apply(profile, scan.Mods);
            });

            Applied = true;

            var text = $"配置档「{profile.Name}」已应用：{result.Summary}";

            if (result.Missing.Count > 0)
                text += $"\n不在 Mods 目录里的：{string.Join("、", result.Missing.Take(5))}"
                        + (result.Missing.Count > 5 ? " 等" : "");

            if (result.Errors.Count > 0)
                text += $"\n失败：{string.Join("；", result.Errors.Take(3))}";

            ShowNotice(text, !result.Ok);
        }
        catch (Exception ex)
        {
            Log.Error("应用配置档失败", ex);
            ShowNotice($"应用失败：{ex.Message}", true);
        }
        finally
        {
            _busy = false;
            SetButtonsEnabled(true);
        }
    }

    private async void OnOverwriteClick(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        if (sender is not FrameworkElement { Tag: ModProfile profile }) return;

        var answer = MessageBox.Show(this,
            $"用当前 Mod 的启停状态覆盖配置档「{profile.Name}」？\n\n覆盖后原来的记录就找不回来了。",
            "覆盖配置档", MessageBoxButton.OKCancel, MessageBoxImage.Question);

        if (answer != MessageBoxResult.OK) return;

        _busy = true;
        SetButtonsEnabled(false);

        try
        {
            var scan = await Task.Run(() => ModScanner.Scan(_modsDirectory));
            ModProfileStore.Overwrite(profile, scan.Mods);
            ShowNotice($"配置档「{profile.Name}」已更新（{profile.MetaText}）。");
        }
        catch (Exception ex)
        {
            Log.Error("覆盖配置档失败", ex);
            ShowNotice($"更新失败：{ex.Message}", true);
        }
        finally
        {
            _busy = false;
            SetButtonsEnabled(true);
        }
    }

    // ————— 重命名 / 导出 / 删除 —————

    private void OnRenameClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: ModProfile profile }) return;

        var name = Prompt.Show(this, $"给配置档「{profile.Name}」改名", "配置档名字", profile.Name);
        if (name is null) return;

        ModProfileStore.Rename(profile, name, profile.Note);
        ShowNotice($"已改名为「{profile.Name}」。");
    }

    private void OnExportClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: ModProfile profile }) return;

        var dialog = new SaveFileDialog
        {
            Title = "导出配置档",
            Filter = "配置档 (*.json)|*.json",
            FileName = Sanitize(profile.Name) + ".json",
            AddExtension = true,
            DefaultExt = ".json"
        };

        var owner = Window.GetWindow(this);
        var confirmed = owner is null ? dialog.ShowDialog() : dialog.ShowDialog(owner);
        if (confirmed != true) return;

        var ok = ModProfileStore.Export(profile, dialog.FileName, out var error);
        ShowNotice(ok ? $"已导出到 {dialog.FileName}" : $"导出失败：{error}", !ok);
    }

    private void OnDeleteClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: ModProfile profile }) return;

        var answer = MessageBox.Show(this,
            $"删除配置档「{profile.Name}」？\n\n只删这份记录，Mod 文件不受影响。",
            "删除配置档", MessageBoxButton.OKCancel, MessageBoxImage.Warning);

        if (answer != MessageBoxResult.OK) return;

        ModProfileStore.Delete(profile);
        ShowNotice($"已删除配置档「{profile.Name}」。");
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    // ————— 提示条 —————

    private void ShowNotice(string text, bool warn = false)
    {
        LabNotice.Text = text;
        BarNotice.Background = (System.Windows.Media.Brush)FindResource(warn ? "Status.WarnSoft" : "Accent.Faint");
        IconNotice.IconBrush = (System.Windows.Media.Brush)FindResource(warn ? "Status.Warn" : "Accent.Base");
        IconNotice.Icon = warn ? "lucide/triangle-alert" : "lucide/info";
        BarNotice.Visibility = Visibility.Visible;
    }

    private void SetButtonsEnabled(bool enabled) => BtnSaveCurrent.IsEnabled = enabled;

    private static string Sanitize(string name)
    {
        foreach (var invalid in Path.GetInvalidFileNameChars())
            name = name.Replace(invalid, '_');

        return string.IsNullOrWhiteSpace(name) ? "profile" : name.Trim();
    }
}
