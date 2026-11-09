using Avalonia;

namespace CrystalStudio.Tests;

public sealed class QuickMapWorkflowTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(17)]
    [InlineData(-23)]
    [InlineData(int.MaxValue)]
    [InlineData(int.MinValue)]
    public void DecorationVariantsPreserveSourceStructureAndCompleteNativeStamps(int seed)
    {
        var source = DecoratedMap(); var original = MapRecoveryStore.Capture(source); long revision = source.PickupRevision;
        var first = MapDecorationVariation.Create(source, new(), seed);
        var second = MapDecorationVariation.Create(source, new(), seed);
        Assert.Equal(revision, source.PickupRevision); AssertMapsEqual(original, source);
        Assert.True(first.Moved > 0); Assert.Equal(2, first.Movable); Assert.NotEmpty(first.Changed);
        Assert.Equal(MapDecorationVariation.Fingerprint(source), first.StructureFingerprint);
        Assert.Equal(first.StructureFingerprint, MapDecorationVariation.Fingerprint(first.Generated)); AssertMapsEqual(first.Generated, second.Generated);
        Assert.Equal(source.PlacedStamps.Count, first.Generated.PlacedStamps.Count);
        foreach (var stamp in first.Generated.PlacedStamps)
            foreach (var delta in stamp.Changes) Assert.True(first.Generated.Get(stamp.X + delta.X, stamp.Y + delta.Y).SameContents(delta.After));
        Assert.Equal(source.PlacedStamps[^1], first.Generated.PlacedStamps[^1]);
    }

    [Theory]
    [InlineData((int)MapRegionPurpose.Protect)]
    [InlineData((int)MapRegionPurpose.ClearObjects)]
    [InlineData((int)MapRegionPurpose.Walkable)]
    public void ExistingReservedDecorationsCannotMove(int purpose)
    {
        var source = DecoratedMap();
        var design = new MapDesignSettings { Regions = [new("Protected source", 3, 3, 4, 4, (MapRegionPurpose)purpose, false)] };
        var variant = MapDecorationVariation.Create(source, design, 42);
        Assert.Equal(source.PlacedStamps[0], variant.Generated.PlacedStamps[0]);
        foreach (var stamp in variant.Generated.PlacedStamps.Skip(1).Where(stamp => stamp.Wall == null))
            Assert.DoesNotContain(stamp.Changes, delta => design.Regions[0].Contains(stamp.X + delta.X, stamp.Y + delta.Y));
    }

    [Fact]
    public void ProtectedMapReturnsAnExplicitNoChangeResult()
    {
        var source = DecoratedMap();
        var result = MapDecorationVariation.Create(source, new() { Regions = [new("All", 0, 0, 32, 32)] }, 11);
        Assert.Equal(0, result.Movable); Assert.Equal(0, result.Moved); Assert.Empty(result.Changed); AssertMapsEqual(source, result.Generated);
    }

    [Fact]
    public void OnlyExplicitlyAllowedNativeTypesMayMove()
    {
        var source = DecoratedMap();
        var none = MapDecorationVariation.Create(source, new(), 19, allowedNames: new HashSet<string>());
        Assert.Empty(none.Changed); Assert.Equal(0, none.Moved);
        var selected = MapDecorationVariation.Create(source, new(), 19, allowedNames: new HashSet<string> { "Native flower" });
        Assert.Equal(1, selected.Movable); Assert.Equal(1, selected.Moved);
        Assert.Equal(source.PlacedStamps[1], selected.Generated.PlacedStamps[1]); Assert.Equal(source.PlacedStamps[2], selected.Generated.PlacedStamps[2]);
    }

    [Fact]
    public void StaleOrOverlappingStampOwnershipCannotBeVaried()
    {
        var source = DecoratedMap(); var first = source.PlacedStamps[0];
        source.Set(first.X, first.Y, new Cell { BackImage = 1, Foreground = new(0, 9) });
        source.RestoreStamps(source.PlacedStamps.Concat([source.PlacedStamps[1] with { Id = Guid.NewGuid() }]));
        var result = MapDecorationVariation.Create(source, new(), 100);
        Assert.Equal(0, result.Movable); Assert.Empty(result.Changed); AssertMapsEqual(source, result.Generated);
    }

    [Fact]
    public void CancelledVariationDoesNotTouchTheSource()
    {
        var source = DecoratedMap(); long revision = source.PickupRevision;
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => MapDecorationVariation.Create(source, new(), 1, cancellation.Token));
        Assert.Equal(revision, source.PickupRevision);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ClearSpaceChecksRespectTheExplicitRequiredSetting(bool required)
    {
        var map = new MapDocument(16, 16);
        var design = new MapDesignSettings { Points = [new("Entry", 0, 0) { Role = MapPointRole.Entry, ClearRadius = 1 }], IntentRequired = required };
        var result = MapDesignConstraints.Check(map, design);
        Assert.Single(result.Findings); Assert.Equal(!required, result.Accepted); Assert.Equal("Entry", result.Findings[0].Name);
    }

    [Theory]
    [InlineData(0, 0, true)]
    [InlineData(4, 8, true)]
    [InlineData(7, 0, false)]
    [InlineData(0, 5, false)]
    public void RouteLengthUsesActualOrderedPathSteps(int minimum, int maximum, bool accepted)
    {
        var map = new MapDocument(16, 16);
        var design = new MapDesignSettings { Points = [new("Entry", 1, 1), new("Exit", 7, 1)], MinimumRouteSteps = minimum, MaximumRouteSteps = maximum, IntentRequired = true };
        Assert.Equal(accepted, MapDesignConstraints.Check(map, design).Accepted);
    }

    [Fact]
    public void TargetCrossingDistinguishesARealBottleneckFromABypass()
    {
        var map = new MapDocument(16, 16);
        for (int row = 0; row < 16; row++) for (int column = 0; column < 16; column++) map.Set(column, row, new Cell { BackImage = 0x20000000 });
        for (int column = 1; column <= 7; column++) map.Set(column, 5, new Cell());
        var design = new MapDesignSettings
        {
            Points = [new("Entry", 1, 5) { Role = MapPointRole.Entry }, new("Target", 4, 5) { Role = MapPointRole.Target }, new("Exit", 7, 5) { Role = MapPointRole.Exit }],
            RequireTargetCrossing = true, IntentRequired = true
        };
        Assert.True(MapDesignConstraints.Check(map, design).Accepted);
        for (int column = 1; column <= 7; column++) map.Set(column, 4, new Cell());
        var result = MapDesignConstraints.Check(map, design);
        Assert.False(result.Accepted); Assert.Contains(result.Findings, finding => finding.Name == "Target" && finding.Message.Contains("without crossing"));
    }

    [Fact]
    public void InvalidIntentIsRejectedRatherThanSilentlyDisabled()
    {
        var design = new MapDesignSettings { Points = [new("Entry", 1, 1) { Role = MapPointRole.Entry }], RequireTargetCrossing = true };
        Assert.Throws<InvalidDataException>(() => design.Validate(16, 16));
        design.RequireTargetCrossing = false; design.Points.Add(new("Exit", 8, 8)); design.MinimumRouteSteps = 10; design.MaximumRouteSteps = 5;
        Assert.Throws<InvalidDataException>(() => design.Validate(16, 16));
        design.MinimumRouteSteps = design.MaximumRouteSteps = 0; design.Points[0] = design.Points[0] with { Role = (MapPointRole)99 };
        Assert.Throws<InvalidDataException>(() => design.Validate(16, 16));
    }

    [Fact]
    public void NewIntentRoundTripsAsEditorMetadataAndOldMetadataKeepsItsDefaults()
    {
        var design = new MapDesignSettings { Points = [new("Entry", 3, 3) { Role = MapPointRole.Entry, ClearRadius = 2 }, new("Exit", 9, 9)], MinimumRouteSteps = 5, MaximumRouteSteps = 30, IntentRequired = true };
        var restored = StudioJson.Deserialize<MapDesignFile>(StudioJson.SerializeToUtf8Bytes(new MapDesignFile(1, 16, 16, design)))!;
        Assert.True(design.SameAs(restored.Design));
        var legacy = StudioJson.Deserialize<MapDesignFile>("{\"Version\":1,\"Width\":16,\"Height\":16,\"Design\":{\"Points\":[{\"Name\":\"Old\",\"X\":1,\"Y\":1}],\"Regions\":[]}}")!;
        Assert.False(legacy.Design.HasIntent); Assert.Equal(MapPointRole.None, legacy.Design.Points[0].Role); legacy.Design.Validate(16, 16);
    }

    [Fact]
    public void CreationReviewSeparatesGroundDecorationAndCollisionAndInvalidatesChecks()
    {
        var map = new MapDocument(16, 16); var design = new MapDesignSettings();
        var review = new MapCreationReview(map, design, null); Assert.True(review.Current(map, design));
        map.Set(1, 1, new Cell { BackImage = 3 }); map.Set(2, 2, new Cell { Foreground = new(0, 1) }); map.Set(3, 3, new Cell { BackImage = 0x20000000 });
        var difference = review.Compare(map);
        Assert.Equal(1, difference.GroundCells); Assert.Equal(1, difference.DecorationCells); Assert.Equal(1, difference.CollisionCells); Assert.Equal(3, difference.ChangedCells);
        Assert.False(review.Current(map, design)); Assert.Empty(review.Baseline.Cells);
        review.RecordCheck(map, design, MapDesignConstraints.Check(map, design)); Assert.True(review.Current(map, design));
        design.Points.Add(new("New", 4, 4)); Assert.False(review.Current(map, design));
    }

    [Fact]
    public void CreationComparisonRefusesMismatchedDimensions()
    {
        var review = new MapCreationReview(new MapDocument(16, 16), new(), null);
        Assert.Throws<InvalidOperationException>(() => review.Compare(new MapDocument(32, 16)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TaskDraftRestoresRecipesFavoritesAndChoicesWithoutTrustingOldChecks(bool artworkMatches)
    {
        string root = Path.Combine(Path.GetTempPath(), "CrystalStudio-draft-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        var store = new QuickMapDraftStore(Path.Combine(root, "task.json"));
        var source = new MapCreationSession(new(MapCreationKind.Terrain, 64, 64, "Task", root, true), Parameters(7));
        source.Design.Points.Add(new("Entry", 3, 3) { Role = MapPointRole.Entry });
        using var image = CrystalStudio.Rendering.RasterImage.Create(2, 2, 96, 96, CrystalStudio.Rendering.RasterFormat.Bgra32, null, new byte[16], 8);
        var first = source.Candidates.Remember(source.Settings, Parameters(7), image, design: source.Design)!;
        source.Candidates.SetFavorite(first, true); first.Evaluate(new(10, 10, 100, 0, 1), source.Design);
        source.Candidates.Remember(source.Settings, Parameters(8), image, design: source.Design);
        source.SelectedCandidateIndex = 0; source.CompareAIndex = 1; source.CompareBIndex = 0; source.RequestedDecorationSeed = 23;
        store.Save(QuickMapDraftStore.Capture(source, Recipe(source)));
        var restored = new MapCreationSession(new());
        try
        {
            QuickMapDraftStore.Restore(store.Load(), restored, artworkMatches);
            Assert.False(restored.Settings.SaveImmediately); Assert.Equal("Task", restored.Settings.Name); Assert.True(source.Design.SameAs(restored.Design));
            Assert.Equal(2, restored.Candidates.Items.Count); Assert.True(restored.Candidates.Items[0].IsFavorite);
            Assert.Equal(0, restored.SelectedCandidateIndex); Assert.Equal(1, restored.CompareAIndex); Assert.Equal(0, restored.CompareBIndex);
            Assert.Equal(artworkMatches ? 23 : null, restored.RequestedDecorationSeed);
            Assert.Equal(artworkMatches, restored.Candidates.Items[0].Thumbnail != null);
            Assert.Null(restored.Candidates.Items[0].Metrics); Assert.Null(restored.Output);
            Assert.False(File.Exists(Path.Combine(root, "Task.map")));
        }
        finally { restored.Candidates.Dispose(); source.Candidates.Dispose(); }
    }

    [Fact]
    public void InvalidDraftCannotReplaceAValidDraft()
    {
        var session = new MapCreationSession(new(MapCreationKind.Terrain, 64, 64), Parameters(3));
        string root = Path.Combine(Path.GetTempPath(), "CrystalStudio-draft-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        var store = new QuickMapDraftStore(Path.Combine(root, "task.json")); var valid = QuickMapDraftStore.Capture(session, Recipe(session)); store.Save(valid);
        byte[] previous = File.ReadAllBytes(store.FilePath);
        Assert.Throws<InvalidDataException>(() => store.Save(valid with { Selected = 99 }));
        Assert.Equal(previous, File.ReadAllBytes(store.FilePath));
        var bad = new QuickMapDraftCandidate(new(MapCreationKind.Terrain, 64, 64), Parameters(1), new(), true, new(640, 400, 0, new byte[1]), null, null);
        Assert.Throws<InvalidDataException>(() => store.Save(valid with { Candidates = [bad] })); Assert.Equal(previous, File.ReadAllBytes(store.FilePath));
    }

    internal static Dictionary<string, string> Parameters(int seed) => new() { ["SeedBox"] = seed.ToString(System.Globalization.CultureInfo.InvariantCulture), [GenerationAlgorithms.Parameter] = "3" };
    internal static MapCreationRecipe Recipe(MapCreationSession session) => new(1, DateTime.UtcNow, session.Settings.Kind, session.Settings.Width, session.Settings.Height,
        AssetSourceFormat.Default, null, "test", new(session.Parameters), new()) { Design = session.Design.Copy() };
    internal static MapDocument DecoratedMap()
    {
        var map = new MapDocument(32, 32);
        for (int row = 0; row < 32; row++) for (int column = 0; column < 32; column++) map.Set(column, row, new Cell { BackImage = 1 });
        var stamps = new List<PlacedStamp>();
        Add("Native flower", 4, 4, false); Add("Native detail", 20, 20, false); Add("Native structure", 12, 12, true); map.RestoreStamps(stamps); return map;
        void Add(string name, int column, int row, bool blocking)
        {
            var before = map.Get(column, row).Copy(); var after = before.Copy(); after.Foreground = new(0, 1); if (blocking) after.BackImage |= 0x20000000;
            var nextBefore = map.Get(column + 1, row).Copy(); var nextAfter = nextBefore.Copy(); nextAfter.MiddleImage = 1;
            map.Set(column, row, after); map.Set(column + 1, row, nextAfter);
            stamps.Add(new(Guid.NewGuid(), name, column, row, [new(0, 0, new Cell { Foreground = new(0, 1) }), new(1, 0, new Cell { MiddleImage = 1 })],
                [new(0, 0, before, after.Copy(), [after.Foreground], [0]), new(1, 0, nextBefore, nextAfter.Copy())]));
        }
    }
    private static void AssertMapsEqual(MapDocument expected, MapDocument actual)
    {
        Assert.Equal(expected.Width, actual.Width); Assert.Equal(expected.Height, actual.Height);
        for (int row = 0; row < expected.Height; row++) for (int column = 0; column < expected.Width; column++) Assert.True(expected.Get(column, row).SameContents(actual.Get(column, row)), $"Cell ({column}, {row}) differs.");
    }
}
