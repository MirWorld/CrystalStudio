using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CrystalStudio.Tests;

public sealed class JsonCompatibilityTests
{
    [Fact]
    public void SourceGenerationKeepsMissingWallPropertyDefaults()
    {
        const string json = """{"Id":"wall","Name":"Wall","Kind":"wall"}""";
        var generated = StudioJson.Deserialize<WallCatalogue>("""{"SchemaVersion":1,"Families":[""" + json + "]}")!.Families[0];
        var previous = JsonSerializer.Deserialize<WallFamily>(json)!;
        Assert.Equal(previous.StrokeGridPieces, generated.StrokeGridPieces);
        Assert.Equal(previous.PaintSizePieces, generated.PaintSizePieces);
        Assert.Equal(previous.AreaBrush, generated.AreaBrush);
        Assert.Equal(previous.SupportsJunctions, generated.SupportsJunctions);
        Assert.NotNull(generated.Entrances);
    }

    [Fact]
    public void SourceGenerationKeepsSettingsBrushesAndMetadataCompatible()
    {
        var cells = new List<StampCell> { new(0, 0, new() { Foreground = new(2, 1), BackImage = 0x20000000 }) };
        Compare(new LibrarySettings("E:/游戏/Data", "zh-CN", AssetSourceFormat.Sanxiu));
        Compare(new TreeListDocument(1, "树木", [new("id", "Tree", "Tree", cells)]));
        Compare(new StampMetadata.Payload(1, []), new() { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault });
        var report = new ExtractionReport(1, "session", DateTime.UnixEpoch, ["test.map"], [], [],
            [new(Guid.Empty, "Object", "Tree", "Discovered", "Discovered", true, false, true, "fingerprint", null, [new("test.map", 1, 2, false)])]);
        Compare(report, new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true });

        var tree = BrushTree.FromCells("Tree", "", cells);
        byte[] original = JsonSerializer.SerializeToUtf8Bytes(new { Name = "Tree", Cells = tree.Cells });
        Assert.Equal(Convert.ToHexString(SHA256.HashData(original)), TreeBrushList.Entry("Tree", "Tree", cells).Id);
    }

    [Fact]
    public void BundledCataloguesLoadUsingGeneratedContracts()
    {
        Assert.NotEmpty(StampCategories.Read(DataPaths.ResolveEditor("catalogue", "palette.json")).Objects);
        Assert.NotEmpty(TownCatalogue.Load().Families);
        Assert.NotEmpty(TerrainCatalogue.Load().Sets);
        Assert.NotEmpty(WallCatalogue.Read(WallCatalogue.ResolvePath()).Families);
        Assert.NotEmpty(RiverArtworkCatalogue.Load(DataPaths.ResolveEditor("catalogue", "river-artwork.json"), allowResearch: true).Pieces);
    }

    private static void Compare<T>(T value, JsonSerializerOptions? options = null)
    {
        byte[] previous = JsonSerializer.SerializeToUtf8Bytes(value, options);
        byte[] generated = StudioJson.SerializeToUtf8Bytes(value, options);
        Assert.Equal(previous, generated);
        var restored = StudioJson.Deserialize<T>(previous, options);
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(previous), JsonNode.Parse(StudioJson.SerializeToUtf8Bytes(restored, options))));
    }
}
