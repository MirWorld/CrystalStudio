using Avalonia.Controls;
using Avalonia.Headless.XUnit;

namespace CrystalStudio.Tests;

public sealed class DungeonThemeTests
{
    [Theory]
    [InlineData(DungeonTheme.CelestialPalace, 1, 256, 256)]
    [InlineData(DungeonTheme.CelestialPalace, 27, 320, 320)]
    [InlineData(DungeonTheme.CelestialPalace, 1729, 384, 256)]
    [InlineData(DungeonTheme.AbyssalReef, 1, 256, 256)]
    [InlineData(DungeonTheme.AbyssalReef, 27, 320, 320)]
    [InlineData(DungeonTheme.AbyssalReef, 1729, 384, 256)]
    [InlineData(DungeonTheme.AlienPlanet, 1, 256, 256)]
    [InlineData(DungeonTheme.AlienPlanet, 27, 320, 320)]
    [InlineData(DungeonTheme.AlienPlanet, 1729, 384, 256)]
    public void ThemesKeepRoomRoutesOpenAndPreserveCompleteObjectsAndSource(DungeonTheme theme, int seed, int width, int height)
    {
        var source = new MapDocument(width, height);
        var definition = DungeonThemes.Definition(theme); var terrain = TerrainCatalogue.Load();
        var family = WallCatalogue.Read(WallCatalogue.ResolvePath()).Families.Single(f => f.Id == definition.WallId).WithMode(true);
        var floor = terrain.Sets.Single(s => s.Id == definition.FloorId);
        var options = new TempleOptions(definition, seed, Rooms: 9, Type: DungeonThemes.Type(theme), Theme: theme);
        var plan = TempleGenerator.Plan(source, family, floor, options);
        Assert.Empty(source.Cells); Assert.Empty(source.PlacedStamps);
        Assert.InRange(plan.RoomCount, 2, 9); Assert.NotEmpty(plan.Decorations);
        var reachable = Flood(plan.Generated, plan.RoomCentres[0]);
        foreach (var p in plan.RoomCentres) Assert.Contains(p.Y * width + p.X, reachable);
        foreach (var stamp in plan.Decorations)
        {
            Assert.Single(plan.Generated.PlacedStamps, s => s.Id == stamp.Id);
            Assert.All(stamp.Cells, c => Assert.True(plan.Generated.Contains(stamp.X + c.X, stamp.Y + c.Y)));
            string id = stamp.Name["Dungeon theme · ".Length..];
            Assert.Null(ProvinceSceneryPalette.GroundSetId(id));
            var native = BrushTree.FromCells(id, "", CellCodec.LoadStamp(DataPaths.ResolveEditor("Objects", "mir", id[4..] + ".X")));
            Assert.Equal(native.Cells, stamp.Cells);
        }
        string path = Path.Combine(Path.GetTempPath(), "CrystalStudio-world-" + Guid.NewGuid().ToString("N") + ".map");
        try
        {
            plan.Generated.Save(path); var restored = MapDocument.Load(path);
            Assert.Equal(plan.Generated.Cells.OrderBy(c => c.Key), restored.Cells.OrderBy(c => c.Key));
            Assert.Equal(plan.Generated.PlacedStamps.Count, restored.PlacedStamps.Count);
        }
        finally { File.Delete(path); }
        var repeat = TempleGenerator.Plan(source, family, floor, options);
        Assert.Equal(plan.Generated.Cells.OrderBy(c => c.Key), repeat.Generated.Cells.OrderBy(c => c.Key));
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        Assert.Throws<OperationCanceledException>(() => TempleGenerator.Plan(source, family, floor, options, cancelled.Token));
    }

    [Fact]
    public void ThemesHaveDistinctConnectedTopologyAndCelestialSymmetry()
    {
        var layouts = Enum.GetValues<DungeonTheme>().Where(t => t != DungeonTheme.Classic).ToDictionary(t => t,
            t => DungeonThemeLayout.Create(t, 24, 5, 9, 2, new Random(27), default));
        var palace = layouts[DungeonTheme.CelestialPalace];
        Assert.All(palace.Nodes, p => Assert.Contains((-p.U, p.V), palace.Nodes));
        foreach (var layout in layouts.Values)
        {
            var reached = new HashSet<(int U, int V)>(); var queue = new Queue<(int U, int V)>(); queue.Enqueue(layout.Centres[0]);
            while (queue.TryDequeue(out var p))
            {
                if (!layout.Nodes.Contains(p) || !reached.Add(p)) continue;
                queue.Enqueue((p.U - 1, p.V)); queue.Enqueue((p.U + 1, p.V)); queue.Enqueue((p.U, p.V - 1)); queue.Enqueue((p.U, p.V + 1));
            }
            Assert.Equal(layout.Nodes.Count, reached.Count);
            Assert.All(layout.Centres, p => Assert.Contains(p, reached));
        }
        Assert.False(palace.Nodes.SetEquals(layouts[DungeonTheme.AbyssalReef].Nodes));
        Assert.False(layouts[DungeonTheme.AbyssalReef].Nodes.SetEquals(layouts[DungeonTheme.AlienPlanet].Nodes));
        Assert.True(layouts[DungeonTheme.AlienPlanet].Centres.Length >= 3);
    }

    [AvaloniaFact]
    public void ThemePreferencesRestoreAndClassicModeRemainsAvailable()
    {
        using var assets = AssetStore.Load(Path.Combine(TestApplication.Root, "Data"));
        var window = new TempleGeneratorWindow(new(320, 320), assets,
            WallCatalogue.Read(WallCatalogue.ResolvePath()), TerrainCatalogue.Load());
        var theme = window.FindControl<ComboBox>("ThemeList")!;
        Assert.Equal(0, theme.SelectedIndex);
        foreach (int index in new[] { 1, 2, 3 })
        {
            theme.SelectedIndex = index;
            Assert.False(window.FindControl<ComboBox>("TempleList")!.IsEnabled);
            Assert.False(window.FindControl<ComboBox>("DungeonTypeList")!.IsEnabled);
            Assert.True(window.FindControl<Slider>("SizeSlider")!.Minimum > 0);
            var settings = GeneratorPreferences.Capture(window); theme.SelectedIndex = 0;
            Assert.True(window.FindControl<ComboBox>("TempleList")!.IsEnabled);
            Assert.Equal(0, window.FindControl<Slider>("SizeSlider")!.Minimum);
            GeneratorPreferences.Restore(window, settings);
            Assert.Equal(index, theme.SelectedIndex);
            Assert.Equal(DungeonThemes.Type((DungeonTheme)index), window.FindControl<ComboBox>("DungeonTypeList")!.SelectedItem);
            window.FindControl<Button>("RandomizeButton")!.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            Assert.Equal(index, theme.SelectedIndex);
            Assert.Equal(DungeonThemes.Definition((DungeonTheme)index), window.FindControl<ComboBox>("TempleList")!.SelectedItem);
            Assert.InRange(window.FindControl<Slider>("SizeSlider")!.Value, 3, 5);
        }
        window.Close();
    }

    [Fact]
    public void AThemeCannotSilentlyUseClassicOrMismatchedArtwork()
    {
        var definition = DungeonThemes.Definition(DungeonTheme.CelestialPalace);
        var family = WallCatalogue.Read(WallCatalogue.ResolvePath()).Families.Single(f => f.Id == definition.WallId).WithMode(true);
        var floor = TerrainCatalogue.Load().Sets.Single(s => s.Id == definition.FloorId);
        Assert.Throws<ArgumentException>(() => TempleGenerator.Plan(new(320, 320), family, floor,
            new(definition, Type: DungeonType.Cave, Theme: DungeonTheme.CelestialPalace)));
        Assert.Throws<ArgumentOutOfRangeException>(() => TempleGenerator.Plan(new(320, 320), family, floor,
            new(definition, RoomSize: 0, Theme: DungeonTheme.CelestialPalace)));
    }

    [Theory]
    [InlineData(DungeonTheme.CelestialPalace, 1)]
    [InlineData(DungeonTheme.AbyssalReef, 1)]
    [InlineData(DungeonTheme.AlienPlanet, 1)]
    [InlineData(DungeonTheme.CelestialPalace, 30)]
    [InlineData(DungeonTheme.AbyssalReef, 30)]
    [InlineData(DungeonTheme.AlienPlanet, 30)]
    public void LayoutLimitsKeepFiniteUniqueRoomCentres(DungeonTheme theme, int count)
    {
        var layout = DungeonThemeLayout.Create(theme, 24, 5, count, 1, new(1729), default);
        Assert.InRange(layout.Centres.Length, 1, count);
        Assert.Equal(layout.Centres.Length, layout.Centres.Distinct().Count());
        Assert.All(layout.Centres, p => Assert.Contains(p, layout.Nodes));
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        Assert.Throws<OperationCanceledException>(() => DungeonThemeLayout.Create(theme, 24, 5, count, 1, new(1729), cancelled.Token));
    }

    private static HashSet<int> Flood(MapDocument map, (int X, int Y) start)
    {
        var reached = new HashSet<int>(); var queue = new Queue<(int X, int Y)>(); queue.Enqueue(start);
        while (queue.TryDequeue(out var p))
        {
            if (!map.Contains(p.X, p.Y) || (map.Get(p.X, p.Y).BackImage & 0x60000000) != 0 || !reached.Add(p.Y * map.Width + p.X)) continue;
            queue.Enqueue((p.X - 1, p.Y)); queue.Enqueue((p.X + 1, p.Y)); queue.Enqueue((p.X, p.Y - 1)); queue.Enqueue((p.X, p.Y + 1));
        }
        return reached;
    }
}
