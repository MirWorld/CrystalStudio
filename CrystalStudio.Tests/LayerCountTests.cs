using Avalonia.Controls;
using Avalonia.Headless.XUnit;

namespace CrystalStudio.Tests;

public sealed class LayerCountTests
{
    [Fact]
    public void CountsTrackReplacementRemovalAndIgnoreImageFlags()
    {
        var map = new MapDocument(4, 4);
        map.Set(0, 0, new Cell { BackImage = 0x60000001, MiddleImage = 1, Foreground = new(0, 1) });
        map.Set(1, 1, new Cell { BackImage = 1, Foreground = new(0, unchecked((short)0x8000)) });
        Assert.All(Enum.GetValues<MapLayer>(), layer => Assert.Equal(1, map.LayerCount(layer)));
        map.Set(0, 0, new Cell { BackImage = 0x20000000 });
        Assert.Equal(1, map.LayerCount(MapLayer.LowCollision));
        Assert.Equal(0, map.LayerCount(MapLayer.Ground));
        Assert.Equal(0, map.LayerCount(MapLayer.Middle));
        Assert.Equal(0, map.LayerCount(MapLayer.Objects));
        Assert.Equal(0, map.LayerCount(MapLayer.HighCollision));
        map.Set(0, 0, new Cell());
        map.Set(-1, 0, new Cell { MiddleImage = 1 });
        Assert.All(Enum.GetValues<MapLayer>(), layer => Assert.Equal(0, map.LayerCount(layer)));
    }

    [AvaloniaFact]
    public void LayerLabelsFollowMapSwitchUndoAndRedo()
    {
        var window = new MainWindow(new AssetHistoryStore(Path.Combine(TestApplication.Root, "layer-count-assets.json")),
            new RecentMapStore(Path.Combine(TestApplication.Root, "layer-count-maps.json")));
        try
        {
            var viewport = window.FindControl<MapViewport>("Viewport")!;
            var map = new MapDocument(4, 4);
            map.Set(0, 0, new Cell { BackImage = 1, MiddleImage = 1 });
            viewport.SetDocument(map);
            Assert.Equal(" (1)", window.FindControl<TextBlock>("GroundLayerCount")!.Text);
            viewport.History.Record(map, 0, 0);
            map.Set(0, 0, new Cell());
            viewport.History.Commit();
            viewport.Undo();
            Assert.Equal(" (1)", window.FindControl<TextBlock>("MiddleLayerCount")!.Text);
            viewport.Redo();
            Assert.Equal(" (0)", window.FindControl<TextBlock>("MiddleLayerCount")!.Text);
            viewport.SetDocument(new MapDocument(2, 2));
            Assert.Equal(" (0)", window.FindControl<TextBlock>("GroundLayerCount")!.Text);
        }
        finally { window.Close(); }
    }
}
