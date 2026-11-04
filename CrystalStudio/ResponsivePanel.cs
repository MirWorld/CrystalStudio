namespace CrystalStudio;

/// <summary>Flows controls into equal-width columns while retaining readable minimum widths.</summary>
public sealed class ResponsivePanel : Panel
{
    public static readonly StyledProperty<double> ItemMinWidthProperty = AvaloniaProperty.Register<ResponsivePanel, double>(
        nameof(ItemMinWidth), 180d, validate: width => double.IsFinite(width) && width > 0);
    static ResponsivePanel() => AffectsMeasure<ResponsivePanel>(ItemMinWidthProperty);
    public double ItemMinWidth { get => GetValue(ItemMinWidthProperty); set => SetValue(ItemMinWidthProperty, value); }

    private int Columns(double width, int count) => Math.Max(1, Math.Min(count,
        double.IsFinite(width) ? (int)Math.Max(1, Math.Floor(width / ItemMinWidth)) : 1));

    protected override Size MeasureOverride(Size availableSize)
    {
        var children = Children.Where(c => c.IsVisible != false).ToArray();
        int columns = Columns(availableSize.Width, children.Length);
        double width = double.IsFinite(availableSize.Width) ? availableSize.Width / columns : ItemMinWidth;
        double height = 0, rowHeight = 0;
        for (int i = 0; i < children.Length; i++)
        {
            children[i].Measure(new Size(width, double.PositiveInfinity));
            rowHeight = Math.Max(rowHeight, children[i].DesiredSize.Height);
            if ((i + 1) % columns == 0 || i == children.Length - 1) { height += rowHeight; rowHeight = 0; }
        }
        return new Size(children.Length == 0 ? 0 : width * columns, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var children = Children.Where(c => c.IsVisible != false).ToArray();
        int columns = Columns(finalSize.Width, children.Length);
        double width = finalSize.Width / columns, top = 0;
        for (int start = 0; start < children.Length; start += columns)
        {
            double height = children.Skip(start).Take(columns).Max(c => c.DesiredSize.Height);
            for (int i = start; i < Math.Min(start + columns, children.Length); i++)
                children[i].Arrange(new Rect((i - start) * width, top, width, height));
            top += height;
        }
        return finalSize;
    }
}
