using System.Globalization;

namespace CrystalStudio;

public sealed class GeneratorPreview : Grid
{
    private readonly PreviewSurface surface = new() { Name = "PreviewSurface" };
    private readonly TextBlock zoomText = new() { Name = "PreviewZoomText", MinWidth = 42, TextAlignment = TextAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    private readonly Button zoomIn;
    private readonly Button zoomOut;
    private readonly Button fit;
    public Image? Image { get => surface.Image; set { surface.Image = value; UpdateControls(); } }
    public double Zoom => surface.Zoom;
    public Vector Pan => surface.Offset;

    public GeneratorPreview()
    {
        RowDefinitions = new RowDefinitions("*,Auto");
        Children.Add(surface);
        surface.Changed += UpdateControls;
        var toolbar = new DockPanel { LastChildFill = true, Margin = new Thickness(0, 8, 0, 0) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        zoomOut = ActionButton("PreviewZoomOut", "−", "Zoom out", () => surface.SetZoom(Zoom / 1.25));
        zoomIn = ActionButton("PreviewZoomIn", "+", "Zoom in", () => surface.SetZoom(Zoom * 1.25));
        fit = ActionButton("PreviewFit", LocalText.T("Fit preview"), "Fit the preview to the window", surface.Fit);
        buttons.Children.Add(zoomOut); buttons.Children.Add(zoomText); buttons.Children.Add(zoomIn); buttons.Children.Add(fit);
        DockPanel.SetDock(buttons, Dock.Right); toolbar.Children.Add(buttons);
        var hint = new TextBlock { Text = LocalText.T("Wheel: zoom · Drag: pan"), VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 8, 0) };
        hint.Foreground = ThemeService.Brush("Muted");
        toolbar.Children.Add(hint);
        SetRow(toolbar, 1); Children.Add(toolbar);
        ToolTip.SetTip(zoomText, LocalText.T("Zoom is relative to fit and only affects the preview, not the map."));
        UpdateControls();
    }

    private static Button ActionButton(string name, string content, string description, Action action)
    {
        var button = new Button { Name = name, Content = content, Padding = new Thickness(8, 4), MinWidth = 28 };
        Avalonia.Automation.AutomationProperties.SetName(button, LocalText.T(description));
        ToolTip.SetTip(button, LocalText.T(description));
        button.Click += (_, _) => action();
        return button;
    }

    private void UpdateControls()
    {
        bool available = Image?.Source != null;
        zoomText.Text = (Zoom * 100).ToString("0", CultureInfo.InvariantCulture) + "%";
        zoomIn.IsEnabled = available && Zoom < 8;
        zoomOut.IsEnabled = available && Zoom > 1;
        fit.IsEnabled = available;
    }

    private sealed class PreviewSurface : Border
    {
        private IPointer? dragging;
        private Point lastPoint;
        public double Zoom { get; private set; } = 1;
        public Vector Offset { get; private set; }
        public event Action? Changed;
        public Image? Image
        {
            get => Child as Image;
            set
            {
                if (Image is { } previous) previous.PropertyChanged -= ImageChanged;
                Child = value;
                if (value != null)
                {
                    value.IsHitTestVisible = false;
                    value.RenderTransformOrigin = new RelativePoint(0, 0, RelativeUnit.Relative);
                    value.PropertyChanged += ImageChanged;
                }
                Fit();
            }
        }

        public PreviewSurface()
        {
            ClipToBounds = true; Background = Brushes.Transparent; Focusable = true; Cursor = EditorCursors.Hand;
            SizeChanged += (_, _) => UpdateTransform();
            Unloaded += (_, _) => StopDrag();
            Avalonia.Automation.AutomationProperties.SetName(this, LocalText.T("Map preview navigation"));
        }

        private void ImageChanged(object? sender, AvaloniaPropertyChangedEventArgs change)
        {
            if (change.Property != Avalonia.Controls.Image.SourceProperty) return;
            StopDrag();
            if (change.OldValue is not IImage oldImage || change.NewValue is not IImage newImage || oldImage.Size != newImage.Size) Fit();
            else UpdateTransform();
        }

        public void Fit() { Zoom = 1; Offset = default; UpdateTransform(); }
        public void SetZoom(double zoom, Point? anchor = null)
        {
            if (Image?.Source == null || !double.IsFinite(zoom)) return;
            double next = Math.Clamp(zoom, 1, 8);
            var point = anchor ?? new Point(Bounds.Width / 2, Bounds.Height / 2);
            Offset = (Vector)point - ((Vector)point - Offset) * (next / Zoom);
            Zoom = next; UpdateTransform();
        }

        private void UpdateTransform()
        {
            if (Image is not { } image) return;
            if (image.Source is { Size: { Width: > 0, Height: > 0 } } source && Bounds.Width > 0 && Bounds.Height > 0)
            {
                double scale = Math.Min(Bounds.Width / source.Size.Width, Bounds.Height / source.Size.Height);
                double width = source.Size.Width * scale, height = source.Size.Height * scale;
                Offset = new Vector(ClampOffset(Offset.X, Bounds.Width, width), ClampOffset(Offset.Y, Bounds.Height, height));
            }
            image.RenderTransform = new MatrixTransform(Matrix.CreateScale(Zoom, Zoom) * Matrix.CreateTranslation(Offset.X, Offset.Y));
            Changed?.Invoke();
        }

        private double ClampOffset(double offset, double viewport, double fitted)
        {
            double inset = (viewport - fitted) / 2 * Zoom;
            double extent = fitted * Zoom;
            return (extent <= viewport ? (viewport - extent) / 2 : Math.Clamp(inset + offset, viewport - extent, 0)) - inset;
        }

        private void StopDrag() { var pointer = dragging; dragging = null; pointer?.Capture(null); }
        protected override void OnPointerPressed(PointerPressedEventArgs args)
        {
            base.OnPointerPressed(args);
            var properties = args.GetCurrentPoint(this).Properties;
            if (Image?.Source == null || !properties.IsLeftButtonPressed && !properties.IsMiddleButtonPressed) return;
            Focus(); lastPoint = args.GetPosition(this); dragging = args.Pointer; args.Pointer.Capture(this); args.Handled = true;
        }
        protected override void OnPointerMoved(PointerEventArgs args)
        {
            base.OnPointerMoved(args);
            if (dragging != args.Pointer) return;
            var point = args.GetPosition(this); Offset += point - lastPoint; lastPoint = point;
            UpdateTransform(); args.Handled = true;
        }
        protected override void OnPointerReleased(PointerReleasedEventArgs args) { base.OnPointerReleased(args); StopDrag(); }
        protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs args) { base.OnPointerCaptureLost(args); dragging = null; }
        protected override void OnPointerWheelChanged(PointerWheelEventArgs args)
        {
            base.OnPointerWheelChanged(args);
            if (Image?.Source == null || args.Delta.Y == 0) return;
            SetZoom(Zoom * Math.Pow(1.25, args.Delta.Y), args.GetPosition(this)); args.Handled = true;
        }
        protected override void OnKeyDown(KeyEventArgs args)
        {
            base.OnKeyDown(args);
            if (Image?.Source == null) return;
            switch (args.Key)
            {
                case Key.Add: case Key.OemPlus: SetZoom(Zoom * 1.25); break;
                case Key.Subtract: case Key.OemMinus: SetZoom(Zoom / 1.25); break;
                case Key.D0: case Key.NumPad0: case Key.Home: Fit(); break;
                case Key.Left: Offset += new Vector(48, 0); UpdateTransform(); break;
                case Key.Right: Offset += new Vector(-48, 0); UpdateTransform(); break;
                case Key.Up: Offset += new Vector(0, 48); UpdateTransform(); break;
                case Key.Down: Offset += new Vector(0, -48); UpdateTransform(); break;
                default: return;
            }
            args.Handled = true;
        }
    }
}
