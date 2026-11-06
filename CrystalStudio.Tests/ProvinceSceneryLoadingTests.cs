namespace CrystalStudio.Tests;

public sealed class ProvinceSceneryLoadingTests
{
    [Theory]
    [InlineData(99, 1)]
    [InlineData(2, 2)]
    [InlineData(2, 0)]
    public void UnavailableRockDoesNotPreventLoadingValidTrees(short library, short image)
    {
        string root = Path.Combine(Path.GetTempPath(), "CrystalStudio-scenery-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            Fixture.WriteLibrary(Path.Combine(root, "Objects.lib"), 2, 2, 10, 20, 30);
            using var assets = new AssetStore();
            assets.Libraries[2] = new CrystalLibrary(Path.Combine(root, "Objects.lib"));
            CellCodec.SaveStamp(Path.Combine(root, "rock.X"), [new(0, 0, new Cell { Foreground = new(library, image) })]);
            CellCodec.SaveStamp(Path.Combine(root, "tree.X"), [new(0, 0, new Cell { Foreground = new(2, 1) })]);
            var index = new StampCategoryIndex(1, root, root,
                [Entry("a-rock", "rock.X", "Rock"), Entry("b-tree", "tree.X", "Tree")]);

            var scenery = ProvinceGenerator.LoadScenery(index, assets);
            Assert.Equal("b-tree", Assert.Single(scenery).CatalogueId);
            Assert.Equal(SpecialBrushKind.Tree, scenery[0].Kind);

            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            Assert.Throws<OperationCanceledException>(() => ProvinceGenerator.LoadScenery(index, assets, cancelled.Token));
            assets.Libraries.Clear();
            var error = Assert.Throws<InvalidDataException>(() => ProvinceGenerator.LoadScenery(index, assets));
            Assert.Contains("No matching outdoor tree stamps", error.Message);
        }
        finally { Directory.Delete(root, true); }
    }

    private static CategorizedStamp Entry(string id, string file, string type) =>
        new(id, file, id, type, null, [], ["BichonProvince"], [], [], null, "unknown", null, false, "reviewed");
}
