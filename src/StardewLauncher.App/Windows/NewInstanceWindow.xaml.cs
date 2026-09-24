using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using StardewLauncher.App.Controls;
using StardewLauncher.Core.Games;
using StardewLauncher.Core.Instances;

namespace StardewLauncher.App.Windows;

/// <summary>新建实例对话框：选实例类型、手动指定游戏目录（实时校验）、填名称与备注。</summary>
public partial class NewInstanceWindow : LauncherWindow
{
    private const string DefaultModdedName = "我的实例";
    private const string DefaultVanillaName = "原版";

    public NewInstanceWindow()
    {
        InitializeComponent();

        // 游戏目录一律留空，由用户手动选择，不预填任何探测结果
        SetKind(InstanceKind.Modded);
    }

    /// <summary>
    /// 编辑模式：预填现有实例，标题与确认按钮改成「保存」。
    /// 先切类型再填文字——SetKind 会在名字等于默认名时顺手改名，倒过来会把用户的名字改掉。
    /// </summary>
    public NewInstanceWindow(Instance existing) : this()
    {
        Title = "实例设置";
        BtnCreate.Content = "保存";

        SetKind(existing.Kind);

        TxtName.Text = existing.Name;
        TxtNote.Text = existing.Note;
        TxtGameDir.Text = existing.GameDir;

        ValidateDirectory();
    }

    /// <summary>用户填写的实例名称，点「创建」后有效。</summary>
    public string InstanceName { get; private set; } = string.Empty;

    /// <summary>用户选定的游戏目录，点「创建」后有效。</summary>
    public string GameDirectory { get; private set; } = string.Empty;

    /// <summary>用户填写的备注，点「创建」后有效。</summary>
    public string InstanceNote { get; private set; } = string.Empty;

    /// <summary>用户选定的实例类型，默认 Mod 端。</summary>
    public InstanceKind Kind { get; private set; } = InstanceKind.Modded;

    private void OnKindVanillaClick(object sender, RoutedEventArgs e) => SetKind(InstanceKind.Vanilla);

    private void OnKindModdedClick(object sender, RoutedEventArgs e) => SetKind(InstanceKind.Modded);

    /// <summary>切换实例类型：选中项用实心配色，并在用户没改过名字时同步默认名称。</summary>
    private void SetKind(InstanceKind kind)
    {
        Kind = kind;

        BtnKindVanilla.Tone = kind == InstanceKind.Vanilla ? ButtonTone.Solid : ButtonTone.Outline;
        BtnKindModded.Tone = kind == InstanceKind.Modded ? ButtonTone.Solid : ButtonTone.Outline;

        if (kind == InstanceKind.Vanilla)
        {
            if (TxtName.Text == DefaultModdedName) TxtName.Text = DefaultVanillaName;
        }
        else if (TxtName.Text == DefaultVanillaName)
        {
            TxtName.Text = DefaultModdedName;
        }

        ValidateDirectory();
    }

    private void OnGameDirChanged(object sender, TextChangedEventArgs e) => ValidateDirectory();

    private void OnBrowseClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "选择星露谷游戏目录",
            Multiselect = false
        };

        var current = TxtGameDir.Text?.Trim();
        if (!string.IsNullOrEmpty(current)) dialog.InitialDirectory = current;

        if (dialog.ShowDialog(this) == true) TxtGameDir.Text = dialog.FolderName;
    }

    /// <summary>列出自动检测到的候选目录，只有点某一条才会填入，不自动选择。</summary>
    private void OnDetectClick(object sender, RoutedEventArgs e)
    {
        PanCandidates.Children.Clear();

        var candidates = GameLocator.FindAll(null);

        if (candidates.Count == 0)
        {
            var hint = new TextBlock
            {
                FontSize = 11.5,
                TextWrapping = TextWrapping.Wrap,
                Text = "没有自动检测到游戏目录，请点「浏览…」手动选择。"
            };
            hint.SetResourceReference(TextBlock.ForegroundProperty, "Text.Tertiary");

            PanCandidates.Children.Add(hint);
            PanCandidates.Visibility = Visibility.Visible;
            return;
        }

        foreach (var candidate in candidates)
        {
            var button = new OutlineButton
            {
                HorizontalAlignment = HorizontalAlignment.Left,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Padding = new Thickness(0, 3, 0, 3),
                FontSize = 11.5,
                Tone = ButtonTone.Plain,
                Content = $"{candidate.Directory}（{candidate.Source}）",
                Tag = candidate.Directory
            };

            button.Click += OnCandidateClick;
            PanCandidates.Children.Add(button);
        }

        PanCandidates.Visibility = Visibility.Visible;
    }

    private void OnCandidateClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string directory }) return;

        TxtGameDir.Text = directory;
        PanCandidates.Visibility = Visibility.Collapsed;
    }

    private void OnCreateClick(object sender, RoutedEventArgs e)
    {
        var directory = TxtGameDir.Text?.Trim() ?? string.Empty;
        if (!GameLocator.IsGameDirectory(directory)) return;

        var fallback = Kind == InstanceKind.Vanilla ? DefaultVanillaName : DefaultModdedName;

        InstanceName = string.IsNullOrWhiteSpace(TxtName.Text) ? fallback : TxtName.Text.Trim();
        GameDirectory = GameLocator.NormalizePath(directory);
        InstanceNote = TxtNote.Text?.Trim() ?? string.Empty;

        DialogResult = true;
    }

    private void OnCancelClick(object sender, RoutedEventArgs e) => DialogResult = false;

    /// <summary>校验游戏目录，并把结果写进下方小字、决定「创建」是否可用。</summary>
    private void ValidateDirectory()
    {
        if (LabDirState is null || LabSmapiHint is null) return;

        var directory = TxtGameDir.Text?.Trim() ?? string.Empty;
        var install = StardewInstall.TryCreate(directory, out var created) ? created : null;

        if (install is null)
        {
            LabDirState.Text = "该目录下没有 Stardew Valley.exe / StardewValley.exe";
            LabDirState.SetResourceReference(TextBlock.ForegroundProperty, "Status.Danger");
            LabSmapiHint.Visibility = Visibility.Collapsed;
            BtnCreate.IsEnabled = false;
            return;
        }

        var text = $"已找到 {Path.GetFileName(install.Executable)}，游戏版本 {install.GameVersion ?? "未知"}";
        if (install.HasSmapi) text += $"，已安装 SMAPI {install.SmapiVersion ?? "未知"}";

        LabDirState.Text = text;
        LabDirState.SetResourceReference(TextBlock.ForegroundProperty, "Status.Success");
        BtnCreate.IsEnabled = true;

        LabSmapiHint.Visibility = Kind == InstanceKind.Modded && !install.HasSmapi
            ? Visibility.Visible
            : Visibility.Collapsed;
    }
}
