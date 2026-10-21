using System.Windows;
using System.Windows.Controls;

namespace DarkCipher.AgentControl.App.Shell;

/// <summary>
/// Dashboard layout: cards flow left to right in rows of <see cref="Columns"/>; a card whose container has
/// <see cref="IsWideProperty"/> set takes a full row of its own. Rows size to their tallest card, so no card is clipped at
/// any text size or zoom. (The ItemsControl's container style binds IsWide to the card view model.)
/// </summary>
public sealed class CardPanel : Panel
{
    public static readonly DependencyProperty ColumnsProperty = DependencyProperty.Register(nameof(Columns), typeof(int), typeof(CardPanel),
        new FrameworkPropertyMetadata(3, FrameworkPropertyMetadataOptions.AffectsMeasure), v => v is int i && i is >= 1 and <= 6);

    public static readonly DependencyProperty IsWideProperty = DependencyProperty.RegisterAttached("IsWide", typeof(bool), typeof(CardPanel),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsParentMeasure | FrameworkPropertyMetadataOptions.AffectsParentArrange));

    public int Columns { get => (int)GetValue(ColumnsProperty); set => SetValue(ColumnsProperty, value); }

    public static bool GetIsWide(DependencyObject d) => (bool)d.GetValue(IsWideProperty);
    public static void SetIsWide(DependencyObject d, bool value) => d.SetValue(IsWideProperty, value);

    private List<(List<UIElement> Cells, bool Wide)> Rows()
    {
        var rows = new List<(List<UIElement>, bool)>();
        var row = new List<UIElement>();
        foreach (UIElement child in InternalChildren)
        {
            if (child.Visibility == Visibility.Collapsed) continue;
            if (GetIsWide(child))
            {
                if (row.Count > 0) { rows.Add((row, false)); row = []; }
                rows.Add(([child], true));
                continue;
            }
            row.Add(child);
            if (row.Count == Columns) { rows.Add((row, false)); row = []; }
        }
        if (row.Count > 0) rows.Add((row, false));
        return rows;
    }

    protected override Size MeasureOverride(Size available)
    {
        var width = double.IsInfinity(available.Width) ? 1200 : available.Width;
        double height = 0;
        foreach (UIElement c in InternalChildren) if (c.Visibility == Visibility.Collapsed) c.Measure(new Size(0, 0));
        foreach (var (cells, wide) in Rows())
        {
            var w = wide ? width : width / Columns;
            double rowHeight = 0;
            foreach (var c in cells) { c.Measure(new Size(w, double.PositiveInfinity)); rowHeight = Math.Max(rowHeight, c.DesiredSize.Height); }
            height += rowHeight;
        }
        return new Size(width, height);
    }

    protected override Size ArrangeOverride(Size final)
    {
        double y = 0;
        foreach (var (cells, wide) in Rows())
        {
            var w = wide ? final.Width : final.Width / Columns;
            var rowHeight = cells.Max(c => c.DesiredSize.Height);
            for (var i = 0; i < cells.Count; i++) cells[i].Arrange(new Rect(i * w, y, w, rowHeight));
            y += rowHeight;
        }
        return final;
    }
}
