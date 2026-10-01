using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;
using StardewLauncher.App.Controls;
using StardewLauncher.App.Controls.Svg;
using StardewLauncher.App.Views;
using StardewLauncher.Core.App;
using StardewLauncher.Core.Games;
using StardewLauncher.Core.Instances;
using StardewLauncher.Core.Logging;

namespace StardewLauncher.App.Windows;

/// <summary>
/// 首次运行配置向导：欢迎 → 选游戏目录 → 检查 SMAPI → 建第一个实例。
/// 只在「从没跑过、一个实例都没有、设置里也没记游戏目录」时才弹（判定在 MainWindow），
/// 老用户也可以从「设置 · 游戏与实例」里主动重跑一遍。
/// </summary>
public partial class ConfigWizardWindow : LauncherWindow
{
    private const string FallbackInstanceName = "我的星露谷";

    private readonly List<(Border Row, Border Dot, string Directory)> _candidateRows = [];

    private int _step = 1;
    private bool _busy;
    private string _selectedDirectory = string.Empty;
    private StardewInstall? _selectedInstall;
    private InstanceKind _kind = InstanceKind.Modded;

    public ConfigWizardWindow()
    {
        InitializeComponent();
        Log.SetModule("向导");

        Loaded += (_, _) => SwitchStep(1);
    }

    // ————— 步骤切换 —————

    /// <summary>切到某一步：只显示对应内容，并就地刷新这一步要用到的数据。</summary>
    private void SwitchStep(int step)
    {
        if (step is < 1 or > 4) step = 1;
        _step = step;

        StepWelcome.Visibility = step == 1 ? Visibility.Visible : Visibility.Collapsed;
        StepDirectory.Visibility = step == 2 ? Visibility.Visible : Visibility.Collapsed;
        StepSmapi.Visibility = step == 3 ? Visibility.Visible : Visibility.Collapsed;
        StepCreate.Visibility = step == 4 ? Visibility.Visible : Visibility.Collapsed;

        BarNotice.Visibility = Visibility.Collapsed;

        UpdateStepIndicator();

        BtnPrev.Visibility = step == 1 ? Visibility.Collapsed : Visibility.Visible;
        BtnNext.Content = step == 4 ? "完成" : "下一步";

        switch (step)
        {
            case 2:
                // 每次进来都重新探测：用户很可能在向导开着的时候去外面把游戏装上了
                RefreshCandidates();
                break;

            case 3:
                RefreshSmapiStep();
                BtnNext.IsEnabled = _selectedInstall is not null;
                break;

            case 4:
                PrepareCreateStep();
                BtnNext.IsEnabled = _selectedInstall is not null;
                break;

            default:
                BtnNext.IsEnabled = true;
                break;
        }
    }

    private void UpdateStepIndicator()
    {
        Border[] dots = [Dot1, Dot2, Dot3, Dot4];
        TextBlock[] numbers = [DotText1, DotText2, DotText3, DotText4];
        TextBlock[] labels = [LabStep1, LabStep2, LabStep3, LabStep4];

        for (var i = 0; i < dots.Length; i++)
        {
            var active = i + 1 == _step;

            dots[i].SetResourceReference(Border.BackgroundProperty, active ? "Accent.Base" : "Border.Default");
            numbers[i].SetResourceReference(TextBlock.ForegroundProperty, active ? "Text.OnAccent" : "Text.Secondary");
            labels[i].SetResourceReference(TextBlock.ForegroundProperty, active ? "Accent.Base" : "Text.Tertiary");
            labels[i].FontWeight = active ? FontWeights.SemiBold : FontWeights.Normal;
        }
    }

    private void OnNextClick(object sender, RoutedEventArgs e)
    {
        if (_busy) return;

        if (_step == 4)
        {
            CreateInstance();
            return;
        }

        SwitchStep(_step + 1);
    }

    private void OnPrevClick(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        SwitchStep(_step - 1);
    }

    // ————— 第 2 步：选游戏目录 —————

    private void RefreshCandidates()
    {
        PanCandidates.Children.Clear();
        _candidateRows.Clear();

        IReadOnlyList<GameCandidate> candidates;
        try
        {
            candidates = GameLocator.FindAll(null);
        }
        catch (Exception ex)
        {
            // 探测失败不该让向导整个卡住，退化成「只能手动选」即可
            Log.Error("向导探测游戏目录失败", ex);
            candidates = [];
        }

        LabNoCandidate.Visibility = candidates.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        foreach (var candidate in candidates)
            PanCandidates.Children.Add(BuildCandidateRow(candidate));

        ApplyCandidateStates();
        ValidateSelectedDirectory();
    }

    /// <summary>把一个候选目录做成一整行可点选的卡片，左侧圆点表示选中态。</summary>
    private Border BuildCandidateRow(GameCandidate candidate)
    {
        var dirText = new TextBlock
        {
            Text = candidate.Directory,
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap
        };
        dirText.SetResourceReference(TextBlock.ForegroundProperty, "Text.Primary");

        var sourceText = new TextBlock
        {
            Text = $"来源：{candidate.Source}",
            FontSize = 11,
            Margin = new Thickness(0, 2, 0, 0)
        };
        sourceText.SetResourceReference(TextBlock.ForegroundProperty, "Text.Secondary");

        var texts = new StackPanel();
        texts.Children.Add(dirText);
        texts.Children.Add(sourceText);

        var dot = new Border
        {
            Width = 16,
            Height = 16,
            Margin = new Thickness(0, 1, 10, 0),
            VerticalAlignment = VerticalAlignment.Top,
            BorderThickness = new Thickness(1),
            CornerRadius = (CornerRadius)FindResource("Radius.Pill")
        };

        var layout = new Grid();
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(dot, 0);
        Grid.SetColumn(texts, 1);
        layout.Children.Add(dot);
        layout.Children.Add(texts);

        var row = new Border
        {
            Padding = new Thickness(12, 8, 12, 8),
            Margin = new Thickness(0, 0, 0, 6),
            BorderThickness = new Thickness(1),
            CornerRadius = (CornerRadius)FindResource("Radius.Item"),
            Cursor = Cursors.Hand,
            Tag = candidate.Directory,
            Child = layout
        };

        row.MouseLeftButtonUp += OnCandidateClick;

        _candidateRows.Add((row, dot, candidate.Directory));
        return row;
    }

    private void ApplyCandidateStates()
    {
        foreach (var (row, dot, directory) in _candidateRows)
        {
            var selected = string.Equals(directory, _selectedDirectory, StringComparison.OrdinalIgnoreCase);

            row.SetResourceReference(Border.BackgroundProperty, selected ? "Accent.Faint" : "Surface.Card");
            row.SetResourceReference(Border.BorderBrushProperty, selected ? "Accent.Base" : "Border.Default");
            dot.SetResourceReference(Border.BackgroundProperty, selected ? "Accent.Base" : "Common.Transparent");
            dot.SetResourceReference(Border.BorderBrushProperty, selected ? "Accent.Base" : "Border.Strong");
        }
    }

    private void OnCandidateClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Border { Tag: string directory }) return;
        SelectDirectory(directory);
    }

    private void OnManualDirClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "选择星露谷游戏目录",
            Multiselect = false
        };

        if (!string.IsNullOrWhiteSpace(_selectedDirectory)) dialog.InitialDirectory = _selectedDirectory;

        if (dialog.ShowDialog(this) == true && !string.IsNullOrWhiteSpace(dialog.FolderName))
            SelectDirectory(dialog.FolderName);
    }

    private void SelectDirectory(string directory)
    {
        _selectedDirectory = GameLocator.NormalizePath(directory);

        ApplyCandidateStates();
        ValidateSelectedDirectory();
    }

    /// <summary>校验当前选中的目录是不是星露谷，并决定「下一步」能不能点。</summary>
    private void ValidateSelectedDirectory()
    {
        _selectedInstall = null;
        LabSelectedDir.Text = string.IsNullOrWhiteSpace(_selectedDirectory) ? "尚未选择目录" : _selectedDirectory;

        if (string.IsNullOrWhiteSpace(_selectedDirectory))
        {
            LabValidateResult.Text = "先选一个目录，这里会显示校验结果。";
            LabValidateResult.SetResourceReference(TextBlock.ForegroundProperty, "Text.Tertiary");
            BtnNext.IsEnabled = false;
            return;
        }

        if (StardewInstall.TryCreate(_selectedDirectory, out var install) && install is not null)
        {
            _selectedInstall = install;

            var text = $"校验通过：{Path.GetFileName(install.Executable)}，游戏版本 {install.GameVersion ?? "未知"}";
            text += install.HasSmapi
                ? $"，已安装 SMAPI {install.SmapiVersion ?? "未知"}"
                : "，未安装 SMAPI";

            LabValidateResult.Text = text;
            LabValidateResult.SetResourceReference(TextBlock.ForegroundProperty, "Status.Success");
            BtnNext.IsEnabled = true;
            return;
        }

        LabValidateResult.Text = "校验失败：该目录下没有 Stardew Valley.exe / StardewValley.exe，请确认选的是游戏安装根目录。";
        LabValidateResult.SetResourceReference(TextBlock.ForegroundProperty, "Status.Danger");
        BtnNext.IsEnabled = false;
    }

    // ————— 第 3 步：检查 SMAPI —————

    private void RefreshSmapiStep()
    {
        var install = _selectedInstall;

        if (install is null)
        {
            IconSmapiState.Icon = "lucide/triangle-alert";
            IconSmapiState.SetResourceReference(SvgIcon.IconBrushProperty, "Status.Danger");
            LabSmapiState.Text = "没有可检查的游戏目录";
            LabSmapiState.SetResourceReference(TextBlock.ForegroundProperty, "Status.Danger");
            LabSmapiDetail.Text = "请回到上一步选择一个有效的游戏目录。";
            LabSmapiDetail.SetResourceReference(TextBlock.ForegroundProperty, "Text.Secondary");
            return;
        }

        if (install.HasSmapi)
        {
            IconSmapiState.Icon = "lucide/circle-check";
            IconSmapiState.SetResourceReference(SvgIcon.IconBrushProperty, "Status.Success");
            LabSmapiState.Text = $"已安装 SMAPI {install.SmapiVersion ?? "未知"}";
            LabSmapiState.SetResourceReference(TextBlock.ForegroundProperty, "Status.Success");
            LabSmapiDetail.Text = "很好，Mod 端实例现在就能正常加载 Mod，直接下一步即可。";
            BarSmapiState.SetResourceReference(Border.BackgroundProperty, "Surface.Sunken");
        }
        else
        {
            IconSmapiState.Icon = "lucide/triangle-alert";
            IconSmapiState.SetResourceReference(SvgIcon.IconBrushProperty, "Status.Warn");
            LabSmapiState.Text = "未检测到 SMAPI";
            LabSmapiState.SetResourceReference(TextBlock.ForegroundProperty, "Status.Warn");
            LabSmapiDetail.Text = "Mod 端实例需要 SMAPI 才能加载 Mod。你可以先建好实例，之后到「设置 · 游戏与实例」或启动页用 SMAPI 安装功能装上；也可以现在先建一个原版实例。";
            BarSmapiState.SetResourceReference(Border.BackgroundProperty, "Status.WarnSoft");
        }

        LabSmapiDetail.SetResourceReference(TextBlock.ForegroundProperty, "Text.Secondary");
        SetKind(_kind);
    }

    private void OnKindModdedClick(object sender, RoutedEventArgs e) => SetKind(InstanceKind.Modded);

    private void OnKindVanillaClick(object sender, RoutedEventArgs e) => SetKind(InstanceKind.Vanilla);

    /// <summary>切换要建的实例类型：选中项用实心配色；Mod 端缺 SMAPI 时给一条提醒。</summary>
    private void SetKind(InstanceKind kind)
    {
        _kind = kind;

        BtnKindModded.Tone = kind == InstanceKind.Modded ? ButtonTone.Solid : ButtonTone.Outline;
        BtnKindVanilla.Tone = kind == InstanceKind.Vanilla ? ButtonTone.Solid : ButtonTone.Outline;

        var missingSmapi = _selectedInstall is not null && !_selectedInstall.HasSmapi;

        if (kind == InstanceKind.Modded && missingSmapi)
        {
            LabKindHint.Text = "提醒：这个目录还没装 SMAPI，Mod 端实例要等 SMAPI 装好后才能加载 Mod。仍然可以继续，之后补装即可。";
            LabKindHint.SetResourceReference(TextBlock.ForegroundProperty, "Status.Warn");
            LabKindHint.Visibility = Visibility.Visible;
        }
        else
        {
            LabKindHint.Visibility = Visibility.Collapsed;
        }
    }

    // ————— 第 4 步：创建实例 —————

    private void PrepareCreateStep()
    {
        if (string.IsNullOrWhiteSpace(TxtName.Text)) TxtName.Text = DefaultInstanceName();

        var kindText = _kind == InstanceKind.Vanilla ? "原版" : "Mod 端";
        var smapiText = _selectedInstall is null
            ? "游戏目录信息缺失"
            : _selectedInstall.HasSmapi
                ? $"SMAPI {_selectedInstall.SmapiVersion ?? "未知"}"
                : "未安装 SMAPI";

        LabCreateSummary.Text =
            $"游戏目录：{_selectedDirectory}\n" +
            $"实例类型：{kindText} · 游戏版本 {_selectedInstall?.GameVersion ?? "未知"} · {smapiText}";
    }

    private string DefaultInstanceName()
    {
        if (string.IsNullOrWhiteSpace(_selectedDirectory)) return FallbackInstanceName;

        try
        {
            var name = new DirectoryInfo(_selectedDirectory).Name;
            return string.IsNullOrWhiteSpace(name) ? FallbackInstanceName : name;
        }
        catch
        {
            return FallbackInstanceName;
        }
    }

    private void CreateInstance()
    {
        if (_busy) return;

        if (_selectedInstall is null || string.IsNullOrWhiteSpace(_selectedDirectory))
        {
            ShowNotice("还没有选定有效的游戏目录，请回到上一步重新选择。", true);
            return;
        }

        try
        {
            _busy = true;

            var name = string.IsNullOrWhiteSpace(TxtName.Text) ? DefaultInstanceName() : TxtName.Text.Trim();
            var note = TxtNote.Text?.Trim() ?? string.Empty;

            var instance = InstanceStore.Create(name, _selectedDirectory, note, _kind);

            SettingsStore.Current.FirstRunCompleted = true;
            SettingsStore.Save();

            Log.Info($"首次运行向导完成，已创建实例「{instance.Name}」（{instance.KindText}）：{instance.GameDir}");

            Dialogs.Info(this, $"第一个实例「{instance.Name}」已建好，可以点「启动」开始了。", "配置完成");
            Close();
        }
        catch (Exception ex)
        {
            Log.Error("首次运行向导创建实例失败", ex);
            Dialogs.Error(this, $"创建实例失败：{ex.Message}");
        }
        finally
        {
            _busy = false;
        }
    }

    // ————— 跳过与提示 —————

    private void OnSkipClick(object sender, RoutedEventArgs e)
    {
        if (_busy) return;

        if (!Dialogs.Confirm(this,
                "确定跳过配置向导吗？\n\n之后可以随时到「设置 · 游戏与实例」里点「重新运行配置向导」再跑一遍。",
                "跳过向导", "跳过", "继续配置"))
            return;

        SettingsStore.Current.FirstRunCompleted = true;
        SettingsStore.Save();

        Log.Info("用户跳过了首次运行配置向导");
        Close();
    }

    private void ShowNotice(string text, bool warn = false)
    {
        LabNotice.Text = text;
        BarNotice.Background = (System.Windows.Media.Brush)FindResource(warn ? "Status.WarnSoft" : "Accent.Faint");
        IconNotice.IconBrush = (System.Windows.Media.Brush)FindResource(warn ? "Status.Warn" : "Accent.Base");
        IconNotice.Icon = warn ? "lucide/triangle-alert" : "lucide/info";
        BarNotice.Visibility = Visibility.Visible;
    }

    private void OnDismissNoticeClick(object sender, RoutedEventArgs e)
        => BarNotice.Visibility = Visibility.Collapsed;
}
