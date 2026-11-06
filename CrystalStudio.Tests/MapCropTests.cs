using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;

namespace CrystalStudio.Tests;

public sealed class MapCropTests
{
    [Fact]
    public void CropAlignsGroundPreservesLayersAndCompleteObjectsAndRoundTrips()
    {
        var source = new MapDocument(12, 10) { SourceFormat = AssetSourceFormat.Sanxiu };
        var cell = new Cell { BackImage = 0x60000001, MiddleLibrary = 1001, MiddleImage = 3,
            Foreground = new Front(2002, 4, 2, 3), Light = 5, Door = 1, DoorOffset = 2,
            MiddleFrames = 2, MiddleTick = 4, TileAnimation = 3, TileOffset = 4, TileFrames = 5 };
        source.Set(2, 2, cell);
        source.Set(10, 8, new Cell { MiddleImage = 7 });
        var piece = new List<StampCell> { new(0, 0, new Cell { Foreground = new Front(2002, 5) }) };
        var complete = source.PlaceStamp(piece, 4, 4, "complete", new(), (_, _) => { })!;
        var partial = source.PlaceStamp([..piece, new(1, 0, new Cell { Foreground = new Front(2002, 6) })],
            7, 4, "partial", new(), (_, _) => { })!;
        var crop = source.Crop(new Rect(3, 3, 4, 4));
        Assert.Equal((6, 6), (crop.Width, crop.Height));
        Assert.Equal(cell, crop.Get(0, 0));
        Assert.Equal((12, 10), (source.Width, source.Height)); Assert.Equal(7, source.Get(10, 8).MiddleImage);
        Assert.Equal(AssetSourceFormat.Sanxiu, crop.SourceFormat);
        var retained = Assert.Single(crop.PlacedStamps);
        Assert.Equal(complete.Id, retained.Id); Assert.Equal((2, 2), (retained.X, retained.Y));
        Assert.Equal(retained, crop.StampAt(2, 2));
        Assert.DoesNotContain(crop.PlacedStamps, s => s.Id == partial.Id);
        Assert.Equal(new Front(2002, 5), crop.Get(5, 2).Foreground);
        Assert.Equal(source.LayerCount(MapLayer.Ground), crop.LayerCount(MapLayer.Ground));
        cell.Light = 8; Assert.Equal(5, crop.Get(0, 0).Light);
        string path = Path.Combine(Path.GetTempPath(), "CrystalStudio-Crop-" + Guid.NewGuid().ToString("N") + ".map");
        try
        {
            crop.Save(path); var loaded = MapDocument.Load(path, AssetSourceFormat.Sanxiu);
            Assert.Equal((6, 6), (loaded.Width, loaded.Height));
            Assert.Equal(crop.Cells.OrderBy(c => c.Key), loaded.Cells.OrderBy(c => c.Key));
            Assert.Equal(retained.Id, Assert.Single(loaded.PlacedStamps).Id);
        }
        finally { File.Delete(path); }
    }

    [AvaloniaFact]
    public void CropUndoRedoInterleavesWithCellEditsAndRestoresSavedState()
    {
        var source = new MapDocument(12, 10);
        var history = new EditHistory();
        history.Record(source, 10, 8); source.Set(10, 8, new Cell { MiddleImage = 7 }); history.Commit();
        history.Saved();
        var viewport = new MapViewport();
        viewport.RestoreEditorState(new(source, history, 1, default, new Rect(2, 2, 4, 4)));
        Assert.True(viewport.CropSelection()); Assert.Same(source, viewport.Document);
        Assert.Equal((4, 4), (source.Width, source.Height)); Assert.True(history.Dirty); Assert.Null(viewport.Selection);
        history.Record(source, 0, 0); source.Set(0, 0, new Cell { MiddleImage = 9 }); history.Commit();
        viewport.Undo(); Assert.Equal(0, source.Get(0, 0).MiddleImage);
        viewport.Undo(); Assert.Equal((12, 10), (source.Width, source.Height));
        Assert.Equal(7, source.Get(10, 8).MiddleImage); Assert.False(history.Dirty);
        viewport.Undo(); Assert.Equal(0, source.Get(10, 8).MiddleImage);
        viewport.Redo(); Assert.Equal(7, source.Get(10, 8).MiddleImage); Assert.False(history.Dirty);
        viewport.Redo(); Assert.Equal((4, 4), (source.Width, source.Height));
        viewport.Redo(); Assert.Equal(9, source.Get(0, 0).MiddleImage);
        Assert.Equal(1, source.LayerCount(MapLayer.Middle));
        Assert.False(viewport.CropSelection());
        viewport.StopBackgroundWork();
    }

    [AvaloniaFact]
    public void CropButtonRequiresSelectionAndUnlockedLayersAndRunsTheCommand()
    {
        var window = new MainWindow(); window.Show();
        try
        {
            var viewport = window.FindControl<MapViewport>("Viewport")!;
            var button = window.FindControl<Button>("CropButton")!;
            var menu = window.FindControl<MenuItem>("CropMenuButton")!;
            Assert.False(button.IsEnabled); Assert.False(menu.IsEnabled);
            viewport.RestoreEditorState(new(new MapDocument(12, 10), new(), 1, default, new Rect(2, 2, 4, 4)));
            Assert.True(button.IsEnabled); Assert.True(menu.IsEnabled);
            viewport.SetLayerLocked(MapLayer.Ground, true);
            Assert.False(button.IsEnabled); Assert.False(viewport.CropSelection());
            viewport.SetLayerLocked(MapLayer.Ground, false);
            viewport.RestoreEditorState(new(viewport.Document, viewport.History, 1, default, new Rect(2, 2, 4, 4)));
            Assert.True(button.IsEnabled);
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal((4, 4), (viewport.Document.Width, viewport.Document.Height));
            Assert.True(viewport.History.Dirty); Assert.False(button.IsEnabled);
            viewport.Undo(); Assert.Equal((12, 10), (viewport.Document.Width, viewport.Document.Height));
            viewport.History.Saved();
        }
        finally { window.Close(); }
    }

    [Fact]
    public void CropClipsToOddMapEdgesAndRejectsOutsideAreas()
    {
        var source = new MapDocument(9, 7);
        var cropped = source.Crop(new Rect(7, 5, 5, 5));
        Assert.Equal((3, 3), (cropped.Width, cropped.Height));
        Assert.Throws<ArgumentException>(() => source.Crop(new Rect(20, 20, 2, 2)));
    }
}
