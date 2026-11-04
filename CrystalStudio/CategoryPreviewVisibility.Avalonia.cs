namespace CrystalStudio;

public sealed class CategoryPreviewVisibility : AvaloniaObject
{
    public static readonly AttachedProperty<bool> TrackProperty = AvaloniaProperty.RegisterAttached<CategoryPreviewVisibility, Image, bool>("Track");
    static CategoryPreviewVisibility() => TrackProperty.Changed.AddClassHandler<Image>((image, change) =>
    {
        if (image.GetValue(TrackProperty))
        {
            image.PropertyChanged += Changed;
            image.AttachedToVisualTree += Attached;
            image.DetachedFromVisualTree += Detached;
        }
        else
        {
            image.PropertyChanged -= Changed;
            image.AttachedToVisualTree -= Attached;
            image.DetachedFromVisualTree -= Detached;
            Release(image);
        }
    });
    public static bool GetTrack(Image target) => target.GetValue(TrackProperty);
    public static void SetTrack(Image target, bool value) => target.SetValue(TrackProperty, value);
    private static void Changed(object? sender, AvaloniaPropertyChangedEventArgs args)
    {
        if (sender is not Image image) return;
        if (args.Property != StyledElement.DataContextProperty && args.Property != Visual.IsVisibleProperty) return;
        if (args.Property == StyledElement.DataContextProperty && args.OldValue is StampCategoryNode old) old.ReleasePreview();
        if (image.DataContext is not StampCategoryNode node) return;
        if (image.GetVisualRoot() != null && image.IsEffectivelyVisible) _ = node.Bitmap; else node.ReleasePreview();
    }
    private static void Attached(object? sender, VisualTreeAttachmentEventArgs args)
    {
        if (sender is Image { DataContext: StampCategoryNode node } image && image.IsEffectivelyVisible) _ = node.Bitmap;
    }
    private static void Detached(object? sender, VisualTreeAttachmentEventArgs args) { if (sender is Image image) Release(image); }
    private static void Release(Image image) { if (image.DataContext is StampCategoryNode node) node.ReleasePreview(); }
}
