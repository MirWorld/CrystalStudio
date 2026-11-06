using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;

namespace CrystalStudio.Tests;

public sealed class RegionMoveTests
{
    [Fact]
    public void OverlappingMovePreservesAllFieldsAndEmptyCellsOverwriteDestinationAtomically()
    {
        var map = new MapDocument(12, 12);
        var first = new Cell { BackImage = 0x60000001, MiddleImage = 4, Foreground = new Front(2, 5),
            Light = 7, Door = 1, DoorOffset = 2, TileAnimation = 3, TileFrames = 4, TileOffset = 5, MiddleFrames = 6, MiddleTick = 7 };
        map.Set(2, 2, first); map.Set(4, 4, new Cell { Light = 9 });
        map.Set(7, 7, new Cell { MiddleImage = 8 });
        var before = map.Cells.OrderBy(p => p.Key).ToArray();
        var history = new EditHistory();
        Assert.True(map.MoveRegion(new Rect(2, 2, 4, 4), 2, 2, history, (_, _) => { }));
        Assert.True(map.Get(2, 2).Empty); Assert.Equal(first, map.Get(4, 4)); Assert.Equal(9, map.Get(6, 6).Light);
        Assert.True(map.Get(7, 7).Empty); Assert.True(history.Dirty);
        history.Undo(map); Assert.Equal(before, map.Cells.OrderBy(p => p.Key)); Assert.False(history.Dirty);
        history.Redo(map); Assert.Equal(first, map.Get(4, 4));
        Assert.Equal(1, map.LayerCount(MapLayer.Ground));
    }

    [Fact]
    public void RegionMoveRelocatesWholeStampsAndDropsCutAndOverwrittenOwnership()
    {
        var map = new MapDocument(16, 12);
        var piece = new List<StampCell> { new(0, 0, new Cell { Foreground = new Front(2, 5) }) };
        var whole = map.PlaceStamp(piece, 2, 2, "whole", new(), (_, _) => { })!;
        var cut = map.PlaceStamp([..piece, new(1, 0, new Cell { Foreground = new Front(2, 6) })], 5, 3, "cut", new(), (_, _) => { })!;
        var covered = map.PlaceStamp(piece, 10, 2, "covered", new(), (_, _) => { })!;
        var untouched = map.PlaceStamp(piece, 14, 10, "untouched", new(), (_, _) => { })!;
        var history = new EditHistory();
        Assert.True(map.MoveRegion(new Rect(2, 2, 4, 4), 8, 0, history, (_, _) => { }));
        Assert.Equal(whole.Id, map.StampAt(10, 2)!.Id);
        Assert.Equal(untouched.Id, map.StampAt(14, 10)!.Id);
        Assert.DoesNotContain(map.PlacedStamps, s => s.Id == cut.Id || s.Id == covered.Id);
        Assert.Equal(new Front(2, 6), map.Get(6, 3).Foreground);
        history.Undo(map); Assert.Equal(4, map.PlacedStamps.Count); Assert.Equal(whole.Id, map.StampAt(2, 2)!.Id);
    }

    [AvaloniaFact]
    public void DragInsideSelectionMovesOnReleaseAndEscapeAndInvalidDropsDoNotModifyMap()
    {
        var map = new MapDocument(12, 12); map.Set(2, 2, new Cell { MiddleImage = 3 });
        var viewport = new MapViewport { Tool = EditorTool.Select };
        var window = new EditorWindow { Width = 800, Height = 600, Content = viewport }; window.Show();
        try
        {
            Reset();
            window.MouseDown(new Point(180, 150), MouseButton.Left); window.MouseMove(new Point(276, 214));
            Assert.Equal(3, map.Get(2, 2).MiddleImage); Assert.False(viewport.History.Dirty);
            window.MouseUp(new Point(276, 214), MouseButton.Left);
            Assert.Equal(3, map.Get(4, 4).MiddleImage); Assert.True(map.Get(2, 2).Empty);
            Assert.Equal(new Rect(4, 4, 4, 4), viewport.Selection);
            viewport.Undo(); Assert.Equal(3, map.Get(2, 2).MiddleImage);
            Reset(); window.MouseDown(new Point(180, 150), MouseButton.Left); window.MouseMove(new Point(276, 214));
            viewport.CancelInteraction(); window.MouseUp(new Point(276, 214), MouseButton.Left);
            Assert.Equal(3, map.Get(2, 2).MiddleImage); Assert.False(viewport.History.Dirty);
            Reset(); window.MouseDown(new Point(180, 150), MouseButton.Left); window.MouseMove(new Point(750, 550));
            window.MouseUp(new Point(750, 550), MouseButton.Left);
            Assert.Equal(3, map.Get(2, 2).MiddleImage); Assert.False(viewport.History.Dirty);
            viewport.SetLayerLocked(MapLayer.Objects, true);
            viewport.RestoreEditorState(new(map, new(), 1, new Point(60, 70), new Rect(2, 2, 4, 4), [false, false, true, false, false]));
            window.MouseDown(new Point(180, 150), MouseButton.Left); window.MouseMove(new Point(276, 214)); window.MouseUp(new Point(276, 214), MouseButton.Left);
            Assert.Equal(3, map.Get(2, 2).MiddleImage); Assert.False(viewport.History.Dirty);
        }
        finally { viewport.StopBackgroundWork(); window.Close(); }
        void Reset() => viewport.RestoreEditorState(new(map, new(), 1, new Point(60, 70), new Rect(2, 2, 4, 4)));
    }
}
