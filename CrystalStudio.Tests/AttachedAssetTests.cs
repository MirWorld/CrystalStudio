using Avalonia.Headless.XUnit;

namespace CrystalStudio.Tests;

public sealed class AttachedAssetTests
{
    [AvaloniaFact]
    public async Task AttachedLibraryDecodesInFullAndOverviewRenderingWithoutReplacingClient()
    {
        string root = CreateRoot();
        try
        {
            Fixture.CreateLibraries(Path.Combine(root, "client"));
            Fixture.WriteLibrary(Path.Combine(root, "scene.lib"), 96, 64, 12, 34, 56);
            File.WriteAllText(Path.Combine(root, "libraries.json"), """
                {"schemaVersion":1,"libraries":[{"id":400,"file":"scene.lib"}]}
                """);
            using var assets = AssetStore.Load(Path.Combine(root, "client"), attachmentsRoot: root);
            Assert.True(assets.Libraries.ContainsKey(0));
            var sprite = assets.Libraries[400].Decode(0)!;
            try { Assert.Equal(new byte[] { 12, 34, 56, 255 }, Fixture.Pixels(sprite.Bitmap)[..4]); }
            finally { sprite.Bitmap.Dispose(); }
            var overview = await assets.PrepareOverviewAsync([(400, 0)], 2, default);
            Assert.Equal(24, overview[(400, 0)].Bitmap.PixelWidth);
            Assert.Equal(new byte[] { 12, 34, 56, 255 }, Fixture.Pixels(overview[(400, 0)].Bitmap)[..4]);
        }
        finally { Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData("{\"schemaVersion\":2,\"libraries\":[]}")]
    [InlineData("{\"schemaVersion\":1,\"libraries\":[{\"id\":0,\"file\":\"scene.lib\"}]}")]
    [InlineData("{\"schemaVersion\":1,\"libraries\":[{\"id\":400,\"file\":\"../scene.lib\"}]}")]
    [InlineData("{\"schemaVersion\":1,\"libraries\":[{\"id\":400,\"file\":\"source.png\"}]}")]
    [InlineData("{\"schemaVersion\":1,\"libraries\":[{\"id\":400,\"file\":\"scene.lib\"},{\"id\":400,\"file\":\"scene.lib\"}]}")]
    public void InvalidAttachmentsDoNotOverwriteClientLibraries(string manifest)
    {
        string root = CreateRoot();
        try
        {
            Fixture.CreateLibraries(Path.Combine(root, "client"));
            Fixture.WriteLibrary(Path.Combine(root, "scene.lib"), 2, 2, 1, 2, 3);
            File.WriteAllText(Path.Combine(root, "libraries.json"), manifest);
            Assert.Throws<InvalidDataException>(() => AssetStore.Load(Path.Combine(root, "client"), attachmentsRoot: root));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void MissingManifestPreservesExistingClientLoading()
    {
        string root = CreateRoot();
        try
        {
            Fixture.CreateLibraries(root);
            using var assets = AssetStore.Load(root, attachmentsRoot: Path.Combine(root, "absent"));
            Assert.Equal(3, assets.Libraries.Count);
        }
        finally { Directory.Delete(root, true); }
    }

    private static string CreateRoot()
    {
        string root = Path.Combine(Path.GetTempPath(), "CrystalStudio-Attachments-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }
}
