using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml.MarkupExtensions;

namespace CrystalStudio;

public class EditorWindow : Window
{
    private readonly Dictionary<string, object> editorNames = [];
    public new EditorWindow? Owner { get; set; }
    public double Left { get => Position.X; set => Position = new PixelPoint((int)value, Position.Y); }
    public double Top { get => Position.Y; set => Position = new PixelPoint(Position.X, (int)value); }
    public double ActualWidth => Bounds.Width;
    public double ActualHeight => Bounds.Height;
    public bool? DialogResult { set => Close(value); }

    public EditorWindow()
    {
        AddHandler(KeyDownEvent, (_, e) => InputState.Press(e), RoutingStrategies.Tunnel, true);
        AddHandler(KeyUpEvent, (_, e) => InputState.Release(e), RoutingStrategies.Tunnel, true);
        AddHandler(PointerPressedEvent, (_, e) => InputState.Modifiers = e.KeyModifiers, RoutingStrategies.Tunnel, true);
        AddHandler(PointerMovedEvent, (_, e) => InputState.Modifiers = e.KeyModifiers, RoutingStrategies.Tunnel, true);
        Deactivated += (_, _) => InputState.Clear();
        AddHandler(Button.ClickEvent, (_, e) =>
        {
            if (IsVisible && e.Source is Button { IsCancel: true } button && button.GetVisualRoot() == this)
            {
                e.Handled = true; Close(false);
            }
        });
        Opened += (_, _) =>
        {
            foreach (var choice in this.GetVisualDescendants().OfType<ComboBox>())
                if (choice.ItemTemplate == null && choice.DisplayMemberBinding == null) choice.ItemTemplate = LocalText.ChoiceTemplate;
            // WPF window sizes include native decorations. Avalonia sizes the
            // client area, so retain the original requested outer dimensions.
            if (SizeToContent == SizeToContent.Manual && FrameSize is { } frame)
            {
                Width = Math.Max(MinWidth, Width - Math.Max(0, frame.Width - ClientSize.Width));
                Height = Math.Max(MinHeight, Height - Math.Max(0, frame.Height - ClientSize.Height));
            }
            PopupHeight.Fit(this);
        };
    }

    public bool? ShowDialog() => ShowDialog(Owner ?? Modal.Owner);
    public bool? ShowDialog(EditorWindow? owner)
    {
        if (owner == null) throw new InvalidOperationException("A dialog requires an open owner window.");
        return Modal.Run(base.ShowDialog<bool?>(owner));
    }
    public new void Show() { if (Owner is { IsVisible: true } owner) base.Show(owner); else base.Show(); }
    protected void RegisterEditorName(string name, object value) => editorNames[name] = value;
    protected object? FindEditorName(string name) => editorNames.GetValueOrDefault(name) ?? this.FindControl<Control>(name);
}

internal static class Modal
{
    public static EditorWindow? Owner => (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?
        .Windows.OfType<EditorWindow>().LastOrDefault(w => w.IsActive)
        ?? (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow as EditorWindow;

    // A nested dispatcher frame retains the editor's synchronous confirmation and
    // save contracts while native Avalonia dialogs complete asynchronously.
    internal static T Run<T>(Task<T> task)
    {
        if (task.IsCompleted) return task.GetAwaiter().GetResult();
        var frame = new DispatcherFrame();
        _ = task.ContinueWith(_ => Dispatcher.UIThread.Post(() => frame.Continue = false), TaskScheduler.Default);
        Dispatcher.UIThread.PushFrame(frame);
        return task.GetAwaiter().GetResult();
    }
}

internal static class InputState
{
    private static readonly HashSet<Key> pressed = [];
    public static KeyModifiers Modifiers { get; set; }
    public static IInputElement? FocusedElement => Modal.Owner?.FocusManager?.GetFocusedElement();
    public static bool IsKeyDown(Key key) => pressed.Contains(key);
    public static void Press(KeyEventArgs e) { Modifiers = e.KeyModifiers; pressed.Add(e.Key); }
    public static void Release(KeyEventArgs e) { Modifiers = e.KeyModifiers; pressed.Remove(e.Key); }
    public static void Clear() { Modifiers = KeyModifiers.None; pressed.Clear(); }
}

internal static class EditorCursors
{
    public static Cursor Arrow { get; } = new(StandardCursorType.Arrow);
    public static Cursor Hand { get; } = new(StandardCursorType.Hand);
    public static Cursor SizeAll { get; } = new(StandardCursorType.SizeAll);
    public static Cursor SizeWE { get; } = new(StandardCursorType.SizeWestEast);
    public static Cursor SizeNS { get; } = new(StandardCursorType.SizeNorthSouth);
    public static Cursor Cross { get; } = new(StandardCursorType.Cross);
}

internal static class EditorControls
{
    public static void SetResource(this StyledElement target, AvaloniaProperty property, object key) =>
        target.Bind(property, new DynamicResourceExtension(key));
    public static void SetTip(this Control target, object? tip) => ToolTip.SetTip(target, tip);
    public static object? GetTip(this Control target) => ToolTip.GetTip(target);
}

internal static class EditorGeometry
{
    public static Rect Intersect(Rect a, Rect b) => a.Intersect(b);
    public static bool IsEmpty(Rect rect) => rect.Width <= 0 || rect.Height <= 0;
    public static Rect Union(Rect a, Rect b) => IsEmpty(a) ? b : IsEmpty(b) ? a : a.Union(b);
}

internal static class EditorLifetime
{
    public static bool IsExiting { get; set; }
}

public abstract class SceneControl : Control
{
    private IPointer? capturedPointer;
    protected double ActualWidth => Bounds.Width;
    protected double ActualHeight => Bounds.Height;
    protected void CapturePointer(IPointer pointer) { capturedPointer = pointer; pointer.Capture(this); }
    protected void ReleasePointer() { var pointer = capturedPointer; capturedPointer = null; pointer?.Capture(null); }
    public sealed override void Render(Avalonia.Media.DrawingContext context)
    {
        base.Render(context);
        var scene = new SceneGroup();
        using (var recorder = scene.Open()) RenderScene(recorder);
        scene.Render(context);
    }
    private protected abstract void RenderScene(SceneContext context);
    protected void AddSceneLayer(Control child)
    {
        child.IsHitTestVisible = false;
        VisualChildren.Add(child); LogicalChildren.Add(child);
    }
    protected override Size MeasureOverride(Size availableSize)
    {
        foreach (var child in VisualChildren.OfType<Control>()) child.Measure(availableSize);
        return default;
    }
    protected override Size ArrangeOverride(Size finalSize)
    {
        foreach (var child in VisualChildren.OfType<Control>()) child.Arrange(new Rect(finalSize));
        return finalSize;
    }
}
