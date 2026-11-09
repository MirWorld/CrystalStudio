using System.Diagnostics;
using System.Text.Json;

namespace CrystalStudio.Tests;

public sealed class MapGenerationBenchmarkTests
{
    private sealed record Sample(string Kind, int Version, int Seed, double Milliseconds, long AllocatedBytes,
        int WalkableCells, double ConnectedRatio, int StampCount);

    [Fact]
    public void FixedSeedSamplesRecordTimingAndNativeCollisionMetrics()
    {
        int[] seeds = [101, 211, 307, 401, 503, 601, 701, 809, 907, 1009, 1103, 1201];
        var terrain = TerrainCatalogue.Load(); var walls = WallCatalogue.Read(WallCatalogue.ResolvePath());
        var basis = terrain.Sets.Single(set => set.Id == "0:0:0"); var patch = terrain.Sets.Single(set => set.Id == "0:0:950");
        var definition = TempleDefinition.All[2]; var floor = terrain.Sets.Single(set => set.Id == definition.FloorId);
        var family = walls.Families.Single(item => item.Id == definition.WallId).WithMode(true);
        ProvinceScenery[] scenery = [new(new("Benchmark tree", "", [new(0, 0, new Cell { BackImage = 0x60000000, Foreground = new(2, 1) })]), SpecialBrushKind.Tree, [ProvinceStyle.All[0].Region])];
        var samples = new List<Sample>();
        foreach (string kind in new[] { "terrain", "province", "dungeon" })
        {
            Generate(kind, 1, 3); Generate(kind, 2, 3);
            foreach (int seed in seeds) foreach (int version in Array.IndexOf(seeds, seed) % 2 == 0 ? new[] { 1, 2 } : new[] { 2, 1 })
            {
                var source = new MapDocument(256, 256);
                long allocated = GC.GetAllocatedBytesForCurrentThread(); var watch = Stopwatch.StartNew();
                var map = Generate(kind, version, seed, source);
                watch.Stop(); allocated = GC.GetAllocatedBytesForCurrentThread() - allocated;
                var metrics = MapRouteAnalysis.Measure(map);
                Assert.True(metrics.WalkableCells > 0); Assert.Empty(source.Cells); Assert.Empty(source.PlacedStamps);
                samples.Add(new(kind, version, seed, watch.Elapsed.TotalMilliseconds, allocated, metrics.WalkableCells, metrics.ConnectedRatio, metrics.StampCount));
            }
        }
        Assert.Equal(72, samples.Count);
        if (Environment.GetEnvironmentVariable("CRYSTALSTUDIO_ALGORITHM_ARTIFACTS") is { Length: > 0 } directory)
        {
            Directory.CreateDirectory(directory);
            var summary = samples.GroupBy(sample => (sample.Kind, sample.Version)).Select(group =>
            {
                var elapsed = group.Select(sample => sample.Milliseconds).Order().ToArray();
                return new { group.Key.Kind, group.Key.Version, Count = group.Count(), P50Milliseconds = (elapsed[(elapsed.Length - 1) / 2] + elapsed[elapsed.Length / 2]) / 2,
                    P95Milliseconds = elapsed[(int)Math.Ceiling(elapsed.Length * .95) - 1],
                    MeanAllocatedBytes = group.Average(sample => sample.AllocatedBytes), MeanConnectedRatio = group.Average(sample => sample.ConnectedRatio), MeanStamps = group.Average(sample => sample.StampCount) };
            }).ToArray();
            File.WriteAllText(Path.Combine(directory, "seed-benchmark.json"), JsonSerializer.Serialize(new
            {
                GeneratedAtUtc = DateTime.UtcNow, Framework = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
                Machine = System.Runtime.InteropServices.RuntimeInformation.OSDescription, Width = 256, Height = 256,
                Notes = "Warm-cache microbenchmark, 12 seeds per mode. Synthetic one-cell scenery, no native water or client sprites. Allocated bytes are current-thread allocation, not peak memory. No performance assertions.",
                Seeds = seeds, Summary = summary, Samples = samples
            }, new JsonSerializerOptions { WriteIndented = true }));
        }

        MapDocument Generate(string kind, int version, int seed, MapDocument? source = null)
        {
            source ??= new(256, 256);
            if (kind == "terrain") return GeneratedMapSnapshot.Terrain(TerrainGenerator.Plan(source, new(0, 0, 256, 256), terrain,
                new(basis, patch, null, seed) { AlgorithmVersion = version }));
            if (kind == "province") return ProvinceGenerator.Plan(source, terrain, walls, scenery,
                new(seed, ProvinceStyle.All[0], ForestChance: 0, CliffChance: 0, BeachChance: 0, RiverChance: 0) { AlgorithmVersion = version }).Generated;
            return TempleGenerator.Plan(source, family, floor, new(definition, seed) { AlgorithmVersion = version }).Generated;
        }
    }
}
