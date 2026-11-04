namespace CrystalStudio;

public sealed class CategoryPreviewVisibility : AvaloniaObject
{
    public static readonly AttachedProperty<bool> TrackProperty = AvaloniaProperty.RegisterAttached<CategoryPreviewVisibility, Image, bool>("Track");
    private static readonly AttachedProperty<Rect?> ViewportProperty = AvaloniaProperty.RegisterAttached<CategoryPreviewVisibility, Image, Rect?>("Viewport");
    static CategoryPreviewVisibility() => TrackProperty.Changed.AddClassHandler<Image>((image, change) =>
    {
        if (image.GetValue(TrackProperty))
        {
            image.PropertyChanged += Changed;
            image.AttachedToVisualTree += Attached;
            image.DetachedFromVisualTree += Detached;
            image.EffectiveViewportChanged += ViewportChanged;
        }
        else
        {
            image.PropertyChanged -= Changed;
            image.AttachedToVisualTree -= Attached;
            image.DetachedFromVisualTree -= Detached;
            image.EffectiveViewportChanged -= ViewportChanged;
            Release(image);
        }
    });
    public static bool GetTrack(Image target) => target.GetValue(TrackProperty);
    public static void SetTrack(Image target, bool value) => target.SetValue(TrackProperty, value);
    private static void Changed(object? sender, AvaloniaPropertyChangedEventArgs args)
    {
        if (sender is not Image image) return;
        if (args.Property != StyledElement.DataContextProperty && args.Property != Visual.IsVisibleProperty && args.Property != Visual.BoundsProperty) return;
        if (args.Property == StyledElement.DataContextProperty && args.OldValue is StampCategoryNode old) old.ReleasePreview();
        Update(image);
    }
    private static void Attached(object? sender, VisualTreeAttachmentEventArgs args)
    {
        if (sender is Image image) Update(image);
    }
    private static void ViewportChanged(object? sender, EffectiveViewportChangedEventArgs args)
    {
        if (sender is not Image image) return;
        image.SetValue(ViewportProperty, args.EffectiveViewport);
        Update(image);
    }
    private static void Update(Image image)
    {
        if (image.DataContext is not StampCategoryNode node) return;
        if (image.GetVisualRoot() != null && image.IsEffectivelyVisible &&
            image.GetValue(ViewportProperty) is { } viewport && viewport.Intersects(new Rect(image.Bounds.Size))) node.RequestPreview();
        else node.PausePreview();
    }
    private static void Detached(object? sender, VisualTreeAttachmentEventArgs args) { if (sender is Image image) Release(image); }
    private static void Release(Image image)
    {
        if (image.DataContext is StampCategoryNode node) node.ReleasePreview();
        image.ClearValue(ViewportProperty);
    }
}
