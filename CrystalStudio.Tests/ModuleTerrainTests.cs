namespace CrystalStudio.Tests;

public sealed class ModuleTerrainTests
{
    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 3)]
    public void LegacyReferencesSurviveCaptureCropReloadAndGeneration(int x, int y)
    {
        var map = new MapDocument(20, 20);
        for (int row = 0; row < 20; row++) for (int col = 0; col < 20; col++)
            map.Set(col, row, new Cell { BackLibrary = 1, BackImage = 1 + col + row * 20, MiddleImage = 2 });
        var draft = ModuleCapture.Capture(map, new(x, y, 8, 8));
        var prepared = ModuleCapture.Prepare(draft, "legacy", draft.Connections);
        string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".X");
        try
        {
            CellCodec.SaveStamp(path, prepared.Cells);
            var cells = CellCodec.LoadStamp(path);
            Assert.Contains(cells, c => c.X < 0 && c.Y < 0);
            var module = new JoinModule("legacy", cells, prepared.Connections!);
            var plan = ModuleMapGeneration.Generate([module], new(4, 5, 32, 32, ModuleLayout.Compact));
            Assert.Equal(4, plan.Placement.Placements.Count);
            foreach (var placement in plan.Placement.Placements)
            {
                Assert.Equal(0, placement.X & 1); Assert.Equal(0, placement.Y & 1);
                foreach (var cell in placement.Module.Cells)
                {
                    var original = map.Get(cell.X + prepared.OffsetX - (x & 1) + x, cell.Y + prepared.OffsetY - (y & 1) + y);
                    Assert.True(original.SameContents(cell.Cell));
                    Assert.True(cell.Cell.SameContents(plan.Placement.Generated.Get(cell.X + placement.X, cell.Y + placement.Y)));
                    // Rendered and non-rendered ground positions retain the same phase.
                    Assert.Equal(((cell.X & 1), (cell.Y & 1)), (((cell.X + placement.X) & 1), ((cell.Y + placement.Y) & 1)));
                }
            }
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void FourNeighbourAnalysisIncludesEmptyWalkableCellsAndForegroundCollision()
    {
        var map = new MapDocument(5, 4);
        for (int y = 0; y < 4; y++) map.Set(2, y, new Cell { Foreground = new(1, unchecked((short)0x8001)) });
        var analysis = ModuleTerrain.Analyze(map);
        Assert.Equal(new[] { 8, 8 }, analysis.Regions.Select(r => r.Count));
        Assert.Equal(-1, analysis.Labels[2]); Assert.NotEqual(analysis.Labels[0], analysis.Labels[3]);
        using var stop = new CancellationTokenSource(); stop.Cancel();
        Assert.Throws<OperationCanceledException>(() => ModuleTerrain.Analyze(map, stop.Token));
    }

    [Fact]
    public void AmbiguousLegacyObjectWithoutFootprintHasLocatableGroundDiagnostic()
    {
        var module = new JoinModule("old", [new(0, 0, new Cell { BackImage = 1 }), new(1, 1, new Cell { BackImage = 2 })], new(1, []));
        var error = Assert.Throws<InvalidDataException>(() => ModuleTerrain.GroundParity(module));
        Assert.Equal("ground-phase", ModuleDiagnostics.Read(error)?.Code);
        Assert.Equal(1, ModuleDiagnostics.Read(error)?.X);
    }

    [Fact]
    public void ExplicitMapGridSurvivesEditingAndJsonWhileOldOddGroundKeepsItsAlignment()
    {
        var map = new MapDocument(8, 8); map.Set(1, 1, new Cell { BackImage = 1 });
        var draft = ModuleCapture.Capture(map, new(0, 0, 8, 8));
        var editor = new ModuleConnectionEditorState(draft.Cells, draft.Connections);
        Assert.True(editor.Connections.MapGroundGrid);
        var json = System.Text.Json.JsonSerializer.Serialize(editor.Connections);
        var restored = System.Text.Json.JsonSerializer.Deserialize<ModuleConnections>(json)!;
        Assert.Equal(((int?)0, (int?)0), ModuleTerrain.GroundParity(new("capture", draft.Cells, restored)));
        var old = restored with { MapGroundGrid = false };
        Assert.DoesNotContain("MapGroundGrid", System.Text.Json.JsonSerializer.Serialize(old));
        Assert.Equal(((int?)1, (int?)1), ModuleTerrain.GroundParity(new("legacy", draft.Cells, old)));
    }
}
