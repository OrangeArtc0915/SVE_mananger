using System.Windows;
using System.Windows.Controls;

namespace StardewLauncher.App.Controls;

public enum MasonryMode
{
    /// <summary>固定列数。</summary>
    Columns,

    /// <summary>按可用宽度与目标项宽自动计算列数。</summary>
    AutoFit
}

/// <summary>
/// 瀑布流面板：每个子元素投入当前最矮的一列。列宽由可用宽度均分，高度不限。
/// </summary>
public class MasonryPanel : Panel
{
    private int _computedColumns = 1;

    public static readonly DependencyProperty ModeProperty = DependencyProperty.Register(
        nameof(Mode), typeof(MasonryMode), typeof(MasonryPanel),
        new FrameworkPropertyMetadata(MasonryMode.Columns, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty ColumnsProperty = DependencyProperty.Register(
        nameof(Columns), typeof(int), typeof(MasonryPanel),
        new FrameworkPropertyMetadata(2, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty ColumnGapProperty = DependencyProperty.Register(
        nameof(ColumnGap), typeof(double), typeof(MasonryPanel),
        new FrameworkPropertyMetadata(14d, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty RowGapProperty = DependencyProperty.Register(
        nameof(RowGap), typeof(double), typeof(MasonryPanel),
        new FrameworkPropertyMetadata(14d, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty ItemWidthProperty = DependencyProperty.Register(
        nameof(ItemWidth), typeof(double), typeof(MasonryPanel),
        new FrameworkPropertyMetadata(260d, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public MasonryMode Mode
    {
        get => (MasonryMode)GetValue(ModeProperty);
        set => SetValue(ModeProperty, value);
    }

    public int Columns
    {
        get => (int)GetValue(ColumnsProperty);
        set => SetValue(ColumnsProperty, value);
    }

    public double ColumnGap
    {
        get => (double)GetValue(ColumnGapProperty);
        set => SetValue(ColumnGapProperty, value);
    }

    public double RowGap
    {
        get => (double)GetValue(RowGapProperty);
        set => SetValue(RowGapProperty, value);
    }

    public double ItemWidth
    {
        get => (double)GetValue(ItemWidthProperty);
        set => SetValue(ItemWidthProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var columns = ResolveColumnCount(availableSize.Width);
        _computedColumns = columns;

        var columnWidth = ResolveColumnWidth(availableSize.Width, columns);
        var heights = new double[columns];

        foreach (UIElement child in InternalChildren)
        {
            child.Measure(new Size(columnWidth, double.PositiveInfinity));

            var index = IndexOfShortest(heights);
            heights[index] += child.DesiredSize.Height + RowGap;
        }

        var totalHeight = 0d;
        foreach (var height in heights)
        {
            var content = height > 0 ? height - RowGap : 0;
            totalHeight = Math.Max(totalHeight, content);
        }

        var totalWidth = double.IsInfinity(availableSize.Width) || double.IsNaN(availableSize.Width)
            ? columnWidth * columns + ColumnGap * (columns - 1)
            : availableSize.Width;

        return new Size(totalWidth, totalHeight);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var columns = _computedColumns > 0 ? _computedColumns : 1;
        var columnWidth = ResolveColumnWidth(finalSize.Width, columns);
        var heights = new double[columns];

        foreach (UIElement child in InternalChildren)
        {
            var index = IndexOfShortest(heights);
            var x = index * (columnWidth + ColumnGap);
            var y = heights[index];

            child.Arrange(new Rect(new Point(x, y), new Size(columnWidth, child.DesiredSize.Height)));
            heights[index] += child.DesiredSize.Height + RowGap;
        }

        return finalSize;
    }

    private int ResolveColumnCount(double availableWidth)
    {
        if (Mode != MasonryMode.AutoFit || double.IsInfinity(availableWidth) || double.IsNaN(availableWidth))
            return Math.Max(1, Columns);

        var itemWidth = ItemWidth > 0 ? ItemWidth : 260;
        return Math.Max(1, (int)Math.Floor((availableWidth + ColumnGap) / (itemWidth + ColumnGap)));
    }

    private double ResolveColumnWidth(double availableWidth, int columns)
    {
        if (double.IsInfinity(availableWidth) || double.IsNaN(availableWidth))
            return ItemWidth > 0 ? ItemWidth : 260;

        var total = availableWidth - (columns - 1) * ColumnGap;
        return Math.Max(0, total / columns);
    }

    private static int IndexOfShortest(double[] heights)
    {
        var index = 0;
        var min = heights[0];

        for (var i = 1; i < heights.Length; i++)
        {
            if (heights[i] >= min) continue;
            min = heights[i];
            index = i;
        }

        return index;
    }
}
