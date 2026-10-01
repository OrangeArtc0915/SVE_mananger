using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;
using StardewLauncher.App.Views;
using StardewLauncher.Core.App;
using StardewLauncher.Core.Instances;
using StardewLauncher.Core.Logging;
using StardewLauncher.Core.Mods;
using StardewLauncher.Core.Translate;

namespace StardewLauncher.App.Windows;

/// <summary>列表里一条用于界面绑定的翻译项。把 Mod 信息与已落盘的译文拼到一起。</summary>
public sealed class TranslationItem : INotifyPropertyChanged
{
    public TranslationItem(ModEntry entry, string key)
    {
        Entry = entry;
        Key = key;
        NameLine = $"原名：{entry.OriginalName}";

        // 描述可能为空，界面上给一句人话，别让用户以为漏了
        SourceDescription = string.IsNullOrWhiteSpace(entry.DisplayDescription)
            ? "（这个 Mod 没写描述）"
            : entry.DisplayDescription;

        ApplyFromStore();
    }

    public ModEntry Entry { get; }

    /// <summary>Mod 的稳定标识，和 ModTagStore 用同一套。</summary>
    public string Key { get; }

    public string DisplayName => Entry.DisplayName;

    public string NameLine { get; }

    public string SourceDescription { get; }

    /// <summary>最近一次翻译失败的可读原因，只用于汇总提示。</summary>
    public string LastError { get; set; } = "";

    private bool _canTranslate = true;

    /// <summary>忙的时候置为 false，把这一行的按钮一起禁掉。</summary>
    public bool CanTranslate
    {
        get => _canTranslate;
        set
        {
            if (_canTranslate == value) return;
            _canTranslate = value;
            OnPropertyChanged();
        }
    }

    private bool _hasTranslation;

    public bool HasTranslation
    {
        get => _hasTranslation;
        private set
        {
            if (_hasTranslation == value) return;
            _hasTranslation = value;
            OnPropertyChanged();
        }
    }

    private string _translationName = "";

    public string TranslationName
    {
        get => _translationName;
        private set
        {
            if (_translationName == value) return;
            _translationName = value;
            OnPropertyChanged();
        }
    }

    private string _translationDescription = "";

    public string TranslationDescription
    {
        get => _translationDescription;
        private set
        {
            if (_translationDescription == value) return;
            _translationDescription = value;
            OnPropertyChanged();
        }
    }

    private string _translatedAtText = "";

    public string TranslatedAtText
    {
        get => _translatedAtText;
        private set
        {
            if (_translatedAtText == value) return;
            _translatedAtText = value;
            OnPropertyChanged();
        }
    }

    /// <summary>按存储里的最新内容刷新译文显示（翻译后或清空后调用）。</summary>
    public void ApplyFromStore()
    {
        if (ModTranslationStore.TryGet(Key, out var entry))
        {
            TranslationName = string.IsNullOrWhiteSpace(entry.Name) ? Entry.OriginalName : entry.Name;
            TranslationDescription = string.IsNullOrWhiteSpace(entry.Description) ? "（没有描述）" : entry.Description;
            TranslatedAtText = $"翻译于 {entry.TranslatedAtText}";
            HasTranslation = true;
        }
        else
        {
            TranslationName = string.Empty;
            TranslationDescription = string.Empty;
            TranslatedAtText = string.Empty;
            HasTranslation = false;
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>
/// Mod 翻译：把 Mod 的名称与描述翻成中文，记在启动器自己这边，不动 Mod 的文件。
/// 密钥没配好时不弹错、不崩，只提示去设置页补；每翻译成功一个就立刻落盘，关窗不会丢。
/// </summary>
public partial class ModTranslationWindow : LauncherWindow
{
    /// <summary>描述截断长度：太长既费 token 又容易触发接口限制，取前 400 个字符足够看懂。</summary>
    private const int MaxDescriptionLength = 400;

    private readonly ObservableCollection<TranslationItem> _items = [];

    private bool _busy;

    public ModTranslationWindow()
    {
        InitializeComponent();
        Log.SetModule("翻译");

        PanMods.ItemsSource = _items;
        Loaded += (_, _) => Refresh();
    }

    private static string? ModsDirectory => InstanceStore.Current?.ModsDirectory;

    private void Refresh()
    {
        _items.Clear();

        var directory = ModsDirectory;
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        {
            LabEmpty.Text = "还没有可用的 Mods 目录，请先创建游戏实例。";
            LabEmpty.Visibility = Visibility.Visible;
            UpdateProviderLine();
            return;
        }

        var scan = ModScanner.Scan(directory);

        foreach (var mod in scan.Mods)
        {
            var key = ModTagStore.KeyOf(mod);
            if (string.IsNullOrWhiteSpace(key)) continue;

            _items.Add(new TranslationItem(mod, key));
        }

        LabEmpty.Text = "这个 Mods 目录里还没有 Mod。";
        LabEmpty.Visibility = _items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        UpdateProviderLine();
        Log.Info($"翻译窗口载入 {_items.Count} 个 Mod");
    }

    private void UpdateProviderLine()
    {
        var settings = SettingsStore.Current;
        LabProvider.Text = $"当前服务商：{TranslateService.ProviderDisplayName(settings.TranslateProvider)}" +
                           $" · 目标语言：{TranslateService.TargetDisplayName(settings.TranslateTargetLanguage)}";
    }

    // ————— 翻译 —————

    private async void OnTranslateOneClick(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        if (sender is not FrameworkElement { Tag: TranslationItem item }) return;

        await TranslateManyAsync([item]);
    }

    private async void OnTranslateAllClick(object sender, RoutedEventArgs e)
    {
        if (_busy) return;

        var pending = _items.Where(item => !item.HasTranslation).ToList();
        if (pending.Count == 0)
        {
            ShowNotice("所有 Mod 都已经有译文了。");
            return;
        }

        await TranslateManyAsync(pending);
    }

    /// <summary>逐个翻译一串条目。遇到「没配置好」就整体停下，提示去设置页。</summary>
    private async Task TranslateManyAsync(IReadOnlyList<TranslationItem> items)
    {
        SetBusy(true);

        var done = 0;
        var failed = 0;
        string? lastError = null;
        var misconfigured = false;

        try
        {
            foreach (var item in items)
            {
                var status = await TranslateOneAsync(item);

                if (status is TranslateStatus.NotConfigured or TranslateStatus.Disabled)
                {
                    misconfigured = true;
                    break;
                }

                if (status == TranslateStatus.Ok)
                {
                    done++;
                }
                else
                {
                    failed++;
                    lastError = item.LastError;
                }
            }
        }
        finally
        {
            SetBusy(false);
            TranslateCache.Save();
        }

        if (misconfigured)
        {
            ShowNotice("还没配置好翻译：请到「设置」页的「网络与账号」分类里选好服务商并填上密钥。", true);
            return;
        }

        if (failed == 0)
            ShowNotice($"翻译完成，共 {done} 个 Mod。");
        else
            ShowNotice($"完成 {done} 个，失败 {failed} 个（{lastError}）。", true);
    }

    /// <summary>翻译单个条目：名字与描述一起发一次请求，命中的缓存不再请求。</summary>
    private async Task<TranslateStatus> TranslateOneAsync(TranslationItem item)
    {
        item.LastError = "";

        var settings = SettingsStore.Current;
        var provider = (settings.TranslateProvider ?? "").Trim().ToLowerInvariant();
        var target = settings.TranslateTargetLanguage ?? "";

        // 名字本来就是中文就不用再翻；描述为空同样跳过
        var wantName = !TranslateService.HasChinese(item.Entry.OriginalName)
                       && !string.IsNullOrWhiteSpace(item.Entry.OriginalName);
        var description = TrimDescription(item.Entry.DisplayDescription);
        var wantDescription = !string.IsNullOrWhiteSpace(description);

        // 名字已是中文又没描述：直接记一条空翻译，免得「翻译全部」每次都重来一遍
        if (!wantName && !wantDescription)
        {
            ModTranslationStore.Put(item.Key, item.Entry.OriginalName, string.Empty);
            item.ApplyFromStore();
            return TranslateStatus.Ok;
        }

        var sources = new List<string>();
        if (wantName) sources.Add(item.Entry.OriginalName);
        if (wantDescription) sources.Add(description);

        var keys = sources.Select(text => TranslateCache.KeyOf(text, target, provider)).ToList();
        var results = new string[sources.Count];
        var pendingIndexes = new List<int>();
        var pendingTexts = new List<string>();

        for (var i = 0; i < sources.Count; i++)
        {
            if (TranslateCache.TryGet(keys[i], out var cached)) results[i] = cached;
            else
            {
                pendingIndexes.Add(i);
                pendingTexts.Add(sources[i]);
            }
        }

        if (pendingTexts.Count > 0)
        {
            var response = await TranslateService.TranslateAsync(pendingTexts);

            if (!response.IsOk)
            {
                item.LastError = response.Error ?? "翻译失败";
                return response.Status;
            }

            for (var i = 0; i < pendingIndexes.Count; i++)
            {
                var value = response.Texts[i];
                results[pendingIndexes[i]] = value;
                TranslateCache.Put(keys[pendingIndexes[i]], value);
            }
        }

        var cursor = 0;
        var translatedName = item.Entry.OriginalName;
        var translatedDescription = string.Empty;

        if (wantName) translatedName = results[cursor++];
        if (wantDescription) translatedDescription = results[cursor];

        ModTranslationStore.Put(item.Key, translatedName, translatedDescription);
        item.ApplyFromStore();

        return TranslateStatus.Ok;
    }

    /// <summary>把描述压成单行并截断，避免过长文本既费额度又容易被接口拒。</summary>
    private static string TrimDescription(string? description)
    {
        if (string.IsNullOrWhiteSpace(description)) return string.Empty;

        var text = description.ReplaceLineEndings(" ").Trim();
        return text.Length > MaxDescriptionLength ? text[..MaxDescriptionLength] : text;
    }

    // ————— 其它 —————

    private void OnRefreshClick(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        Refresh();
        ShowNotice("已重新扫描 Mods 目录。");
    }

    private void OnCopyClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: TranslationItem item }) return;
        if (!item.HasTranslation) return;

        try
        {
            var text = string.IsNullOrWhiteSpace(item.TranslationDescription)
                ? item.TranslationName
                : $"{item.TranslationName}\n{item.TranslationDescription}";

            Clipboard.SetText(text);
            ShowNotice($"已复制「{item.DisplayName}」的译文。");
        }
        catch (Exception ex)
        {
            // 剪贴板可能被别的程序占着，这不是致命问题，提示一下就行
            Log.Warn($"复制译文失败：{ex.Message}");
            ShowNotice("复制失败，剪贴板可能正被别的程序占用。", true);
        }
    }

    private void OnClearAllClick(object sender, RoutedEventArgs e)
    {
        if (_busy) return;

        if (_items.Count == 0)
        {
            ShowNotice("还没有 Mod，没什么可清的。", true);
            return;
        }

        if (!Dialogs.Confirm(this,
                "清空所有 Mod 的翻译结果？\n\n只删启动器里记的译文，Mod 自己的文件一个字都不会动。",
                "清空翻译", "清空", "算了"))
            return;

        ModTranslationStore.Clear();

        foreach (var item in _items) item.ApplyFromStore();

        ShowNotice("已清空所有翻译。");
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    private void SetBusy(bool busy)
    {
        _busy = busy;

        LabBusy.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        BtnTranslateAll.IsEnabled = !busy;
        BtnRefresh.IsEnabled = !busy;
        BtnClearAll.IsEnabled = !busy;

        foreach (var item in _items) item.CanTranslate = !busy;
    }

    private void ShowNotice(string text, bool warn = false)
    {
        LabNotice.Text = text;
        BarNotice.Background = (Brush)FindResource(warn ? "Status.WarnSoft" : "Accent.Faint");
        IconNotice.IconBrush = (Brush)FindResource(warn ? "Status.Warn" : "Accent.Base");
        IconNotice.Icon = warn ? "lucide/triangle-alert" : "lucide/info";
        BarNotice.Visibility = Visibility.Visible;
    }

    private void OnDismissNoticeClick(object sender, RoutedEventArgs e)
        => BarNotice.Visibility = Visibility.Collapsed;
}
