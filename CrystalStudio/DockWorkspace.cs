using System.IO;
using System.Text.Json;
namespace CrystalStudio;

/// <summary>Owns movable panel containers; the editor controls themselves stay alive when reparented.</summary>
public sealed class DockWorkspace : Grid
{
    private readonly EditorWindow owner;
    private readonly EventHandler<KeyEventArgs> shortcuts;
    private readonly Dictionary<Dock, Grid> zones = [];
    private readonly List<Pane> panes = [];
    private readonly string layoutPath = Path.Combine(EditorPaths.ProfileDirectory, "workspace.json");
    private static Brush Surface => ThemeService.Brush("Theme1C2028");
    private static Brush Line => ThemeService.Brush("Theme3B4555");
    private bool shuttingDown;
    private readonly IReadOnlyList<LayoutEntry>? defaultLayout;
    public bool IsLayoutLocked { get; private set; }
    public event EventHandler? LayoutLockChanged;
    private readonly Canvas dropOverlay = new() { IsHitTestVisible = false, IsVisible = false };
    private readonly Border dropPreview = new() { Background = new ImmutableSolidColorBrush(Color.FromArgb(65, 132, 152, 255)), BorderBrush = new ImmutableSolidColorBrush(Color.FromRgb(132, 152, 255)), BorderThickness = new(1), CornerRadius = new(3), IsHitTestVisible = false };
    private MenuItem? sectionsMenu;
    private readonly Dictionary<string, MenuItem> sectionItems = [];
    private readonly Dictionary<Dock, double> dockSizes = new() { [Dock.Left] = 110, [Dock.Right] = 360, [Dock.Top] = 112, [Dock.Bottom] = 160 };
    private sealed class Pane(string title, Control content, Dock home, double size)
    {
        public string Title = title;
        public Dock Home = home;
        public Dock Side = home;
        public double Size = size;
        public double HomeSize = size;
        public bool Visible = true;
        public Border Frame = new() { Background = Surface, BorderBrush = Line, BorderThickness = new(0), Child = content };
        public EditorWindow? EditorWindow;
        public PixelPoint? LockedPosition;
    }
    public sealed record LayoutEntry(string Title, Dock Side, double Size, bool Floating, double Left, double Top, double Width, double Height, double DockSize = 0, bool Visible = true, int Version = 0, bool Locked = false);

    public static IReadOnlyList<LayoutEntry> LoadDefaultLayout()
    {
        using var stream = typeof(DockWorkspace).Assembly.GetManifestResourceStream("CrystalStudio.DefaultWorkspace.json");
        return stream is null ? [] : JsonSerializer.Deserialize<List<LayoutEntry>>(stream) ?? [];
    }

    public void SetLayoutLocked(bool locked)
    {
        IsLayoutLocked = locked;
        foreach (var pane in panes) pane.LockedPosition = locked ? pane.EditorWindow?.Position : null;
        dropOverlay.IsVisible = false;
        Rebuild();
        LayoutLockChanged?.Invoke(this, EventArgs.Empty);
        Save();
    }

    public DockWorkspace(EditorWindow owner, Control canvas, EventHandler<KeyEventArgs> shortcuts, string? layoutFile = null, IReadOnlyList<LayoutEntry>? defaultLayout = null)
    {
        this.SetResource(BackgroundProperty, "Theme1C2028");
        this.owner = owner; this.shortcuts = shortcuts;
        this.defaultLayout = defaultLayout;
        if (layoutFile is not null) layoutPath = layoutFile;
        RowDefinitions.Add(new() { Height = GridLength.Auto });
        RowDefinitions.Add(new() { Height = new(1, GridUnitType.Star) });
        RowDefinitions.Add(new() { Height = GridLength.Auto });
        ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
        ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        SetRow(canvas, 1); SetColumn(canvas, 1); Children.Add(canvas);
        foreach (Dock side in Enum.GetValues<Dock>())
        {
            var zone = new Grid { Background = Surface };
            SetRow(zone, side == Dock.Top ? 0 : side == Dock.Bottom ? 2 : 1);
            SetColumn(zone, side == Dock.Left ? 0 : side == Dock.Right ? 2 : 1);
            if (side is Dock.Left or Dock.Right) { SetRow(zone, 0); SetRowSpan(zone, 3); }
            zone.SetResource(BackgroundProperty, "Theme1C2028");
            zones.Add(side, zone); Children.Add(zone);
        }
        dropPreview.Child = new TextBlock { Text = LocalText.T("Dock here"), Foreground = Brushes.White, FontWeight = FontWeight.SemiBold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        SetRowSpan(dropOverlay, 3); SetColumnSpan(dropOverlay, 3); dropOverlay.ZIndex = 100; dropOverlay.Children.Add(dropPreview); Children.Add(dropOverlay);
        owner.Closing += (_, e) => { if (e.Cancel) return; Save(); shuttingDown = true; };
        owner.Closed += (_, _) => { foreach (var p in panes) p.EditorWindow?.Close(); };
    }
    public void Add(string title, Control content, Dock side, double size)
    {
        var p = new Pane(title, content, side, size); panes.Add(p); p.Frame.SetResource(Border.BackgroundProperty, "Theme1C2028");
        var body = new DockPanel(); p.Frame.Child = null;
        var header = new Border { Background = Line, Padding = new(7, 4, 7, 4), Cursor = EditorCursors.SizeAll };
        ToolTip.SetTip(header, LocalText.T("Drag to dock or float; right-click for panel positions."));
        header.SetResource(Border.BackgroundProperty, "Theme3B4555");
        header.Child = new TextBlock { Text = string.Concat(("\u283F  "), LocalText.T(title)), FontSize = 11, TextTrimming = TextTrimming.CharacterEllipsis };
        ((TextBlock)header.Child).SetResource(TextBlock.ForegroundProperty, "ThemeE5E9F0");
        var menu = new ContextMenu();
        foreach (Dock d in Enum.GetValues<Dock>()) { var item = new MenuItem { Header = string.Concat(LocalText.T("Dock "), LocalText.T(d.ToString()))}; item.Click += (_, _) => Move(p, d); menu.Items.Add(item); }
        var floating = new MenuItem { Header = LocalText.T("Float EditorWindow")}; floating.Click += (_, _) => Float(p); menu.Items.Add(floating); header.ContextMenu = menu;
        var hide = new MenuItem { Header = LocalText.T("Hide Section")}; hide.Click += (_, _) => SetVisible(p, false); menu.Items.Add(hide);
        Point start = default; bool pressed = false, moved = false; IPointer? dragPointer = null;
        owner.AddHandler(KeyDownEvent, (_, e) =>
        {
            if (!pressed || e.Key != Key.Escape) return;
            pressed = moved = false; dragPointer?.Capture(null); dragPointer = null;
            dropOverlay.IsVisible = false; e.Handled = true;
        }, RoutingStrategies.Tunnel);
        header.PointerPressed += (_, e) =>
        {
            if (IsLayoutLocked || !e.GetCurrentPoint(header).Properties.IsLeftButtonPressed) return;
            if (e.ClickCount == 2) { if (p.EditorWindow is null) Float(p); else Move(p, p.Side); e.Handled = true; return; }
            start = e.GetPosition(header); pressed = true; moved = false; dragPointer = e.Pointer; e.Pointer.Capture(header);
        };
        header.PointerMoved += (_, e) =>
        {
            if (IsLayoutLocked || !pressed || !e.GetCurrentPoint(header).Properties.IsLeftButtonPressed) return;
            var point = e.GetPosition(header); if (!moved && Math.Abs(point.X - start.X) + Math.Abs(point.Y - start.Y) < 4) return;
            moved = true;
            var at = this.PointToClient(header.PointToScreen(point));
            if (new Rect(Bounds.Size).Contains(at)) { var target = DropTarget(at); ShowDropPreview(target.side, target.before); }
            else dropOverlay.IsVisible = false;
            e.Handled = true;
        };
        header.PointerReleased += (_, e) =>
        {
            if (!pressed) return;
            // 释放捕获会同步触发 PointerCaptureLost，先保留本次拖动状态。
            bool wasMoved = moved;
            pressed = false; e.Pointer.Capture(null); dropOverlay.IsVisible = false;
            if (!wasMoved || IsLayoutLocked) return;
            var at = this.PointToClient(header.PointToScreen(e.GetPosition(header)));
            if (new Rect(Bounds.Size).Contains(at)) { var target = DropTarget(at); Move(p, target.side, target.before); }
            else Float(p);
            e.Handled = true;
        };
        header.PointerCaptureLost += (_, _) => { pressed = false; dragPointer = null; dropOverlay.IsVisible = false; };
        DockPanel.SetDock(header, Dock.Top); body.Children.Add(header); body.Children.Add(content); p.Frame.Child = body;
        AddSectionItem(p); Rebuild();
    }
    private void Detach(Pane p)
    {
        if (p.EditorWindow is { } window) { window.Content = null; p.EditorWindow = null; window.Close(); }
        if (p.Frame.Parent is Panel parent) parent.Children.Remove(p.Frame);
    }
    private void Move(Pane p, Dock side, Pane? before = null)
    {
        if (IsLayoutLocked) return;
        if (ReferenceEquals(p, before)) return;
        Detach(p); p.Visible = true;
        if (p.Side != side) p.Size = p.HomeSize;
        p.Side = side; panes.Remove(p);
        if (side is Dock.Top or Dock.Bottom)
            dockSizes[side] = Math.Max(dockSizes[side], p.Title == "Palette" ? 230 : p.Title == "Cell Inspector" ? 160 : 112);
        int index = before is null ? -1 : panes.IndexOf(before);
        if (index < 0) panes.Add(p); else panes.Insert(index, p);
        Rebuild(); Save();
    }
    private void Float(Pane p, LayoutEntry? saved = null)
    {
        if (IsLayoutLocked && saved is null) return;
        if (p.EditorWindow is not null) { p.EditorWindow.Activate(); return; }
        Detach(p); p.Visible = true;
        var w = new EditorWindow { Title = string.Concat(LocalText.T(p.Title), LocalText.T(" \u2014 Crystal Studio")), Owner = owner, Content = p.Frame, Background = Surface, Foreground = owner.Foreground, FontFamily = owner.FontFamily, Width = saved?.Width ?? 380, Height = saved?.Height ?? 450, MinWidth = 180, MinHeight = 120, ShowInTaskbar = false };
        w.SetResource(EditorWindow.BackgroundProperty, "Theme1C2028"); w.SetResource(EditorWindow.ForegroundProperty, "ThemeE5E9F0");
        p.EditorWindow = w;
        if (saved is not null && double.IsFinite(saved.Left) && double.IsFinite(saved.Top))
        {
            var requested = new PixelPoint((int)saved.Left, (int)saved.Top);
            var screen = owner.Screens.ScreenFromPoint(requested) ?? owner.Screens.Primary;
            if (screen != null) w.Position = new PixelPoint(Math.Clamp(requested.X, screen.WorkingArea.X, Math.Max(screen.WorkingArea.X, screen.WorkingArea.Right - 180)),
                Math.Clamp(requested.Y, screen.WorkingArea.Y, Math.Max(screen.WorkingArea.Y, screen.WorkingArea.Bottom - 80)));
        }
        else w.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        w.PositionChanged += (_, _) =>
        {
            if (IsLayoutLocked && p.LockedPosition is { } position && w.Position != position) w.Position = position;
        };
        w.KeyDown += shortcuts;
        w.Closing += (_, e) => { if (IsLayoutLocked && !shuttingDown) { e.Cancel = true; return; } w.Content = null; p.EditorWindow = null; if (!shuttingDown) Rebuild(); };
        Rebuild(); w.Show();
    }
    private void Rebuild()
    {
        foreach (var p in panes)
        {
            if (sectionItems.TryGetValue(p.Title, out var item)) { item.IsChecked = p.Visible; item.IsEnabled = !IsLayoutLocked; }
            var header = (Border)((DockPanel)p.Frame.Child).Children[0];
            header.Cursor = IsLayoutLocked ? EditorCursors.Arrow : EditorCursors.SizeAll;
            ToolTip.SetTip(header, (IsLayoutLocked ? LocalText.T("Panel Layout Is Locked. Unlock It in the View Menu.") : LocalText.T("Drag to a Workspace Edge to Dock, or Outside to Float. Right-Click for Panel Positions.")));
            foreach (var action in header.ContextMenu.Items.OfType<MenuItem>()) action.IsEnabled = !IsLayoutLocked;
            if (p.EditorWindow is { } window)
            {
                window.CanResize = !IsLayoutLocked;
                p.LockedPosition = IsLayoutLocked ? p.LockedPosition ?? window.Position : null;
            }
        }
        foreach (var zone in zones.Values) zone.Children.Clear();
        foreach (var (side, zone) in zones)
        {
            zone.Children.Clear(); zone.RowDefinitions.Clear(); zone.ColumnDefinitions.Clear();
            var items = panes.Where(p => p.Visible && p.EditorWindow is null && p.Side == side).ToArray();
            bool vertical = side is Dock.Left or Dock.Right;
            zone.Width = vertical && items.Length > 0 ? dockSizes[side] : double.NaN;
            zone.Height = !vertical && items.Length > 0 ? dockSizes[side] : double.NaN;
            if (items.Length == 0) { zone.Width = 0; zone.Height = 0; continue; }
            for (int i = 0; i < items.Length; i++)
            {
                var p = items[i]; int index = i * 2;
                if (vertical) zone.RowDefinitions.Add(new() { Height = new(p.Size, GridUnitType.Star), MinHeight = p.Title == "Tool Options" ? 65 : p.Title == "Palette" ? 120 : 85 });
                else zone.ColumnDefinitions.Add(new() { Width = new(p.Size, GridUnitType.Star), MinWidth = 100 });
                SetRow(p.Frame, vertical ? index : 0); SetColumn(p.Frame, vertical ? 0 : index); zone.Children.Add(p.Frame);
                if (i == items.Length - 1) continue;
                if (vertical) zone.RowDefinitions.Add(new() { Height = new(5) }); else zone.ColumnDefinitions.Add(new() { Width = new(5) });
                var split = new GridSplitter { Background = Surface, HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Stretch, ResizeDirection = vertical ? GridResizeDirection.Rows : GridResizeDirection.Columns, ResizeBehavior = GridResizeBehavior.PreviousAndNext };
                split.SetResource(BackgroundProperty, "Theme1C2028");
                split.IsEnabled = !IsLayoutLocked; split.IsHitTestVisible = !IsLayoutLocked;
                SetRow(split, vertical ? index + 1 : 0); SetColumn(split, vertical ? 0 : index + 1); zone.Children.Add(split);
                split.DragCompleted += (_, _) => { for (int j = 0; j < items.Length; j++) items[j].Size = vertical ? zone.RowDefinitions[j * 2].Height.Value : zone.ColumnDefinitions[j * 2].Width.Value; Save(); };
            }
            // Resize the whole dock without restricting the canvas to a fixed sidebar width.
            var grip = new Thumb { Name = "DockResize" + side, Width = vertical ? 7 : double.NaN, Height = vertical ? double.NaN : 7, Background = Brushes.Transparent, Cursor = vertical ? EditorCursors.SizeWE : EditorCursors.SizeNS, Template = ResizeGripTemplate(vertical), HorizontalAlignment = vertical ? (side == Dock.Left ? HorizontalAlignment.Right : HorizontalAlignment.Left) : HorizontalAlignment.Stretch, VerticalAlignment = vertical ? VerticalAlignment.Stretch : (side == Dock.Top ? VerticalAlignment.Bottom : VerticalAlignment.Top) };
            ToolTip.SetTip(grip, LocalText.T(vertical ? "Drag to resize sidebar width" : "Drag to resize panel height"));
            grip.DragCompleted += (_, _) => Save();
            // Dock size is adjusted directly so automatic outer grid dimensions remain valid.
            grip.IsEnabled = !IsLayoutLocked; grip.IsHitTestVisible = !IsLayoutLocked;
            grip.DragDelta += (_, e) => { if (IsLayoutLocked) return; if (vertical) zone.Width = Math.Clamp(zone.Bounds.Width + (side == Dock.Left ? e.Vector.X : -e.Vector.X), 90, Math.Max(90, Bounds.Width * 0.45)); else zone.Height = Math.Clamp(zone.Bounds.Height + (side == Dock.Top ? e.Vector.Y : -e.Vector.Y), 50, Math.Max(50, Bounds.Height * 0.4)); dockSizes[side] = vertical ? zone.Width : zone.Height; e.Handled = true; };
            SetRowSpan(grip, Math.Max(1, zone.RowDefinitions.Count)); SetColumnSpan(grip, Math.Max(1, zone.ColumnDefinitions.Count)); zone.Children.Add(grip);
        }
    }
    public void Reset()
    {
        if (IsLayoutLocked) return;
        foreach (var p in panes) { Detach(p); p.Side = p.Home; p.Size = p.HomeSize; p.Visible = true; }
        dockSizes[Dock.Left] = 110; dockSizes[Dock.Right] = 360; dockSizes[Dock.Top] = 112; dockSizes[Dock.Bottom] = 160;
        if (defaultLayout is { Count: > 0 }) ApplyLayout(defaultLayout);
        Rebuild();
    }
    public void Restore()
    {
        try
        {
            IReadOnlyList<LayoutEntry>? entries = File.Exists(layoutPath)
                ? JsonSerializer.Deserialize<List<LayoutEntry>>(File.ReadAllText(layoutPath)) : defaultLayout;
            if (entries is null) return;
            IsLayoutLocked = false;
            ApplyLayout(entries);
            IsLayoutLocked = entries.Any(e => e.Locked);
            Rebuild();
            LayoutLockChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            if (defaultLayout is { Count: > 0 }) ApplyLayout(defaultLayout);
            Rebuild();
        }
    }

    private void ApplyLayout(IReadOnlyList<LayoutEntry> entries)
    {
        // Panels added since this layout was saved inherit their shipped defaults.
        // Apply them first so saved dock sizes and existing panel choices still win.
        var missing = defaultLayout?.Where(e => entries.All(saved => saved.Title != e.Title)) ?? [];
        foreach (var e in missing.Concat(entries))
        {
            var p = panes.FirstOrDefault(p => p.Title == e.Title);
            if (p is null || !Enum.IsDefined(e.Side) || !double.IsFinite(e.Size) || e.Size <= 0) continue;
            Detach(p);
            p.Side = e.Side; p.Size = e.Size; p.Visible = e.Visible;
            if (double.IsFinite(e.DockSize) && e.DockSize > 0)
                dockSizes[e.Side] = Math.Clamp(e.DockSize, 50, 600);
            panes.Remove(p); panes.Add(p);
            if (e.Visible && e.Floating && double.IsFinite(e.Width) && double.IsFinite(e.Height) && e.Width >= 180 && e.Height >= 120)
                Float(p, e);
        }
    }
    private static IControlTemplate ResizeGripTemplate(bool vertical)
    {
        return new FuncControlTemplate<Thumb>((_, _) => new Border { Background = Line, Margin = vertical ? new Thickness(3, 0) : new Thickness(0, 3) });
    }
    public void AddSectionMenu(MenuItem view)
    {
        sectionsMenu = view;
        foreach (var p in panes) AddSectionItem(p);
    }
    private void AddSectionItem(Pane p)
    {
        if (sectionsMenu is null || sectionItems.ContainsKey(p.Title)) return;
        var item = new MenuItem { Header = LocalText.T(p.Title), ToggleType = MenuItemToggleType.CheckBox, IsChecked = p.Visible, StaysOpenOnClick = true };
        item.Click += (_, _) => SetVisible(p, item.IsChecked);
        sectionItems[p.Title] = item; sectionsMenu.Items.Add(item);
    }
    private void SetVisible(Pane p, bool visible)
    {
        if (IsLayoutLocked) return;
        Detach(p); p.Visible = visible;
        if (visible) p.Size = p.HomeSize;
        Rebuild();
    }
    private (Dock side, Pane? before) DropTarget(Point at)
    {
        foreach (var (side, zone) in zones)
        {
            if (zone.Bounds.Width < 1 || zone.Bounds.Height < 1) continue;
            var origin = zone.TranslatePoint(new Point(), this) ?? default;
            if (!new Rect(origin, new Size(zone.Bounds.Width, zone.Bounds.Height)).Contains(at)) continue;
            bool vertical = side is Dock.Left or Dock.Right;
            foreach (var p in panes.Where(p => p.Visible && p.EditorWindow is null && p.Side == side))
            {
                var pos = p.Frame.TranslatePoint(new Point(), this) ?? default;
                if ((vertical ? at.Y : at.X) < (vertical ? pos.Y + p.Frame.Bounds.Height / 2 : pos.X + p.Frame.Bounds.Width / 2)) return (side, p);
            }
            return (side, null);
        }
        var distances = new[] { (Dock.Left, at.X), (Dock.Right, Bounds.Width - at.X), (Dock.Top, at.Y), (Dock.Bottom, Bounds.Height - at.Y) };
        return (distances.MinBy(d => d.Item2).Item1, null);
    }
    private void ShowDropPreview(Dock side, Pane? before)
    {
        var zone = zones[side];
        bool vertical = side is Dock.Left or Dock.Right;
        Point origin; double width, height;
        if (zone.Bounds.Width > 1 && zone.Bounds.Height > 1)
        {
            origin = zone.TranslatePoint(new Point(), this) ?? default; width = zone.Bounds.Width; height = zone.Bounds.Height;
            if (before is not null) { var pos = before.Frame.TranslatePoint(new Point(), this) ?? default; if (vertical) { origin = new Point(origin.X, pos.Y); height = Math.Max(40, before.Frame.Bounds.Height / 2); } else { origin = new Point(pos.X, origin.Y); width = Math.Max(80, before.Frame.Bounds.Width / 2); } }
            else { if (vertical) { height = Math.Min(height, 100); origin = new Point(origin.X, origin.Y + zone.Bounds.Height - height); } else { width = Math.Min(width, 180); origin = new Point(origin.X + zone.Bounds.Width - width, origin.Y); } }
        }
        else
        {
            width = vertical ? Math.Min(dockSizes[side], Bounds.Width / 3) : Bounds.Width;
            height = vertical ? Bounds.Height : Math.Min(dockSizes[side], Bounds.Height / 3);
            origin = new Point(side == Dock.Right ? Bounds.Width - width : 0, side == Dock.Bottom ? Bounds.Height - height : 0);
        }
        Canvas.SetLeft(dropPreview, origin.X); Canvas.SetTop(dropPreview, origin.Y); dropPreview.Width = width; dropPreview.Height = height;
        (((TextBlock)dropPreview.Child)).Text = string.Concat(string.Concat(LocalText.T("Dock "), LocalText.T(side.ToString())), ((before is null ? ("") : string.Concat(LocalText.T(" / Before "), LocalText.T(before.Title)))));
        dropOverlay.IsVisible = true;
    }
    private void Save()
    {
        try { AtomicFile.WriteJson(layoutPath, panes.Select(p => new LayoutEntry(p.Title, p.Side, p.Size, p.EditorWindow is not null, p.EditorWindow?.Left ?? 0, p.EditorWindow?.Top ?? 0, p.EditorWindow?.Bounds.Width ?? 380, p.EditorWindow?.Bounds.Height ?? 450, dockSizes[p.Side], p.Visible, 2, IsLayoutLocked))); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
