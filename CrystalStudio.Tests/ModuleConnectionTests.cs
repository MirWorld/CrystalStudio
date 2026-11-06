using System.Reflection;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace CrystalStudio.Tests;

public sealed class ModuleConnectionTests
{
    private static List<StampCell> Room(int x = 0, int y = 0, bool ground = false) =>
        Enumerable.Range(y, 5).SelectMany(row => Enumerable.Range(x, 5)
            .Select(column => new StampCell(column, row, ground ? new Cell { BackImage = 1 } : new Cell { MiddleImage = 1 }))).ToList();
    private static ModuleConnections One(ModuleConnection? point = null) => new(1, [point ?? new("entry", 1, 0, ModuleEdge.North, "path", 2)]);
    private static CategorizedStamp Category(ModuleConnections? connections = null) =>
        new("room", "room.X", "Room", null, null, [], [], [], [], null, "unknown", null, false, "unreviewed") { Connections = connections };

    [Theory]
    [InlineData(ModuleEdge.North, 1, 0)]
    [InlineData(ModuleEdge.East, 4, 1)]
    [InlineData(ModuleEdge.South, 1, 4)]
    [InlineData(ModuleEdge.West, 0, 1)]
    public void OpeningsValidateOnEachEdge(ModuleEdge edge, int x, int y)
    {
        ModuleConnectionRules.Validate(One(new("entry", x, y, edge, "path", 2)), Room());
    }

    [Theory]
    [InlineData(0x20000000, false, 0)]
    [InlineData(0x40000000, false, 0)]
    [InlineData(0x20000000, false, 1)]
    [InlineData(0, true, 0)]
    [InlineData(0, true, 1)]
    public void OpeningAndInwardCellMustBeWalkable(int back, bool front, int y)
    {
        var cells = Room();
        var cell = cells.Single(c => c.X == 2 && c.Y == y).Cell;
        cell.BackImage = back;
        if (front) cell.Foreground = new(2, unchecked((short)0x8001));
        Assert.Throws<InvalidDataException>(() => ModuleConnectionRules.Validate(One(), cells));
    }

    [Fact]
    public void OrdinaryArtworkAndSparseInteriorDoNotBlockAnOpening()
    {
        var cells = Room();
        cells.Single(c => c.X == 1 && c.Y == 0).Cell.Foreground = new(2, 1);
        cells.RemoveAll(c => c.X is 1 or 2 && c.Y == 1);
        ModuleConnectionRules.Validate(One(), cells);
    }

    public static IEnumerable<object[]> InvalidPoints()
    {
        var valid = One().Points[0];
        yield return [valid with { Id = "" }];
        yield return [valid with { Id = " entry" }];
        yield return [valid with { Kind = "path\n" }];
        yield return [valid with { Width = 0 }];
        yield return [valid with { Width = 65 }];
        yield return [valid with { Edge = (ModuleEdge)100 }];
        yield return [valid with { Y = 1 }];
        yield return [valid with { X = 4 }];
        yield return [valid with { X = int.MaxValue }];
    }

    [Theory]
    [MemberData(nameof(InvalidPoints))]
    public void InvalidFieldsAndOffBoundaryOpeningsAreRejected(ModuleConnection point) =>
        Assert.Throws<InvalidDataException>(() => ModuleConnectionRules.Validate(One(point), Room()));

    [Fact]
    public void DuplicateIdsOverlapAndUnsupportedMetadataAreRejected()
    {
        var first = One().Points[0];
        foreach (var second in new[] { first with { Id = "ENTRY", Y = 4, Edge = ModuleEdge.South }, first with { Id = "other", X = 2 } })
            Assert.Throws<InvalidDataException>(() => ModuleConnectionRules.Validate(new(1, [first, second]), Room()));
        Assert.Throws<InvalidDataException>(() => ModuleConnectionRules.Validate(new(2, []), Room()));
        Assert.Throws<InvalidDataException>(() => ModuleConnectionRules.Validate(new(1, null!), Room()));
        Assert.Throws<InvalidDataException>(() => ModuleConnectionRules.Validate(new(1, [null!]), Room()));
        Assert.Throws<InvalidDataException>(() => ModuleConnectionRules.Validate(new(1, Enumerable.Repeat(first, 65).ToList()), Room()));
        var cells = Room(); cells.Add(cells[0]);
        Assert.Throws<InvalidDataException>(() => ModuleConnectionRules.Validate(One(), cells));
    }

    [Fact]
    public void DeletingGeometryRequiresRepairingOrClearingConnections()
    {
        var cells = Room(); cells.RemoveAll(c => c.Y == 0);
        Assert.Throws<InvalidDataException>(() => ModuleConnectionRules.PrepareSave(cells, One()));
        Assert.Throws<InvalidDataException>(() => ModuleConnectionRules.PrepareSave([], One()));
        Assert.Empty(ModuleConnectionRules.PrepareSave([], new(1, [])).Cells);
        Assert.Null(ModuleConnectionRules.PrepareSave([], null).Connections);
    }

    [Theory]
    [InlineData(12, 7, false)]
    [InlineData(-7, -5, false)]
    [InlineData(-7, -5, true)]
    public void SaveReloadAndRepeatedSaveKeepConnectionsOnTheSameCells(int x, int y, bool ground)
    {
        string root = Path.Combine(Path.GetTempPath(), "CrystalStudio-connections-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "catalogue"));
        Directory.CreateDirectory(Path.Combine(root, "Objects"));
        try
        {
            var source = Room(x, y, ground);
            var connections = One(new("entry", x + 1, y, ModuleEdge.North, "path", 2));
            var prepared = ModuleConnectionRules.PrepareSave(source, connections);
            if (ground) { Assert.Equal(0, prepared.OffsetX % 2); Assert.Equal(0, prepared.OffsetY % 2); }
            var point = Assert.Single(prepared.Connections!.Points);
            Assert.Equal(x + 1 - prepared.OffsetX, point.X); Assert.Equal(y - prepared.OffsetY, point.Y);
            string path = Path.Combine(root, "Objects", "room.X");
            CellCodec.SaveStamp(path, prepared.Cells);
            var index = new StampCategoryIndex(1, "Objects", "previews", [Category(prepared.Connections)]);
            string catalogue = Path.Combine(root, "catalogue", "palette.json");
            File.WriteAllBytes(catalogue, StudioJson.SerializeToUtf8Bytes(index, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
            var reopened = StampCategories.Read(catalogue).Objects[0];
            var reloadedCells = CellCodec.LoadStamp(path);
            ModuleConnectionRules.Validate(reopened.Connections, reloadedCells);
            Assert.Equal(point, Assert.Single(reopened.Connections!.Points));
            // The live editor retains its canvas and adjusts its origin by the same crop offset.
            var liveCells = source.Select(c => c with { X = c.X - prepared.OffsetX, Y = c.Y - prepared.OffsetY }).ToList();
            var again = ModuleConnectionRules.PrepareSave(liveCells, prepared.Connections);
            Assert.Equal(0, again.OffsetX); Assert.Equal(0, again.OffsetY);
            Assert.Equal(point, Assert.Single(again.Connections!.Points));
            Assert.Equal(reloadedCells, again.Cells);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void LegacyCategoryJsonStaysCompatibleAndEmptyConnectionsPersist()
    {
        var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        byte[] old = StudioJson.SerializeToUtf8Bytes(Category(), options);
        Assert.DoesNotContain("connections", System.Text.Encoding.UTF8.GetString(old));
        Assert.Null(StudioJson.Deserialize<CategorizedStamp>(old, options)!.Connections);
        var cleared = StudioJson.Deserialize<CategorizedStamp>(StudioJson.SerializeToUtf8Bytes(Category(new(1, [])), options), options)!;
        Assert.Empty(cleared.Connections!.Points);
    }

    [Fact]
    public void RecoveryKeepsUnsavedConnectionMetadataAndOrigin()
    {
        string root = Path.Combine(Path.GetTempPath(), "CrystalStudio-connection-recovery-" + Guid.NewGuid().ToString("N"));
        try
        {
            using (var writer = new MapRecoveryStore(root))
            {
                var document = new MapDocument(16, 16); document.Set(8, 8, new Cell { MiddleImage = 1 });
                var metadata = new MapRecoveryEntry(1, writer.SessionId, Guid.NewGuid(), "", null, "room", DateTime.UtcNow,
                    AssetSourceFormat.Default, null, true, -8, -8, Category(One()), true);
                writer.Write(metadata.Id, document, null, "room", null, metadata);
            }
            using var reader = new MapRecoveryStore(root);
            var restored = Assert.Single(reader.List());
            Assert.True(restored.CategoryDirty); Assert.True(restored.IsStamp);
            Assert.Equal(-8, restored.OriginX); Assert.Equal(-8, restored.OriginY);
            Assert.Equal(One().Points, restored.Category!.Connections!.Points);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [AvaloniaFact]
    public void DialogAddsUpdatesRemovesAndDoesNotMutateInputOnCancel()
    {
        var original = One();
        var dialog = new ModuleConnectionsWindow(Room(), original);
        dialog.ConnectionsList.SelectedIndex = 0;
        dialog.KindBox.Text = "road";
        Assert.True(dialog.StoreDraft());
        Assert.Equal("path", original.Points[0].Kind);
        Assert.Null(dialog.Result);
        dialog.Close();

        var accepted = new ModuleConnectionsWindow(Room(), original);
        accepted.ConnectionsList.SelectedIndex = 0;
        accepted.KindBox.Text = "door";
        Assert.True(accepted.TryApply()); // Apply must include the edited form without a separate Update click.
        Assert.Equal("door", Assert.Single(accepted.Result!.Points).Kind);
        Assert.Equal("path", original.Points[0].Kind);
        accepted.Show(); Dispatcher.UIThread.RunJobs(); accepted.ConnectionsList.SelectedIndex = 0;
        Assert.Equal(0, accepted.ConnectionsList.SelectedIndex);
        Click(accepted.GetVisualDescendants().OfType<Button>().Single(b => Equals(b.Content, LocalText.T("Remove connection"))));
        Assert.Empty(accepted.ConnectionsList.Items);
        Assert.True(accepted.TryApply()); Assert.Empty(accepted.Result!.Points);
        accepted.Close();

        var added = new ModuleConnectionsWindow(Room(), null);
        added.XBox.Text = "2";
        Assert.True(added.TryApply()); Assert.Single(added.Result!.Points);
        added.Close();
    }

    [AvaloniaFact]
    public void DialogRejectsInvalidDraftAndCanClearConnectionsFromAnEmptyObject()
    {
        var dialog = new ModuleConnectionsWindow(Room(), One());
        dialog.ConnectionsList.SelectedIndex = 0; dialog.WidthBox.Text = "100";
        Assert.False(dialog.TryApply()); Assert.Null(dialog.Result);
        Assert.False(string.IsNullOrWhiteSpace(dialog.Feedback.Text)); dialog.Close();
        var empty = new ModuleConnectionsWindow([], One()); empty.Show(); Dispatcher.UIThread.RunJobs();
        empty.ConnectionsList.SelectedIndex = 0;
        Click(empty.GetVisualDescendants().OfType<Button>().Single(b => Equals(b.Content, LocalText.T("Remove connection"))));
        Assert.True(empty.TryApply()); Assert.Empty(empty.Result!.Points); empty.Close();
    }

    [AvaloniaFact]
    public void ClickingThePreviewSelectsTheCorrectBoundaryCell()
    {
        var dialog = new ModuleConnectionsWindow(Room(), null); dialog.Show();
        try
        {
            Dispatcher.UIThread.RunJobs();
            var preview = dialog.GetVisualDescendants().OfType<ModuleConnectionCanvas>().Single();
            double scale = Math.Min((preview.Bounds.Width - 24) / 5, (preview.Bounds.Height - 24) / (5.0 * 2 / 3));
            double top = (preview.Bounds.Height - 5 * scale * 2 / 3) / 2;
            var point = preview.TranslatePoint(new Point(preview.Bounds.Width / 2, top + scale / 3), dialog)!.Value;
            dialog.MouseDown(point, MouseButton.Left); dialog.MouseUp(point, MouseButton.Left);
            Assert.Equal("2", dialog.XBox.Text); Assert.Equal("0", dialog.YBox.Text);
            Assert.True(dialog.TryApply());
            Assert.Equal(2, Assert.Single(dialog.Result!.Points).X);
        }
        finally { dialog.Close(); }
    }

    [AvaloniaFact]
    public async Task StampToolbarAppliesMetadataAndMarksOnlyStampCategoryDirty()
    {
        var window = new MainWindow(new AssetHistoryStore(Path.Combine(TestApplication.Root, "connections-assets.json")),
            new RecentMapStore(Path.Combine(TestApplication.Root, "connections-maps.json")));
        window.Show();
        object? map = null;
        try
        {
            for (int attempt = 0; attempt < 500 && (window.FindControl<MapViewport>("Viewport")!.Assets == null || !window.FindControl<MenuItem>("NewButton")!.IsEnabled); attempt++)
            { await Task.Delay(20); Dispatcher.UIThread.RunJobs(); }
            Assert.True(window.FindControl<MenuItem>("NewButton")!.IsEnabled);
            var button = window.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "ModuleConnectionsButton");
            Assert.False(button.IsEffectivelyVisible);
            Invoke(window, "NewStampClick", window, new RoutedEventArgs());
            Dispatcher.UIThread.RunJobs();
            Assert.True(button.IsEffectivelyVisible);
            var viewport = window.FindControl<MapViewport>("Viewport")!;
            foreach (var cell in Room(32, 32)) viewport.Document.Set(cell.X, cell.Y, cell.Cell);
            map = typeof(MainWindow).GetField("activeMap", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(window)!;
            using (Window.WindowOpenedEvent.AddClassHandler<ModuleConnectionsWindow>((dialog, _) =>
                Dispatcher.UIThread.Post(() => { dialog.XBox.Text = "1"; Assert.True(dialog.TryApply()); dialog.DialogResult = true; })))
                Click(button);
            var type = map.GetType();
            var category = (CategorizedStamp)type.GetField("Category")!.GetValue(map)!;
            Assert.Single(category.Connections!.Points);
            Assert.True((bool)type.GetField("CategoryDirty")!.GetValue(map)!);
            Assert.False(viewport.History.Dirty);
            type.GetField("CategoryDirty")!.SetValue(map, false);
            using (Window.WindowOpenedEvent.AddClassHandler<ModuleConnectionsWindow>((dialog, _) =>
                Dispatcher.UIThread.Post(() => { dialog.ConnectionsList.SelectedIndex = 0; dialog.KindBox.Text = "discarded"; dialog.DialogResult = false; })))
                Click(button);
            Assert.Same(category, type.GetField("Category")!.GetValue(map));
            Assert.False((bool)type.GetField("CategoryDirty")!.GetValue(map)!);
            using (Window.WindowOpenedEvent.AddClassHandler<EditorWindow>((dialog, _) =>
            {
                if (dialog.Title != LocalText.T("Stamp Categories")) return;
                Dispatcher.UIThread.Post(() => Click(dialog.GetVisualDescendants().OfType<Button>().Single(b => Equals(b.Content, LocalText.T("Apply")))));
            })) Invoke(window, "EditStampCategories");
            Assert.Equal(category.Connections!.Points, ((CategorizedStamp)type.GetField("Category")!.GetValue(map)!).Connections!.Points);
        }
        finally
        {
            if (map != null) map.GetType().GetField("CategoryDirty")!.SetValue(map, false);
            window.FindControl<MapViewport>("Viewport")!.History.Saved(); window.Close();
        }
    }

    private static object? Invoke(object instance, string name, params object[] args) =>
        instance.GetType().GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(instance, args);

    private static void Click(Button button)
    {
        bool called = false;
        button.Click += (_, _) => called = true;
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, button));
        Assert.True(called, "Click event was not dispatched");
    }
}
