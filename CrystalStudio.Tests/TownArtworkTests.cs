namespace CrystalStudio.Tests;

public sealed class TownArtworkTests
{
    [Fact]
    public void ReplacementPixelsAndLargerImagesRemainUsableForTownHouses()
    {
        using var assets = Assets();
        var family = Family(1);
        var sprites = new Dictionary<(int Library, int Image), Sprite>();
        var prepared = TownCatalogue.Prepare(family, assets, sprites, out int skipped);

        Assert.Equal(0, skipped);
        Assert.Equal(2, prepared.Pieces.Count);
        Assert.Equal((192, 256), (sprites[(2, 1)].Bitmap.PixelWidth, sprites[(2, 1)].Bitmap.PixelHeight));
        var plan = TownGenerator.Generate(prepared, new TownOptions(Ground: false, Greenery: false,
            WallChance: 0, Decorations: TownDecorations.None));
        Assert.True(plan.Buildings.Count(building => building.Piece.Role == "house") >= 3);
    }

    [Fact]
    public void MissingHouseImageStillPreventsUsingAnIncompleteTemplate()
    {
        using var assets = Assets();
        var error = Assert.Throws<InvalidDataException>(() =>
            TownCatalogue.Prepare(Family(2), assets, [], out _));
        Assert.Contains("image indices", error.Message);
    }

    [Fact]
    public void SanxiuTownPreparationResolvesCrystalObjectLibraryReferences()
    {
        string folder = Path.Combine(Path.GetTempPath(), "CrystalStudio-TownSanxiu-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            Fixture.WriteLibrary(Path.Combine(folder, "Objects.lib"), 192, 256, 20, 100, 200);
            using var assets = AssetStore.Load(folder, AssetSourceFormat.Sanxiu);
            var sprites = new Dictionary<(int Library, int Image), Sprite>();
            var prepared = TownCatalogue.Prepare(Family(1), assets, sprites, out int skipped);

            Assert.Equal(0, skipped);
            Assert.Equal(2, prepared.Pieces.Count);
            Assert.All(prepared.Pieces.SelectMany(p => p.Cells), c => Assert.Equal((short)2000, c.Cell.Foreground!.Library));
            Assert.Contains((2000, 1), sprites.Keys);
            Assert.Contains((2000, 1), prepared.SpriteProofs.Select(p => (p.Library, p.Image)));
        }
        finally { Directory.Delete(folder, true); }
    }

    private static AssetStore Assets()
    {
        string folder = Path.Combine(Path.GetTempPath(), "CrystalStudio-TownArtwork-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        Fixture.WriteLibrary(Path.Combine(folder, "Objects.lib"), 192, 256, 20, 100, 200);
        return AssetStore.Load(folder);
    }

    private static TownFamily Family(short southwestImage)
    {
        TownPiece House(string direction, short image) => new(direction, direction, "house", direction, new string('0', 64),
            new TownBounds(0, 0, 1, 1), [new StampCell(0, 0, new Cell { Foreground = new Front(2, image) })],
            [new TownPoint(0, 0)], [new TownPoint(0, 0)], false);
        return new TownFamily("replacement", "Replacement town",
            new TownSource(1, "test.map", "test", new string('0', 64), []), 4, "test", "", "",
            [House("SE", 1), House("SW", southwestImage)],
            [new TownSpriteProof(2, 1, new string('0', 64)), new TownSpriteProof(2, southwestImage, new string('1', 64))]);
    }
}
