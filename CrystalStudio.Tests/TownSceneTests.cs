namespace CrystalStudio.Tests;

public sealed class TownSceneTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(27)]
    [InlineData(1729)]
    public void DetailedVillagesHaveWorkingYardsAndReachableCropBeds(int seed)
    {
        var family = TownCatalogue.Load().Families.Single(f => f.Id == "bichon");
        var options = new TownOptions(128, seed, Ground: false, Layout: TownLayout.VillageGreen,
            Decorations: TownDecorations.Detailed, WallChance: 0);
        var plan = TownGenerator.Generate(family, options);
        var again = TownGenerator.Generate(family, options);
        Assert.Equal(plan.Buildings.Select(b => (b.Piece.Id, b.X, b.Y)), again.Buildings.Select(b => (b.Piece.Id, b.X, b.Y)));
        Assert.True(plan.Roads.SetEquals(again.Roads));
        var props = plan.Buildings.Where(b => (b.Piece.Role == "prop" || TownSceneDetails.Use(b.Piece) == "crop")).ToArray();
        Assert.Contains(props, b => TownSceneDetails.Use(b.Piece) == "crop");
        Assert.Contains(props, b => TownSceneDetails.Use(b.Piece) is "storage" or "cart");
        Assert.True(props.Count(b => TownSceneDetails.Use(b.Piece) == "well") <= 1);
        Assert.True(props.Count(b => TownSceneDetails.Use(b.Piece) == "notice") <= 1);
        var footprints = new HashSet<(int X, int Y)>();
        foreach (var building in plan.Buildings)
        foreach (var p in building.Piece.Footprint)
        {
            Assert.True(footprints.Add((building.X + p.X, building.Y + p.Y)), "Scene objects overlap.");
            Assert.DoesNotContain((building.X + p.X, building.Y + p.Y), plan.Roads);
        }
        var reachable = new HashSet<(int X, int Y)>();
        var queue = new Queue<(int X, int Y)>();
        queue.Enqueue((props[0].AccessX, props[0].AccessY));
        while (queue.TryDequeue(out var p))
        {
            if (!plan.Roads.Contains(p) || !reachable.Add(p)) continue;
            queue.Enqueue((p.X - 1, p.Y)); queue.Enqueue((p.X + 1, p.Y));
            queue.Enqueue((p.X, p.Y - 1)); queue.Enqueue((p.X, p.Y + 1));
        }
        Assert.All(props, p => Assert.Contains((p.AccessX, p.AccessY), reachable));
        Assert.All(props, p => Assert.Contains(plan.Buildings, home => home.Piece.Role is "house" or "market" or "landmark" &&
            Math.Abs(home.X - p.X) + Math.Abs(home.Y - p.Y) < 45));
    }

    [Fact]
    public void DetailedScenesRespectDisabledGreeneryAndMissingOptionalProps()
    {
        var family = TownCatalogue.Load().Families.Single(f => f.Id == "bichon");
        var options = new TownOptions(96, Ground: false, Greenery: false,
            Decorations: TownDecorations.Detailed, WallChance: 0);
        var plan = TownGenerator.Generate(family, options);
        Assert.DoesNotContain(plan.Buildings, b => b.Piece.Role == "greenery" || TownSceneDetails.Use(b.Piece) == "crop");
        var housesOnly = family with { Pieces = family.Pieces.Where(p => p.Role == "house").ToList() };
        Assert.True(TownGenerator.Generate(housesOnly, options).Buildings.Count >= 3);
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        Assert.Throws<OperationCanceledException>(() => TownGenerator.Generate(family, options, cancellationToken: cancelled.Token));
    }
}
