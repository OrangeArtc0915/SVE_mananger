using System.Windows;
using System.Windows.Media;

namespace StardewLauncher.App.Windows;

/// <summary>一个简单的单行输入框（起名字用）。项目里没有自绘输入弹窗，先做这一个共用的。</summary>
public partial class PromptWindow : Window
{
    private readonly string _originalLabel;

    public string Value { get; private set; } = string.Empty;

    public PromptWindow(string title, string label, string initial)
    {
        InitializeComponent();

        _originalLabel = label;

        Title = title;
        LabTitle.Text = title;
        LabLabel.Text = label;
        TxtInput.Text = initial ?? string.Empty;

        Loaded += (_, _) =>
        {
            TxtInput.Focus();
            TxtInput.SelectAll();
        };
    }

    private void OnConfirmClick(object sender, RoutedEventArgs e)
    {
        var value = TxtInput.Text.Trim();

        if (string.IsNullOrEmpty(value))
        {
            LabLabel.Text = "不能为空，换个名字吧。";
            LabLabel.Foreground = (Brush)FindResource("Status.Warn");
            TxtInput.Focus();
            return;
        }

        LabLabel.Text = _originalLabel;
        LabLabel.Foreground = (Brush)FindResource("Text.Secondary");

        Value = value;
        DialogResult = true;
    }

    private void OnCancelClick(object sender, RoutedEventArgs e) => DialogResult = false;
}

/// <summary>输入框的调用入口。</summary>
public static class Prompt
{
    /// <summary>弹出输入框。用户取消或留空时返回 null。</summary>
    public static string? Show(Window? owner, string title, string label, string initial = "")
    {
        var window = new PromptWindow(title, label, initial);

        if (owner is not null) window.Owner = owner;

        return window.ShowDialog() == true && !string.IsNullOrWhiteSpace(window.Value)
            ? window.Value
            : null;
    }
}
