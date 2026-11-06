using CrystalStudio;

namespace CrystalStudio.Tests;

public sealed class ModuleMapGenerationTests
{
    private static JoinModule Room(int width = 4, int height = 4, int offset = 0, bool ground = false) => new("Room",
        [new(offset, offset, new Cell { MiddleImage = 1, BackImage = ground ? 1 : 0 })],
        new(1, [new("north", offset + 1, offset, ModuleEdge.North, "path"),
            new("south", offset + 1, offset + height - 1, ModuleEdge.South, "path"),
            new("west", offset, offset + 1, ModuleEdge.West, "path"),
            new("east", offset + width - 1, offset + 1, ModuleEdge.East, "path")])
        { Footprint = new(offset, offset, width, height) });

    private static ModuleMapOptions Options(int count = 12) => new(count, 42, 16, 16, ModuleLayout.Compact);

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(1)]
    public void EstimatesFromTheFullFootprintIncludingBlankMarginsAndNegativeCoordinates(int layout)
    {
        var plan = ModuleMapGeneration.Generate([Room(40, 20, -3, true)], Options() with { Layout = (ModuleLayout)layout });
        Assert.Equal(12, plan.Placement.Placements.Count); Assert.Equal(9600, plan.WalkableCells);
        Assert.True(plan.Placement.Source.Width >= 40 && plan.Placement.Source.Height >= 20);
        Assert.Equal(plan.Options.Area.Width, plan.Placement.Source.Width);
        Assert.Equal(plan.Options.Area.Height, plan.Placement.Source.Height);
        Assert.Empty(plan.Placement.Source.Cells);
        Assert.All(plan.Placement.Cells.Where(c => c.Cell.BackImage != 0), c => { Assert.Equal(0, c.X % 2); Assert.Equal(0, c.Y % 2); });
    }

    private static JoinModule Corridor()
    {
        var room = Room();
        return room with { Connections = room.Connections with { Points = room.Connections.Points.Where(p => p.Edge is ModuleEdge.East or ModuleEdge.West).ToList() } };
    }

    [Fact]
    public void AutomaticallyRetriesLargerAreasForLongChainsAndKeepsTheSeedReproducible()
    {
        var areas = new List<ModuleArea>(); var options = Options(40) with { Layout = ModuleLayout.Chain };
        var first = ModuleMapGeneration.Generate([Corridor()], options, tryingArea: areas.Add);
        Assert.True(areas.Count > 1); Assert.Equal(40, first.Placement.Placements.Count);
        Assert.True(first.Placement.Source.Width >= 160);
        Assert.All(areas, a => { Assert.InRange(a.Width, 16, 4096); Assert.InRange(a.Height, 16, 4096); Assert.True((long)a.Width * a.Height <= 1_000_000); });
        var second = ModuleMapGeneration.Generate([Corridor()], options);
        Assert.Equal(first.Options, second.Options);
        Assert.Equal(first.Placement.Placements.Select(p => (p.X, p.Y)), second.Placement.Placements.Select(p => (p.X, p.Y)));
    }

    [Fact]
    public void OversizedReferenceIsNormalizedWithoutOverflow()
    {
        var plan = ModuleMapGeneration.Generate([Room()], Options() with { ReferenceWidth = int.MaxValue, ReferenceHeight = int.MaxValue });
        Assert.Equal(12, plan.Placement.Placements.Count);
        Assert.InRange((long)plan.Placement.Source.Width * plan.Placement.Source.Height, 256, 1_000_000);
    }

    [Fact]
    public void IncompatibleConnectionsDoNotTriggerSizeRetries()
    {
        var room = Room(); room = room with { Connections = room.Connections with { Points = [room.Connections.Points[0]] } };
        int attempts = 0;
        var error = Assert.Throws<InvalidDataException>(() => ModuleMapGeneration.Generate([room], Options(), tryingArea: _ => attempts++));
        Assert.Equal("generation-connections", ModuleDiagnostics.Read(error)?.Code); Assert.Equal(1, attempts);
    }

    [Fact]
    public void ImpossibleMinimumFootprintAndInvalidCountHaveDistinctDiagnostics()
    {
        var error = Assert.Throws<InvalidDataException>(() => ModuleMapGeneration.Generate([Room(100, 100)], Options(128)));
        Assert.Equal("generation-capacity", ModuleDiagnostics.Read(error)?.Code);
        error = Assert.Throws<InvalidDataException>(() => ModuleMapGeneration.Generate([Room()], Options(129)));
        Assert.Equal("generation-count", ModuleDiagnostics.Read(error)?.Code);
    }

    [Fact]
    public void CancellationDuringSizeRetryStopsBeforeTheNextSearch()
    {
        using var stop = new CancellationTokenSource(); int attempts = 0;
        Assert.Throws<OperationCanceledException>(() => ModuleMapGeneration.Generate([Corridor()], Options(40), stop.Token,
            _ => { if (++attempts == 2) stop.Cancel(); }));
        Assert.Equal(2, attempts);
    }

    [Fact]
    public void ExhaustedRetriesReportTheBestAttemptWithoutReturningAPartialMap()
    {
        var room = Room(); var north = room with { Connections = room.Connections with { Points = [room.Connections.Points[0]] } };
        var south = room with { Connections = room.Connections with { Points = [room.Connections.Points[1]] } };
        int attempts = 0;
        var error = Assert.Throws<InvalidDataException>(() => ModuleMapGeneration.Generate([north, south], Options(3), tryingArea: _ => attempts++));
        Assert.InRange(attempts, 2, 8); Assert.Equal("generation-auto-limit", ModuleDiagnostics.Read(error)?.Code);
        Assert.Contains("best attempt: 2", error.Message); Assert.Contains("does not prove", error.Message);
    }
}
