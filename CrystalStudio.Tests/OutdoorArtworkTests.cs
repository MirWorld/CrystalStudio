namespace CrystalStudio.Tests;

public sealed class OutdoorArtworkTests
{
    [Fact]
    public void TerrainShoreAndRiverAcceptReplacementPixels()
    {
        using var assets = Assets();
        var terrain = new TerrainCatalogue();
        var set = new TerrainSet { Library = 0,
            Shapes = { [15] = [new TerrainVariant { Index = 0, Weight = 1, PixelSha256 = new string('0', 64) }] } };
        Assert.True(terrain.MatchesAssets(set, assets));

        var water = new TerrainWaterCatalogue
        {
            Sprites = [new WaterSprite { Library = 1, Image = 1, Width = 48, Height = 32, PixelSha256 = new string('0', 64) }]
        };
        Assert.True(water.MatchesAssets(assets));
        var river = new RiverArtworkCatalogue(1, "reviewed", "test", "test", 0, "", "", [],
            [new RiverArtworkProof(1, 1, 48, 32, new string('0', 64))]).PrepareSavedLayouts(assets);
        Assert.Equal(80, river.Sprites[(1, 1)].Bgra[0]);
        Assert.Equal(255, river.Sprites[(1, 1)].Bgra[3]);

        set.Shapes[15][0].Index = 1;
        water.Sprites[0].Image = 2;
        Assert.False(terrain.MatchesAssets(set, assets));
        Assert.False(water.MatchesAssets(assets));
    }

    private static AssetStore Assets()
    {
        string root = Path.Combine(Path.GetTempPath(), "CrystalStudio-OutdoorArtwork-" + Guid.NewGuid().ToString("N"));
        Fixture.CreateLibraries(root);
        return AssetStore.Load(root);
    }
}
