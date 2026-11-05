namespace CrystalStudio.Tests;

public sealed class ForestRuinTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(27)]
    public void CompleteRuinsRemainReachableAfterForestPlacementAndSave(int seed)
    {
        var source = new MapDocument(320, 320);
        var terrain = TerrainCatalogue.Load();
        var walls = WallCatalogue.Read(WallCatalogue.ResolvePath());
        ProvinceScenery[] scenery = [new(new BrushTree("Boundary tree", "", [new(0, 0,
            new Cell { Foreground = new(2, 1), BackImage = 0x60000000 })]), SpecialBrushKind.Tree, [ProvinceStyle.All[0].Region]),
            new(new BrushTree("Low shrub", "", [new(0, 0, new Cell { Foreground = new(2, 2), BackImage = 0x60000000 })]),
                SpecialBrushKind.Bush, [ProvinceStyle.All[0].Region])];
        var options = new ProvinceOptions(seed, ProvinceStyle.All[0], ForestChance: 100, CliffChance: 0,
            BeachChance: 0, RiverChance: 0, Vegetation: 100, Scene: ProvinceScene.ForestRuins, RuinCount: 2);
        var plan = ProvinceGenerator.Plan(source, terrain, walls, scenery, options);
        Assert.Equal(2, plan.Ruins.Count);
        Assert.Empty(source.Cells); Assert.Empty(source.PlacedStamps);
        var reachable = Flood(plan.Generated, plan.Ruins[0].X, plan.Ruins[0].Y + 3);
        foreach (var ruin in plan.Ruins)
        {
            Assert.Equal(7, ruin.Objects.Count);
            foreach (var p in ruin.Approach)
            {
                Assert.Contains(p.Y * source.Width + p.X, reachable);
                Assert.Empty(plan.Generated.Get(p.X, p.Y).ForegroundImages);
            }
            foreach (var obj in ruin.Objects)
            {
                var placed = Assert.Single(plan.Generated.PlacedStamps, s => s.X == obj.X && s.Y == obj.Y && s.Name.StartsWith("Forest ruin"));
                Assert.Equal(obj.Cells, placed.Cells);
            }
        }
        var again = ProvinceGenerator.Plan(source, terrain, walls, scenery, options);
        Assert.Equal(plan.Generated.Cells.OrderBy(c => c.Key), again.Generated.Cells.OrderBy(c => c.Key));
        string path = Path.Combine(Path.GetTempPath(), "CrystalStudio-ruins-" + Guid.NewGuid().ToString("N") + ".map");
        try
        {
            plan.Generated.Save(path);
            var loaded = MapDocument.Load(path);
            Assert.Equal(plan.Generated.Cells.OrderBy(c => c.Key), loaded.Cells.OrderBy(c => c.Key));
            Assert.Equal(plan.Generated.PlacedStamps.Count, loaded.PlacedStamps.Count);
        }
        finally { File.Delete(path); }
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        Assert.Throws<OperationCanceledException>(() => ProvinceGenerator.Plan(source, terrain, walls, scenery, options, cancelled.Token));
    }

    [Fact]
    public void MissingTrailDoesNotPlaceUnreachableScenes()
    {
        var map = new MapDocument(128, 128); var notes = new List<string>();
        var ruins = ProvinceRuins.Place(map, TerrainCatalogue.Load(), ProvinceStyle.All[0],
            new(Scene: ProvinceScene.ForestRuins), new bool[128 * 128], [], new(), null, notes, default);
        Assert.Empty(ruins); Assert.Empty(map.Cells); Assert.Empty(map.PlacedStamps);
        Assert.Contains(notes, n => n.Contains("No accessible trail"));
    }

    [Fact]
    public void ForestArtworkCannotBePlacedOnDesertStyle()
    {
        Assert.Throws<ArgumentException>(() => ProvinceGenerator.Plan(new(128, 128), TerrainCatalogue.Load(),
            WallCatalogue.Read(WallCatalogue.ResolvePath()), [], new(Style: ProvinceStyle.All[1], Scene: ProvinceScene.ForestRuins)));
    }

    private static HashSet<int> Flood(MapDocument map, int x, int y)
    {
        var result = new HashSet<int>(); var queue = new Queue<(int X, int Y)>(); queue.Enqueue((x, y));
        while (queue.TryDequeue(out var p))
        {
            if (!map.Contains(p.X, p.Y) || (map.Get(p.X, p.Y).BackImage & 0x60000000) != 0 || !result.Add(p.Y * map.Width + p.X)) continue;
            queue.Enqueue((p.X - 1, p.Y)); queue.Enqueue((p.X + 1, p.Y)); queue.Enqueue((p.X, p.Y - 1)); queue.Enqueue((p.X, p.Y + 1));
        }
        return result;
    }
}
