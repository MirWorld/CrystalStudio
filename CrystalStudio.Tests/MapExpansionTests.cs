using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;

namespace CrystalStudio.Tests;

public sealed class MapExpansionTests
{
    [Fact]
    public void InvalidMovesAndExpansionsLeaveMapAndHistoryUntouched()
    {
        var map = new MapDocument(8, 8); map.Set(2, 2, new Cell { MiddleImage = 1 }); var history = new EditHistory();
        Assert.False(map.MoveRegion(new Rect(2, 2, 4, 4), 4, 0, history, (_, _) => { }));
        Assert.False(map.MoveRegion(new Rect(2, 2, 4, 4), 0, 0, history, (_, _) => { }));
        Assert.Throws<ArgumentException>(() => map.MoveRegion(new Rect(2, 2, 4, 4), 1, 0, history, (_, _) => { }));
        foreach (var (left, top, right, bottom) in new[] { (-2, 0, 0, 0), (1, 0, 0, 0), (0, 0, 0, 0), (32766, 0, 0, 0), (4000, 4000, 0, 0) })
            Assert.Throws<ArgumentException>(() => map.Expand(left, top, right, bottom));
        Assert.False(history.Dirty); Assert.False(history.CanUndo); Assert.Equal(1, map.Get(2, 2).MiddleImage);
    }

    [AvaloniaFact]
    public void ExpansionKeepsSourceFormatStampsAndUndoAcrossResizeAndCellEdits()
    {
        var map = new MapDocument(9, 7) { SourceFormat = AssetSourceFormat.Sanxiu };
        map.Set(2, 2, new Cell { BackImage = 0x60000001, MiddleLibrary = 1001, MiddleImage = 3, Light = 5 });
        var stamp = map.PlaceStamp([new(0, 0, new Cell { Foreground = new Front(2002, 6) })], 4, 4, "test", new(), (_, _) => { })!;
        var viewport = new MapViewport(); viewport.SetDocument(map);
        Assert.True(viewport.ExpandMap(2, 4, 6, 8));
        Assert.Equal((17, 19), (map.Width, map.Height)); Assert.Equal(5, map.Get(4, 6).Light);
        Assert.Equal(stamp.Id, map.StampAt(6, 8)!.Id); Assert.True(map.Get(0, 0).Empty); Assert.True(map.Get(16, 18).Empty);
        Assert.Equal(AssetSourceFormat.Sanxiu, map.SourceFormat);
        viewport.History.Record(map, 0, 0); map.Set(0, 0, new Cell { MiddleLibrary = 1001, MiddleImage = 7 }); viewport.History.Commit();
        viewport.Undo(); viewport.Undo(); Assert.Equal((9, 7), (map.Width, map.Height)); Assert.Equal(stamp.Id, map.StampAt(4, 4)!.Id);
        Assert.False(viewport.History.Dirty); viewport.Redo(); viewport.Redo(); Assert.Equal(7, map.Get(0, 0).MiddleImage);
        string path = Path.Combine(Path.GetTempPath(), "CrystalStudio-Expand-" + Guid.NewGuid().ToString("N") + ".map");
        try
        {
            map.Save(path); var loaded = MapDocument.Load(path, AssetSourceFormat.Sanxiu);
            Assert.Equal((17, 19), (loaded.Width, loaded.Height));
            Assert.Equal(map.Cells.OrderBy(p => p.Key), loaded.Cells.OrderBy(p => p.Key));
            Assert.Equal(stamp.Id, Assert.Single(loaded.PlacedStamps).Id);
        }
        finally { File.Delete(path); viewport.StopBackgroundWork(); }
    }

    [AvaloniaFact]
    public void ExpansionPanelValidatesAmountsAndSizeLimits()
    {
        var window = new MapExpansionWindow(new(100, 60));
        Assert.False(window.ApplyButton.IsEnabled);
        window.LeftInput.Text = "2"; window.TopInput.Text = "4"; window.RightInput.Text = "6"; window.BottomInput.Text = "8";
        Assert.True(window.ApplyButton.IsEnabled); Assert.Equal(2, window.LeftAmount); Assert.Equal(8, window.BottomAmount);
        foreach (string invalid in new[] { "-2", "1", "abc", "2147483648", "32766" })
        { window.LeftInput.Text = invalid; Assert.False(window.ApplyButton.IsEnabled); }
        window.LeftInput.Text = "2"; Assert.True(window.ApplyButton.IsEnabled); window.Close();
    }
}
