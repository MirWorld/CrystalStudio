namespace CrystalStudio;

internal static class MapPreviewRegion
{
    public static bool TrySelect(Point normalized, Rect region, Size bitmap, bool padded, out Point centre)
    {
        centre = default;
        if (!padded) { centre = new(region.X + normalized.X * region.Width, region.Y + normalized.Y * region.Height); return true; }
        double scale = Math.Min((bitmap.Width - 8) / (region.Width * 48), (bitmap.Height - 8) / (region.Height * 32));
        var content = new Rect((bitmap.Width - region.Width * 48 * scale) / 2, (bitmap.Height - region.Height * 32 * scale) / 2,
            region.Width * 48 * scale, region.Height * 32 * scale);
        var point = new Point(normalized.X * bitmap.Width, normalized.Y * bitmap.Height);
        if (!content.Contains(point)) return false;
        centre = new(region.X + (point.X - content.X) / scale / 48, region.Y + (point.Y - content.Y) / scale / 32); return true;
    }
    public static Rect Detail(Rect map, Point centre, int width, int height)
    {
        width = Math.Min(width, (int)map.Width); height = Math.Min(height, (int)map.Height);
        int left = Math.Clamp((int)(centre.X - width / 2.0), (int)map.X, (int)map.Right - width) & ~1;
        int top = Math.Clamp((int)(centre.Y - height / 2.0), (int)map.Y, (int)map.Bottom - height) & ~1;
        return new(left, top, width, height);
    }
}

internal sealed class PreviewNavigation
{
    public double Zoom { get; private set; } = 1;
    public Vector Offset { get; private set; }
    public Rect ImageRect(Size viewport, Size source)
    {
        if (source.Width <= 0 || source.Height <= 0) return default;
        double scale = Math.Min(viewport.Width / source.Width, viewport.Height / source.Height) * Zoom;
        var size = new Size(source.Width * scale, source.Height * scale);
        return new Rect(new Point((viewport.Width - size.Width) / 2 + Offset.X, (viewport.Height - size.Height) / 2 + Offset.Y), size);
    }
    public bool TryMap(Point point, Size viewport, Size source, out Point normalized)
    {
        var bounds = ImageRect(viewport, source); normalized = default;
        if (bounds.Width <= 0 || bounds.Height <= 0 || !bounds.Contains(point)) return false;
        normalized = new((point.X - bounds.X) / bounds.Width, (point.Y - bounds.Y) / bounds.Height); return true;
    }
    public void ZoomAt(Point point, double factor, Size viewport, Size source)
    {
        double next = Math.Clamp(Zoom * factor, 1, 8);
        Offset = (Offset + new Vector(viewport.Width / 2 - point.X, viewport.Height / 2 - point.Y)) * (next / Zoom)
            - new Vector(viewport.Width / 2 - point.X, viewport.Height / 2 - point.Y);
        Zoom = next; Clamp(viewport, source);
    }
    public void Pan(Vector delta, Size viewport, Size source) { Offset += delta; Clamp(viewport, source); }
    private void Clamp(Size viewport, Size source)
    {
        var bounds = ImageRect(viewport, source);
        Offset = new(Math.Clamp(Offset.X, -Math.Max(0, (bounds.Width - viewport.Width) / 2), Math.Max(0, (bounds.Width - viewport.Width) / 2)),
            Math.Clamp(Offset.Y, -Math.Max(0, (bounds.Height - viewport.Height) / 2), Math.Max(0, (bounds.Height - viewport.Height) / 2)));
    }
    public void Reset() { Zoom = 1; Offset = default; }
    internal void Focus(Point normalized, Size viewport, Size source)
    {
        Zoom = 4; Offset = default;
        var bounds = ImageRect(viewport, source);
        Offset = new Vector((.5 - normalized.X) * bounds.Width, (.5 - normalized.Y) * bounds.Height); Clamp(viewport, source);
    }
}

internal sealed class MapPreviewViewport : Control
{
    private readonly Image source;
    private readonly PreviewNavigation navigation;
    private Point? drag;
    private bool editing;
    internal MapPreviewDesign? DesignEditor { get; set; }
    public event Action<Point>? InspectRequested;
    public event Action? ViewChanged;
    public double Zoom => navigation.Zoom;
    public MapPreviewViewport(Image source, PreviewNavigation? navigation = null)
    {
        this.navigation = navigation ?? new();
        this.source = source; Focusable = true; ClipToBounds = true;
        Avalonia.Automation.AutomationProperties.SetName(this, LocalText.T("Interactive map preview"));
        this.SetTip(LocalText.T("Keyboard: + / - to zoom, arrows to pan, Home to fit, Enter to inspect the view centre."));
        source.PropertyChanged += (_, args) =>
        {
            if (args.Property == Image.SourceProperty)
            {
                if (args.OldValue is not IImage previous || args.NewValue is not IImage current || previous.Size != current.Size) this.navigation.Reset();
                ViewChanged?.Invoke();
            }
            if (args.Property == Image.SourceProperty || args.Property == OpacityProperty) InvalidateVisual();
        };
    }
    public override void Render(DrawingContext context)
    {
        context.FillRectangle(Brushes.Transparent, new Rect(Bounds.Size));
        if (source.Source is not { } image) return;
        using (context.PushOpacity(source.Opacity)) context.DrawImage(image, new Rect(image.Size), navigation.ImageRect(Bounds.Size, image.Size));
        DesignEditor?.Render(context, navigation.ImageRect(Bounds.Size, image.Size), image.Size);
    }
    public void ChangeZoom(double factor)
    {
        if (source.Source == null) return;
        navigation.ZoomAt(new Point(Bounds.Width / 2, Bounds.Height / 2), factor, Bounds.Size, source.Source.Size);
        InvalidateVisual(); ViewChanged?.Invoke();
    }
    public void ResetView() { navigation.Reset(); InvalidateVisual(); ViewChanged?.Invoke(); }
    internal void FocusPoint(Point normalized)
    {
        if (source.Source is not { } image) return;
        navigation.Focus(normalized, Bounds.Size, image.Size); InvalidateVisual(); ViewChanged?.Invoke();
    }
    public void InspectCentre()
    {
        if (source.Source is { } image && navigation.TryMap(new Point(Bounds.Width / 2, Bounds.Height / 2), Bounds.Size, image.Size, out var point)) InspectRequested?.Invoke(point);
    }
    protected override void OnPointerWheelChanged(PointerWheelEventArgs args)
    {
        base.OnPointerWheelChanged(args);
        if (source.Source == null) return;
        navigation.ZoomAt(args.GetPosition(this), Math.Pow(1.2, args.Delta.Y), Bounds.Size, source.Source.Size);
        InvalidateVisual(); ViewChanged?.Invoke(); args.Handled = true;
    }
    protected override void OnPointerPressed(PointerPressedEventArgs args)
    {
        base.OnPointerPressed(args);
        var properties = args.GetCurrentPoint(this).Properties;
        if ((!properties.IsLeftButtonPressed && !properties.IsMiddleButtonPressed) || source.Source == null) return;
        Focus(); var point = args.GetPosition(this);
        if (properties.IsLeftButtonPressed && navigation.TryMap(point, Bounds.Size, source.Source.Size, out var editPoint) && DesignEditor?.Press(editPoint, source.Source.Size) == true)
        { editing = true; args.Pointer.Capture(this); }
        else if (properties.IsLeftButtonPressed && args.ClickCount == 2 && navigation.TryMap(point, Bounds.Size, source.Source.Size, out var normalized)) InspectRequested?.Invoke(normalized);
        else { drag = point; args.Pointer.Capture(this); }
        args.Handled = true;
    }
    protected override void OnPointerMoved(PointerEventArgs args)
    {
        base.OnPointerMoved(args);
        if (editing && source.Source is { } current)
        {
            if (navigation.TryMap(args.GetPosition(this), Bounds.Size, current.Size, out var normalized)) DesignEditor?.Move(normalized, current.Size);
            InvalidateVisual(); args.Handled = true; return;
        }
        if (drag is not { } previous || source.Source == null) return;
        var point = args.GetPosition(this); navigation.Pan(point - previous, Bounds.Size, source.Source.Size); drag = point; InvalidateVisual(); ViewChanged?.Invoke();
    }
    protected override void OnPointerReleased(PointerReleasedEventArgs args)
    {
        base.OnPointerReleased(args);
        if (editing)
        {
            editing = false;
            if (source.Source is { } image && navigation.TryMap(args.GetPosition(this), Bounds.Size, image.Size, out var normalized)) DesignEditor?.Release(normalized, image.Size);
            else DesignEditor?.Cancel();
            InvalidateVisual();
        }
        drag = null; args.Pointer.Capture(null);
    }
    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs args) { base.OnPointerCaptureLost(args); drag = null; if (editing) { editing = false; DesignEditor?.Cancel(); InvalidateVisual(); } }
    protected override void OnKeyDown(KeyEventArgs args)
    {
        base.OnKeyDown(args);
        if (DesignEditor?.Key(args.Key, args.KeyModifiers) == true) { InvalidateVisual(); args.Handled = true; return; }
        if (args.Key is Key.Add or Key.OemPlus) ChangeZoom(1.2);
        else if (args.Key is Key.Subtract or Key.OemMinus) ChangeZoom(1 / 1.2);
        else if (args.Key is Key.D0 or Key.NumPad0 or Key.Home) ResetView();
        else if (args.Key == Key.Enter) InspectCentre();
        else if (source.Source is { } image && args.Key is Key.Left or Key.Right or Key.Up or Key.Down)
        {
            navigation.Pan(args.Key switch { Key.Left => new Vector(40, 0), Key.Right => new Vector(-40, 0), Key.Up => new Vector(0, 40), _ => new Vector(0, -40) }, Bounds.Size, image.Size);
            InvalidateVisual(); ViewChanged?.Invoke();
        }
        else return;
        args.Handled = true;
    }
}
