using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CrystalStudio.Rendering;

namespace CrystalStudio.Tests;

public sealed class ModuleInteractionTests
{
    private static List<StampCell> Cells(int ox = 0, int oy = 0) => Enumerable.Range(0, 8).SelectMany(y => Enumerable.Range(0, 10)
        .Select(x => new StampCell(x + ox, y + oy, new Cell { MiddleLibrary = 1, MiddleImage = 1 }))).ToList();
    private static ModuleConnections Ports(int ox = 0, int oy = 0) => new(1, [
        new("north", ox + 2, oy, ModuleEdge.North, "path", 3), new("south", ox + 2, oy + 7, ModuleEdge.South, "path", 3)]);
    private static Point At(ModuleConnectionsWindow window, double x, double y) =>
        window.Editor.Canvas.TranslatePoint(window.Editor.Canvas.Screen(x + .5, y + .5), window)!.Value;
    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, button));
    private static void Key(EditorWindow window, PhysicalKey key, RawInputModifiers modifiers = RawInputModifiers.None)
    { window.KeyPressQwerty(key, modifiers); window.KeyReleaseQwerty(key, modifiers); Dispatcher.UIThread.RunJobs(); }
    private static void Drag(ModuleConnectionsWindow window, int ax, int ay, int bx, int by)
    {
        var a = At(window, ax, ay); var b = At(window, bx, by);
        window.MouseDown(a, MouseButton.Left); window.MouseMove(b, RawInputModifiers.LeftMouseButton); window.MouseUp(b, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
    }
    private static byte[] Pixels(EditorWindow window, string? artifact = null)
    {
        Dispatcher.UIThread.RunJobs(); using var frame = window.CaptureRenderedFrame(); Assert.NotNull(frame);
        if (artifact != null && Environment.GetEnvironmentVariable("CRYSTALSTUDIO_MODULE_UX_ARTIFACTS") is { Length: > 0 } directory)
        { Directory.CreateDirectory(directory); frame.Save(Path.Combine(directory, artifact + ".png")); }
        using var stream = new MemoryStream(); frame.Save(stream); stream.Position = 0;
        using var image = RasterImage.Load(stream); return Fixture.Pixels(image);
    }
    private static int ColorCount(byte[] pixels, byte b, byte g, byte r) =>
        Enumerable.Range(0, pixels.Length / 4).Count(i => pixels[4 * i] == b && pixels[4 * i + 1] == g && pixels[4 * i + 2] == r);

    [AvaloniaFact]
    public void NewModeAndReverseDraggingHaveVisibleFeedbackAndOneUndoStep()
    {
        var cells = Cells(); var window = new ModuleConnectionsWindow(cells, null); window.Show(); Dispatcher.UIThread.RunJobs();
        try
        {
            byte[] before = Pixels(window);
            Click(window.Editor.NewButton); Assert.True(window.Editor.State.Adding); Assert.NotNull(window.Editor.Canvas.Draft);
            window.MouseMove(At(window, 8, 0));
            byte[] draft = Pixels(window, "connection-draft"); Assert.False(before.SequenceEqual(draft));
            Assert.True(ColorCount(draft, 0, 165, 255) > 10); // Orange outline and labelled ghost.
            Assert.Empty(window.Editor.State.Entries);
            Drag(window, 8, 0, 3, 0);
            var point = Assert.Single(window.Editor.State.Entries).Point;
            Assert.Equal((3, 0, 6, ModuleEdge.North), (point.X, point.Y, point.Width, point.Edge));
            Assert.Equal(point.Id, window.Editor.Canvas.SelectedId); Assert.Null(window.Editor.Canvas.Draft);
            byte[] selected = Pixels(window, "connection-selected"); Assert.True(ColorCount(selected, 255, 255, 0) > 10);
            Key(window, PhysicalKey.Z, RawInputModifiers.Control); Assert.Empty(window.Editor.State.Entries);
            Key(window, PhysicalKey.Y, RawInputModifiers.Control); Assert.Single(window.Editor.State.Entries);
            window.ConnectionsList.Focus(); Key(window, PhysicalKey.Z, RawInputModifiers.Control); Assert.Empty(window.Editor.State.Entries);
            Key(window, PhysicalKey.Y, RawInputModifiers.Control); Assert.Single(window.Editor.State.Entries);
            Assert.Equal(cells, window.Editor.State.Cells); Assert.False(window.Editor.Canvas.Viewport.History.Dirty);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void SelectionIsBidirectionalAndDisabledConnectionsRemainEditableAndVisible()
    {
        var window = new ModuleConnectionsWindow(Cells(), Ports()); window.Show(); Dispatcher.UIThread.RunJobs();
        try
        {
            window.ConnectionsList.SelectedIndex = 1; Assert.Equal("south", window.Editor.Canvas.SelectedId);
            var p = At(window, 3, 0); window.MouseDown(p, MouseButton.Left); window.MouseUp(p, MouseButton.Left);
            Assert.Equal("north", window.Editor.State.SelectedId); Assert.Equal(0, window.ConnectionsList.SelectedIndex);
            // Use the actual checkbox, not a direct state mutation.
            var check = window.ConnectionsList.GetVisualDescendants().OfType<CheckBox>().First();
            var location = check.TranslatePoint(new Point(check.Bounds.Width / 2, check.Bounds.Height / 2), window)!.Value;
            window.MouseDown(location, MouseButton.Left); window.MouseUp(location, MouseButton.Left); Dispatcher.UIThread.RunJobs();
            Assert.Contains("north", window.Editor.Canvas.Disabled); Assert.Equal(2, window.Editor.Canvas.Points.Count);
            Assert.Equal("north", window.Editor.State.SelectedId);
            window.KindBox.Text = "road"; Assert.True(window.StoreDraft());
            Assert.Equal("road", window.Editor.State.Selected!.Kind);
            Assert.True(window.TryApply()); Assert.Equal("south", Assert.Single(window.Result!.Points).Id);
            Pixels(window, "connection-disabled");
            Click(window.Editor.NewButton); window.MouseMove(At(window, 7, 0));
            Assert.NotNull(window.Editor.State.Draft); Assert.Null(window.Editor.State.Problem);
            var enable = window.ConnectionsList.GetVisualDescendants().OfType<CheckBox>().First();
            var enableAt = enable.TranslatePoint(new Point(enable.Bounds.Width / 2, enable.Bounds.Height / 2), window)!.Value;
            window.MouseDown(enableAt, MouseButton.Left); window.MouseUp(enableAt, MouseButton.Left);
            Assert.Equal(2, window.Editor.State.Entries.Count); Assert.Null(window.Editor.State.Draft);
            Assert.Equal(2, window.Editor.State.Connections.Points.Count);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void BodyDragAndEndpointDragModifyOnlyTheChosenConnection()
    {
        var window = new ModuleConnectionsWindow(Cells(), Ports()); window.Show(); Dispatcher.UIThread.RunJobs();
        try
        {
            Drag(window, 3, 0, 5, 0); Assert.Equal(4, window.Editor.State.Selected!.X); Assert.Equal(3, window.Editor.State.Selected.Width);
            Drag(window, 6, 0, 8, 0); Assert.Equal(5, window.Editor.State.Selected!.Width);
            Drag(window, 4, 0, 2, 0); Assert.Equal(2, window.Editor.State.Selected!.X); Assert.Equal(7, window.Editor.State.Selected.Width);
            Assert.Equal(Ports().Points[1], window.Editor.State.Entries[1].Point);
            Key(window, PhysicalKey.Z, RawInputModifiers.Control); Assert.Equal(4, window.Editor.State.Selected!.X); Assert.Equal(5, window.Editor.State.Selected.Width);
            Key(window, PhysicalKey.Delete); Assert.Single(window.Editor.State.Entries);
            Key(window, PhysicalKey.Z, RawInputModifiers.Control); Assert.Equal(2, window.Editor.State.Entries.Count);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void InvalidInputAndCollisionDraftStayVisibleUntilCorrectedOrCancelled()
    {
        var cells = Cells(); cells.Single(c => c.X == 7 && c.Y == 0).Cell.BackImage = 0x20000000;
        var window = new ModuleConnectionsWindow(cells, Ports()); window.Show(); Dispatcher.UIThread.RunJobs();
        try
        {
            window.WidthBox.Text = "invalid"; window.ConnectionsList.SelectedIndex = 1;
            Assert.Equal("north", window.Editor.State.SelectedId); Assert.Equal("invalid", window.WidthBox.Text);
            Assert.False(window.TryApply()); Assert.NotNull(window.Editor.State.Problem);
            window.WidthBox.Focus(); Key(window, PhysicalKey.Escape); Assert.Null(window.Editor.State.Problem); Assert.Equal("3", window.WidthBox.Text);
            Click(window.Editor.NewButton); Drag(window, 6, 0, 8, 0);
            Assert.NotNull(window.Editor.State.Problem); Assert.Equal("blocked", window.Editor.State.Problem!.Code);
            Assert.Equal(7, window.Editor.State.Problem.X); Assert.True(window.Editor.Canvas.InvalidDraft);
            Assert.False(window.TryApply()); Assert.Equal(2, window.Editor.State.Entries.Count);
            byte[] invalid = Pixels(window, "connection-invalid"); Assert.True(ColorCount(invalid, 0, 0, 255) > 10);
            Key(window, PhysicalKey.Escape); Assert.Null(window.Editor.State.Draft); Assert.Null(window.Editor.State.Problem);
            Assert.True(window.TryApply());
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void NavigationAndNegativeCoordinatesKeepTheOverlayAlignedAndCannotPaint()
    {
        var window = new ModuleConnectionsWindow(Cells(-6, -4), Ports(-6, -4)); window.Show(); Dispatcher.UIThread.RunJobs();
        try
        {
            var canvas = window.Editor.Canvas; long revision = canvas.Viewport.Document.PickupRevision;
            var before = canvas.Screen(-3.5, -3.5); var location = canvas.TranslatePoint(before, window)!.Value;
            window.MouseDown(location, MouseButton.Middle); window.MouseMove(location + new Vector(50, 30), RawInputModifiers.MiddleMouseButton);
            window.MouseUp(location + new Vector(50, 30), MouseButton.Middle);
            var after = canvas.Screen(-3.5, -3.5); Assert.Equal(before.X + 50, after.X, 2); Assert.Equal(before.Y + 30, after.Y, 2);
            double zoom = canvas.Viewport.Zoom;
            window.MouseWheel(canvas.TranslatePoint(after, window)!.Value, new Vector(0, -1), RawInputModifiers.None);
            Assert.True(canvas.Viewport.Zoom < zoom); Assert.Equal((-4, -4), canvas.CellAt(canvas.Screen(-3.5, -3.5)));
            var p = At(window, -3, -4); window.MouseDown(p, MouseButton.Left); window.MouseUp(p, MouseButton.Left);
            Assert.Equal("north", canvas.SelectedId); Assert.Equal(revision, canvas.Viewport.Document.PickupRevision);
            Assert.False(canvas.Viewport.History.Dirty); canvas.Fit();
            Assert.Equal((-4, -4), canvas.CellAt(canvas.Screen(-3.5, -3.5)));
        }
        finally { window.Close(); }
    }

    [Fact]
    public void DisabledDraftsAndDuplicateIdsRemainIndependentOfSavedMetadata()
    {
        var saved = Ports(); var state = new ModuleConnectionEditorState(Cells(), saved);
        state.Toggle("north", false); state.Select("north");
        state.Preview(saved.Points[0] with { Id = "south" }, "north"); Assert.Equal("duplicate", state.Problem!.Code); Assert.False(state.Commit());
        state.CancelDraft(); state.Preview(saved.Points[0] with { Kind = "road" }, "north"); Assert.True(state.Commit());
        Assert.Single(state.Connections.Points); Assert.Equal("path", saved.Points[0].Kind);
        state.Toggle("north", true); Assert.Equal(2, state.Connections.Points.Count);
        state.Undo(); Assert.Single(state.Connections.Points);
    }

    [AvaloniaFact]
    public void SingleCellCornersCanBePlacedAndResizedUsingTheirVisibleEndHandle()
    {
        var window = new ModuleConnectionsWindow(Cells(), null); window.Show(); Dispatcher.UIThread.RunJobs();
        try
        {
            Click(window.Editor.NewButton); Drag(window, 0, 0, 0, 0);
            Assert.Equal(ModuleEdge.North, window.Editor.State.Selected!.Edge); Assert.Equal(1, window.Editor.State.Selected.Width);
            var canvas = window.Editor.Canvas;
            var handle = canvas.TranslatePoint(canvas.Screen(.92, .5), window)!.Value;
            window.MouseDown(handle, MouseButton.Left); window.MouseMove(At(window, 3, 0), RawInputModifiers.LeftMouseButton);
            window.MouseUp(At(window, 3, 0), MouseButton.Left);
            Assert.Equal(4, window.Editor.State.Selected!.Width); Assert.Equal(0, window.Editor.State.Selected.X);
            window.KindBox.Focus(); window.KindBox.SelectAll(); Key(window, PhysicalKey.Delete);
            Assert.Single(window.Editor.State.Entries); Assert.False(window.TryApply());
            Key(window, PhysicalKey.Escape); Assert.Equal("path", window.KindBox.Text); Assert.True(window.TryApply());
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void LosingPointerCaptureDiscardsOnlyTheUnfinishedGesture()
    {
        var window = new ModuleConnectionsWindow(Cells(), Ports()); window.Show(); Dispatcher.UIThread.RunJobs();
        try
        {
            IPointer? pointer = null;
            window.Editor.Canvas.AddHandler(InputElement.PointerPressedEvent, (_, e) => pointer = e.Pointer, RoutingStrategies.Tunnel);
            window.MouseDown(At(window, 3, 0), MouseButton.Left); window.MouseMove(At(window, 6, 0), RawInputModifiers.LeftMouseButton);
            Assert.NotNull(window.Editor.State.Draft); Assert.NotNull(pointer); pointer.Capture(null);
            Assert.Null(window.Editor.State.Draft); Assert.Equal(Ports().Points, window.Editor.State.Connections.Points);
            window.MouseUp(At(window, 6, 0), MouseButton.Left); Assert.Equal(Ports().Points, window.Editor.State.Connections.Points);
        }
        finally { window.Close(); }
    }

    [Fact]
    public void DraftValidationRejectsTooWideOverlappingAndOffBoundaryOpenings()
    {
        var state = new ModuleConnectionEditorState(Cells(), Ports());
        state.Preview(new("new", 0, 0, ModuleEdge.North, "path", 65), null); Assert.Equal("definition", state.Problem!.Code);
        state.Preview(new("new", 3, 0, ModuleEdge.North, "path"), null); Assert.Equal("overlap", state.Problem!.Code);
        state.Preview(new("new", 7, 2, ModuleEdge.North, "path"), null); Assert.Equal("edge", state.Problem!.Code);
        Assert.False(state.Commit()); state.CancelDraft(); Assert.Equal(Ports().Points, state.Connections.Points);
    }

    [AvaloniaFact]
    public async Task SearchPreservesSelectionAndStaleOrFailedPreviewsCannotApply()
    {
        using var fixture = new PoolFixture();
        var dialog = new ModuleGenerationWindow(fixture.Index, null); dialog.Show();
        try
        {
            dialog.SelectModules(["room"]); dialog.SearchBox.Text = "terminal";
            Assert.Single(dialog.PoolList.Items); Assert.Equal(new[] { "room" }, dialog.SelectedModuleIds);
            dialog.SelectSearchResults(true); Assert.Equal(2, dialog.SelectedModuleIds.Count);
            dialog.SelectSearchResults(false); Assert.Equal(new[] { "room" }, dialog.SelectedModuleIds);
            dialog.SearchBox.Text = "stone"; Assert.Single(dialog.PoolList.Items);
            await dialog.GeneratePreviewAsync(); Assert.Equal(ModuleGenerationViewState.Ready, dialog.ViewState);
            Assert.Contains(LocalText.Format("Requested {0} · Placed {1} · Types {2} · Reused {3} · Links {4} · Joined ports {5}",
                12, dialog.Plan!.Placement.Placements.Count, 1, 11, dialog.Plan.Links.Count, dialog.Plan.Placement.Openings.Count), dialog.Feedback.Text);
            var candidates = dialog.GetVisualDescendants().OfType<ComboBox>().Single(control => control.Name == "ModuleGenerationCandidates");
            Assert.Single(candidates.Items);
            var displayed = dialog.DisplayedPlan; var preview = dialog.Preview;
            dialog.SeedBox.Text = "21"; Assert.Equal(ModuleGenerationViewState.Stale, dialog.ViewState);
            Assert.Same(displayed, dialog.DisplayedPlan); Assert.Same(preview, dialog.Preview); Assert.False(dialog.TryAccept());
            dialog.CountBox.Text = "bad"; await dialog.GeneratePreviewAsync(); Assert.Equal(ModuleGenerationViewState.Failed, dialog.ViewState);
            Assert.Same(preview, dialog.Preview); Assert.False(dialog.ApplyButton.IsEnabled);
            Pixels(dialog, "generation-stale");
            dialog.CountBox.Text = "4"; await dialog.GeneratePreviewAsync(); Assert.True(dialog.TryAccept());
            Assert.Equal(2, candidates.Items.Count);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(4, dialog.Preview!.Regions.Count);
            var region = dialog.Preview.Regions[0].Bounds;
            var click = dialog.Preview.TranslatePoint(dialog.Preview.Screen(region.Left + .5, region.Top + .5), dialog)!.Value;
            dialog.MouseDown(click, MouseButton.Left); dialog.MouseUp(click, MouseButton.Left);
            Assert.True(dialog.Preview.Regions[0].Selected); Pixels(dialog, "generation-ready");
            var prior = dialog.Preview;
            dialog.CountBox.Text = "128";
            var work = dialog.GeneratePreviewAsync();
            dialog.GetVisualDescendants().OfType<Button>().Single(b => Equals(b.Content, LocalText.T("Stop generation")))
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await work; Assert.False(dialog.TryAccept()); Assert.Same(prior, dialog.Preview);
            candidates.SelectedIndex = 1;
            dialog.GetVisualDescendants().OfType<Button>().Single(control => control.Name == "RestoreModuleCandidate")
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await EditorExperienceTests.WaitFor(() => dialog.ViewState == ModuleGenerationViewState.Ready);
            Assert.Equal("12", dialog.CountBox.Text); Assert.Equal(12, dialog.Plan!.Placement.Placements.Count);
        }
        finally { dialog.Close(); }
    }

    [AvaloniaFact]
    public async Task InvalidModuleDiagnosticsIdentifyTheModuleConnectionAndCell()
    {
        using var fixture = new PoolFixture();
        var entry = fixture.Index.Objects[0];
        var bad = entry with { Connections = entry.Connections! with { Points = [new("broken", 1, 1, ModuleEdge.North, "path")] } };
        var dialog = new ModuleGenerationWindow(fixture.Index with { Objects = [bad] }, null); dialog.Show();
        try
        {
            await dialog.GeneratePreviewAsync(); Assert.Equal(ModuleGenerationViewState.Failed, dialog.ViewState);
            Assert.Equal("room", dialog.Problem!.ModuleId); Assert.Equal("broken", dialog.Problem.ConnectionId);
            Assert.Equal((1, 1), (dialog.Problem.X, dialog.Problem.Y));
            Assert.Equal("room", ((ModulePoolItem)dialog.PoolList.SelectedItem!).Entry.Id); Assert.False(dialog.TryAccept());
        }
        finally { dialog.Close(); }
    }

    [AvaloniaFact]
    public async Task PoolThumbnailsAndDimensionsLoadForVisibleModules()
    {
        using var fixture = new PoolFixture(); using var assets = AssetStore.Load(Path.Combine(TestApplication.Root, "Data"));
        using var item = new ModulePoolItem(fixture.Index, fixture.Index.Objects[0], assets);
        await item.LoadAsync(); Assert.NotNull(item.Thumbnail); Assert.Contains("10 × 8", item.Summary);
        Assert.True(item.Matches("stone")); Assert.True(item.Matches("PATH")); Assert.False(item.Matches("missing"));
    }

    private sealed class PoolFixture : IDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "module-interaction-" + Guid.NewGuid().ToString("N"));
        internal StampCategoryIndex Index { get; }
        internal PoolFixture()
        {
            Directory.CreateDirectory(root); CellCodec.SaveStamp(Path.Combine(root, "room.X"), Cells());
            var room = new CategorizedStamp("room", "room.X", "Stone room", "Module", null, [], [], [], ["stone"], null, "unknown", null, false, "reviewed") { Connections = Ports() };
            Index = new(1, root, root, [room, room with { Id = "terminal", Name = "Terminal", Tags = ["end"] }]);
        }
        public void Dispose() => Directory.Delete(root, true);
    }
}
