using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Avalonia.Headless.XUnit;

namespace CrystalStudio.Tests;

public sealed class MapGenerationAcceptanceTests
{
    private sealed record AcceptanceFixture(int Version, int Width, int Height, int[] CalibrationSeeds, int[] HoldoutSeeds, string[] ObjectIds, string Notes);
    private sealed record Sample(string Pair, string Variant, string Kind, int Seed, int Algorithm, double Milliseconds, long AllocatedBytes,
        MapCandidateMetrics Metrics, IReadOnlyList<MapConstraintFinding> Findings, NativeLoopReport? LoopVerification);

    [AvaloniaFact]
    public void RepositoryNativeHoldoutMapsProduceReproducibleEvidenceAndUnscoredBlindReviewPack()
    {
        string fixturePath = Path.Combine(AppContext.BaseDirectory, "Fixtures", "map-generation-acceptance.json");
        var fixture = JsonSerializer.Deserialize<AcceptanceFixture>(File.ReadAllText(fixturePath))!;
        Assert.Equal(1, fixture.Version); Assert.Empty(fixture.CalibrationSeeds.Intersect(fixture.HoldoutSeeds));
        Assert.Equal(fixture.HoldoutSeeds.Length, fixture.HoldoutSeeds.Distinct().Count());
        var index = StampCategories.Read(StampCategories.FindIndex()!); var terrain = TerrainCatalogue.Load();
        var walls = WallCatalogue.Read(WallCatalogue.ResolvePath()); var nativePaths = new List<string>();
        var scenery = fixture.ObjectIds.Select(id =>
        {
            var entry = index.Objects.Single(item => item.Id == id); Assert.Equal("reviewed", entry.ReviewStatus);
            string path = StampCategories.Resolve(index.ObjectRoot, entry.File); nativePaths.Add(path);
            var tree = BrushTree.FromCells(entry.Name, path, CellCodec.LoadStamp(path));
            return new ProvinceScenery(tree, Enum.GetValues<SpecialBrushKind>().First(kind => kind.Matches(entry)), entry.RegionGroups) { CatalogueId = entry.Id };
        }).ToArray();
        Assert.Contains(scenery, item => item.Tree.Cells.Count > 1);
        string? output = Environment.GetEnvironmentVariable("CRYSTALSTUDIO_ALGORITHM_ARTIFACTS");
        string? archive = string.IsNullOrWhiteSpace(output) ? null : Path.Combine(output, "native-acceptance");
        string run = "run-" + DateTime.UtcNow.ToString("yyyyMMddTHHmmssfff", System.Globalization.CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N")[..8];
        string? pack = archive == null ? null : Path.Combine(archive, run);
        if (pack != null) { Directory.CreateDirectory(Path.Combine(pack, "blind")); Directory.CreateDirectory(Path.Combine(pack, "provenance")); }
        var samples = new List<Sample>(); var review = new StringBuilder("Pair,Reviewer,PreferredVariant,EditMinutesA,EditMinutesB,RouteIssuesA,RouteIssuesB,SeamIssuesA,SeamIssuesB,Notes\n"); int pairIndex = 0;
        foreach (string kind in new[] { "terrain", "province", "dungeon" }) foreach (int seed in fixture.HoldoutSeeds)
        {
            string pair = "pair-" + (++pairIndex).ToString("00"); bool swapped = (SHA256.HashData(Encoding.UTF8.GetBytes(kind + "/" + seed))[0] & 1) != 0;
            MapDesignSettings? sharedDesign = null;
            foreach (int algorithm in new[] { 2, 3 })
            {
                var source = new MapDocument(fixture.Width, fixture.Height); long allocated = GC.GetAllocatedBytesForCurrentThread(); var watch = Stopwatch.StartNew();
                MapDocument generated; NativeLoopReport? loopReport = null;
                if (kind == "terrain") generated = GeneratedMapSnapshot.Terrain(TerrainGenerator.Plan(source, new(0, 0, fixture.Width, fixture.Height), terrain,
                    new(terrain.Sets.Single(set => set.Id == "0:0:0"), terrain.Sets.Single(set => set.Id == "0:0:950"), null, seed) { AlgorithmVersion = algorithm }));
                else if (kind == "province") generated = ProvinceGenerator.Plan(source, terrain, walls, scenery,
                    new(seed, ProvinceStyle.All[0], ForestChance: 100, CliffChance: 0, BeachChance: 0, RiverChance: 0, Vegetation: 60) { AlgorithmVersion = algorithm }).Generated;
                else
                {
                    var definition = TempleDefinition.All[2];
                    var plan = TempleGenerator.Plan(source, walls.Families.Single(item => item.Id == definition.WallId).WithMode(true), terrain.Sets.Single(set => set.Id == definition.FloorId),
                        new(definition, seed) { AlgorithmVersion = algorithm }); generated = plan.Generated; loopReport = plan.LoopVerification;
                }
                watch.Stop(); allocated = GC.GetAllocatedBytesForCurrentThread() - allocated;
                Assert.Empty(source.Cells); Assert.Empty(source.PlacedStamps);
                if (kind == "province") Assert.NotEmpty(generated.PlacedStamps);
                sharedDesign ??= EvaluationMarkers(generated);
                var metrics = MapRouteAnalysis.Measure(generated, design: sharedDesign); var check = MapDesignConstraints.Check(generated, sharedDesign);
                var directAnchors = MapGenerationDepthTests.Anchors(generated, 1);
                var origin = sharedDesign.Points[0];
                var directDistances = MapGenerationDepthTests.Flood(directAnchors, generated.Width, origin.Y * generated.Width + origin.X);
                Assert.Equal(directAnchors.Count(open => open), metrics.WalkableCells);
                Assert.Equal(directDistances.Count(distance => distance >= 0), metrics.StartArea);
                var target = sharedDesign.Points[1]; int distance = directDistances[target.Y * generated.Width + target.X];
                Assert.Equal(distance >= 0 ? distance : (int?)null, metrics.RouteSteps);
                string variant = (algorithm == 2) != swapped ? "A" : "B";
                samples.Add(new(pair, variant, kind, seed, algorithm, watch.Elapsed.TotalMilliseconds, allocated, metrics, check.Findings, loopReport));
                if (pack != null)
                {
                    string directory = Path.Combine(pack, "blind", pair); Directory.CreateDirectory(directory);
                    string path = Path.Combine(directory, variant + ".map"); generated.Save(path); MapDesignStore.Save(path, generated, sharedDesign);
                    var restored = MapDocument.Load(path); Assert.Equal(metrics.TopologyFingerprint, MapRouteAnalysis.Measure(restored, design: sharedDesign).TopologyFingerprint);
                    using var image = MapRouteAnalysis.Draw(generated, MapRouteAnalysis.Analyze(generated, sharedDesign.Points), sharedDesign);
                    using var picture = File.Create(Path.Combine(directory, variant + "-collision.png")); image.Save(picture);
                }
            }
            review.Append(pair).Append(",,,,,,,,,\n");
        }
        Assert.Equal(18, samples.Count);
        if (pack == null) return;
        string root = Directory.GetParent(Path.GetDirectoryName(StampCategories.FindIndex())!)!.FullName;
        var sources = nativePaths.Concat(new[] { fixturePath, DataPaths.ResolveEditor("catalogue", "terrain.json"), WallCatalogue.ResolvePath(), StampCategories.FindIndex()!, DataPaths.ResolveEditor("catalogue", "province-decoration-ground.json") })
            .Distinct(StringComparer.OrdinalIgnoreCase).Select(path => new { Path = Path.GetRelativePath(root, path).Replace('\\', '/'), Sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))), Bytes = new FileInfo(path).Length }).ToArray();
        var options = new JsonSerializerOptions { WriteIndented = true };
        File.WriteAllText(Path.Combine(pack, "provenance", "results-and-answer-key.json"), JsonSerializer.Serialize(new
        {
            Fixture = fixture, FixtureSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(fixturePath))), Sources = sources, Samples = samples,
            HumanReviewCompleted = false, Notes = "Raw repository .X object collision/anchor data and reviewed catalogue cells were used. Collision diagrams are not sprite renders. Timings are observations, not a speedup verdict. Evaluation markers are selected from v2 and held fixed for its paired v3 map; they were not generation constraints."
        }, options));
        File.WriteAllText(Path.Combine(pack, "blind", "review.csv"), review.ToString(), new UTF8Encoding(true));
        File.WriteAllText(Path.Combine(pack, "开始验收.md"), "# 原生地图盲评包\n\n先打开 blind 中每对 A.map / B.map，再填写 blind/review.csv；完成前不要查看 provenance 的版本对应表。\n\n请在匹配的真实客户端素材环境中，以相同方形占地和相同交付任务评估断路、素材接缝、装饰穿插，以及修到可交付所需时间。遇到问题请保留地图与问题坐标，不要只记录审美偏好。\n\n碰撞示意图不包含真实贴图，不能用来证明素材接缝正确。review.csv 为空白人工记录表，尚未进行专业编辑员盲评或玩家测试。\n\n本包使用仓库原生 .X 素材与已审核目录数据；不包含真实客户端图像库，也没有虚构编辑员退回案例。若验收种子被用于调参，应更换独立验收集。\n");
        File.WriteAllText(Path.Combine(archive!, "latest-run.txt"), run);
        File.WriteAllText(Path.Combine(archive!, "开始验收.md"), "# 原生素材验收入口\n\n最新一轮目录记录在 latest-run.txt，请进入对应目录阅读开始验收.md。每次运行创建独立目录，不覆盖旧地图或已填写的盲评表。\n\n本轮目录：`" + run + "`。\n");
    }

    private static MapDesignSettings EvaluationMarkers(MapDocument map)
    {
        var anchors = MapGenerationDepthTests.Anchors(map, 1); int start = Array.IndexOf(anchors, true); Assert.True(start >= 0);
        var distance = MapGenerationDepthTests.Flood(anchors, map.Width, start); int goal = Enumerable.Range(0, distance.Length).MaxBy(index => distance[index]);
        return new() { RequireRoute = true, Points = [new("Entry", start % map.Width, start / map.Width), new("Target", goal % map.Width, goal / map.Width)] };
    }
}
