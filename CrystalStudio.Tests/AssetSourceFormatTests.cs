using System.IO.Compression;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;

namespace CrystalStudio.Tests;

public sealed class AssetSourceFormatTests
{
    [Fact]
    public void SanxiuClientRootPrefersDataLibrariesOverMapDirectory()
    {
        using var files = new TemporaryAssets();
        files.Write("Tiles.lib", 6, 20, 80);
        files.Write("Objects.lib", 5, 40, 90);
        string data = Path.Combine(files.Root, "Data");
        Directory.CreateDirectory(data);
        Directory.Move(Path.Combine(files.Root, "Map"), Path.Combine(data, "Map"));
        Directory.CreateDirectory(Path.Combine(files.Root, "Map"));
        File.WriteAllText(Path.Combine(files.Root, "Map", "Tiles.lib"), "map folder is not the library folder");
        foreach (string selected in new[] { files.Root, data, Path.Combine(data, "Map") })
        {
            using var assets = AssetStore.Load(selected, AssetSourceFormat.Sanxiu);
            Assert.Equal(new[] { 0, 2000 }, assets.Libraries.Keys.Order());
            Assert.All(assets.Libraries.Values, library => Assert.StartsWith(data, library.Path));
        }
    }

    [Fact]
    public void SanxiuGameRootSelectsDataInsteadOfTheSiblingMapFolder()
    {
        using var files = new TemporaryAssets();
        files.Write("Tiles.Lib", 5, 12, 34);
        string data = Path.Combine(files.Root, "Data");
        Directory.CreateDirectory(data);
        Directory.Move(Path.Combine(files.Root, "Map"), Path.Combine(data, "Map"));
        Directory.CreateDirectory(Path.Combine(files.Root, "Map"));
        using var store = AssetStore.Load(files.Root, AssetSourceFormat.Sanxiu);
        Assert.Equal(12, Fixture.Pixels(store.Libraries[0].Decode(0)!.Bitmap)[0]);
        Assert.StartsWith(data, store.Libraries[0].Path);
    }

    [Fact]
    public void SanxiuLibraryLoadsPlainV5AndMapsLayerIds()
    {
        using var files = new TemporaryAssets();
        files.Write("Tiles.Lib", 5, 12, 34);
        files.Write("SmTiles.Lib", 5, 56, 78);
        files.Write("Objects2.Lib", 5, 90, 123);

        using var store = AssetStore.Load(files.Root, AssetSourceFormat.Sanxiu);
        Assert.Equal([0, 1000, 2001], store.Libraries.Keys.Order());
        Assert.Equal(new SpriteHeader(2, 2, 3, -4), store.Libraries[0].ReadHeader(0));
        Assert.Equal(12, Fixture.Pixels(store.Libraries[0].Decode(0)!.Bitmap)[0]);
        Assert.Equal(56, Fixture.Pixels(store.Libraries[1000].Decode(0)!.Bitmap)[0]);
        Assert.Equal(90, Fixture.Pixels(store.Libraries[2001].Decode(0)!.Bitmap)[0]);
        Assert.Equal(TilePatternLayout.Mir2, AutoTileBrush.Layout(0, MapLayer.Ground, AssetSourceFormat.Sanxiu));
        Assert.Equal(TilePatternLayout.Small, AutoTileBrush.Layout(1000, MapLayer.Middle, AssetSourceFormat.Sanxiu));
    }

    [Fact]
    public async Task SanxiuLibraryLoadsPlainV6AndOverview()
    {
        using var files = new TemporaryAssets();
        files.Write("Tiles.Lib", 6, 18, 32);

        using var store = AssetStore.Load(files.Root, AssetSourceFormat.Sanxiu);
        var library = store.Libraries[0];
        Assert.Equal(new SpriteHeader(2, 2, 3, -4), library.ReadHeader(0));
        Assert.Equal(18, Fixture.Pixels(library.Decode(0)!.Bitmap)[0]);
        var overview = await store.PrepareOverviewAsync([(0, 0)], 2, CancellationToken.None);
        Assert.Equal(2, overview[(0, 0)].OriginalWidth);
        Assert.Equal(1, overview[(0, 0)].Bitmap.PixelWidth);
        Assert.Equal(18, Fixture.Pixels(overview[(0, 0)].Bitmap)[0]);
    }

    [AvaloniaFact]
    public async Task SanxiuCategoryPreviewsResolveAllThreeLayerLibraryIds()
    {
        using var files = new TemporaryAssets();
        files.Write("Tiles.Lib", 5, 18, 32);
        files.Write("SmTiles2.Lib", 5, 38, 52);
        files.Write("Objects3.Lib", 5, 58, 72);
        using var assets = AssetStore.Load(files.Root, AssetSourceFormat.Sanxiu);

        Cell[] layers =
        [
            new() { BackLibrary = 0, BackImage = 1 },
            new() { MiddleLibrary = 1, MiddleImage = 1 },
            new() { Foreground = new Front(2, 1) }
        ];
        for (int i = 0; i < layers.Length; i++)
        {
            string path = Path.Combine(files.Root, $"category-layer-{i}.X");
            CellCodec.SaveStamp(path, [new StampCell(0, 0, layers[i])]);
            var converted = CellCodec.LoadStamp(path, AssetSourceFormat.Sanxiu).Single().Cell;
            Assert.Equal(0, converted.BackLibrary);
            Assert.Equal(i == 1 ? 1001 : 0, converted.MiddleLibrary);
            Assert.Equal(i == 2 ? (short?)2002 : null, converted.Foreground?.Library);
            var preview = await MainWindow.RenderCategoryPreviewAsync(path, assets, CancellationToken.None);
            Assert.NotNull(preview);
            using (preview)
            {
                byte[] pixels = new byte[preview!.PixelWidth * preview.PixelHeight * 4];
                preview.CopyPixels(pixels, preview.PixelWidth * 4, 0);
                Assert.True(Enumerable.Range(0, pixels.Length / 4).Any(pixel => pixels[pixel * 4 + 3] != 0),
                    $"The category preview for layer {i} was transparent.");
            }
        }

        string nativePath = Path.Combine(files.Root, "category-native.X");
        CellCodec.SaveStamp(nativePath, [new StampCell(0, 0, new Cell
        {
            MiddleLibrary = 1001, MiddleImage = 1, Foreground = new Front(2002, 1)
        })]);
        var alreadyConverted = CellCodec.LoadStamp(nativePath, AssetSourceFormat.Sanxiu).Single().Cell;
        Assert.Equal(1001, alreadyConverted.MiddleLibrary);
        Assert.Equal((short?)2002, alreadyConverted.Foreground?.Library);
    }

    [Fact]
    public async Task SwitchingOverviewRequestsCancelsOldWorkWithoutPoisoningAssets()
    {
        using var files = new TemporaryAssets();
        files.Write("Tiles.Lib", 6, 18, 32, size: 1024);
        using var store = AssetStore.Load(files.Root, AssetSourceFormat.Sanxiu);
        // More jobs than workers keeps the request active until it is canceled.
        for (int i = 1; i < 256; i++) store.Libraries[i] = store.Libraries[0];
        var errors = new System.Collections.Concurrent.ConcurrentQueue<string>();
        store.Error += errors.Enqueue;
        var images = Enumerable.Range(0, 256).Select(i => (i, 0)).ToArray();
        for (int i = 0; i < 5; i++)
        {
            using var cancellation = new CancellationTokenSource();
            var pending = store.PrepareOverviewAsync(images, 4, cancellation.Token);
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
            Assert.True(pending.IsCanceled);
        }
        var current = await store.PrepareOverviewAsync([(0, 0)], 4, CancellationToken.None);
        Assert.Equal(18, Fixture.Pixels(current[(0, 0)].Bitmap)[0]);
        Assert.Empty(errors);
    }

    [Fact]
    public void SanxiuV6GifUsesItsOwnDimensionsWhenMetadataIsEmpty()
    {
        using var files = new TemporaryAssets();
        files.WriteGif("Objects.Lib");

        using var store = AssetStore.Load(files.Root, AssetSourceFormat.Sanxiu);
        var library = store.Libraries[2000];
        Assert.Equal(new SpriteHeader(1, 1, 3, -4), library.ReadHeader(0));
        var sprite = library.Decode(0)!;
        Assert.Equal((1, 1), (sprite.Bitmap.PixelWidth, sprite.Bitmap.PixelHeight));
    }

    [Fact]
    public void SanxiuModeRejectsV2AndProtectedV5()
    {
        using var files = new TemporaryAssets();
        string map = Path.Combine(files.Root, "Map");
        Directory.CreateDirectory(map);
        using (var writer = new BinaryWriter(File.Create(Path.Combine(map, "Tiles.Lib"))))
        { writer.Write(2); writer.Write(0); }
        Assert.Throws<InvalidDataException>(() => AssetStore.Load(files.Root, AssetSourceFormat.Sanxiu));
        using (var defaults = AssetStore.Load(files.Root)) Assert.Equal(0, defaults.Libraries[0].Count);

        files.Write("Tiles.Lib", 5, 12, 34);
        using (var stream = File.OpenWrite(Path.Combine(map, "Tiles.Lib")))
        { stream.Position = 4; stream.WriteByte(1); }
        Assert.Throws<InvalidDataException>(() => AssetStore.Load(files.Root, AssetSourceFormat.Sanxiu));
    }

    [Fact]
    public void SanxiuModeDoesNotDiscoverDirectoryOnlyLibraries()
    {
        using var files = new TemporaryAssets();
        Directory.CreateDirectory(Path.Combine(files.Root, "Map", "Tiles"));
        Assert.Throws<InvalidDataException>(() => AssetStore.Load(files.Root, AssetSourceFormat.Sanxiu));
    }

    [Fact]
    public void SanxiuLibraryLoadsV3()
    {
        using var files = new TemporaryAssets();
        string map = Path.Combine(files.Root, "Map");
        Directory.CreateDirectory(map);
        Fixture.WriteLibrary(Path.Combine(map, "Tiles.Lib"), 2, 2, 21, 42, 63);

        using var store = AssetStore.Load(files.Root, AssetSourceFormat.Sanxiu);
        Assert.Equal(new SpriteHeader(2, 2, 0, 0), store.Libraries[0].ReadHeader(0));
        Assert.Equal(21, Fixture.Pixels(store.Libraries[0].Decode(0)!.Bitmap)[0]);
    }

    [Fact]
    public void SanxiuMapNormalizesLayerLibrariesAndSavesRawIds()
    {
        using var files = new TemporaryAssets();
        Directory.CreateDirectory(files.Root);
        string input = Path.Combine(files.Root, "input.map"), output = Path.Combine(files.Root, "output.map");
        var raw = new MapDocument(2, 2);
        raw.Set(0, 0, new Cell { BackLibrary = 1, BackImage = 1, MiddleLibrary = 0,
            MiddleImage = 1, Foreground = new Front(1, 1) });
        raw.Save(input);

        var document = MapDocument.Load(input, AssetSourceFormat.Sanxiu);
        Assert.Equal(1, document.Get(0, 0).BackLibrary);
        Assert.Equal(1000, document.Get(0, 0).MiddleLibrary);
        Assert.Equal(2001, document.Get(0, 0).Foreground!.Library);
        document.Save(output);
        var saved = MapDocument.Load(output);
        Assert.Equal(0, saved.Get(0, 0).MiddleLibrary);
        Assert.Equal(1, saved.Get(0, 0).Foreground!.Library);
        Assert.Equal(document.Get(0, 0).Foreground, MapDocument.Load(output, AssetSourceFormat.Sanxiu).Get(0, 0).Foreground);
    }

    [AvaloniaFact]
    public void MaterialFormatRadioDefaultsAndRestoresSavedChoice()
    {
        var first = new SettingsWindow(null, AssetSourceFormat.Default, (_, _) => Task.CompletedTask);
        first.Show();
        try
        {
            var options = ((StackPanel)((ScrollViewer)first.Content!).Content!).Children.OfType<RadioButton>()
                .Where(option => option.GroupName == "AssetSourceFormat").ToArray();
            Assert.Equal(2, options.Length);
            Assert.True(options[0].IsChecked);
            Assert.False(options[1].IsChecked);
        }
        finally { first.Close(); }

        var second = new SettingsWindow(null, AssetSourceFormat.Sanxiu, (_, _) => Task.CompletedTask);
        second.Show();
        try
        {
            var options = ((StackPanel)((ScrollViewer)second.Content!).Content!).Children.OfType<RadioButton>()
                .Where(option => option.GroupName == "AssetSourceFormat").ToArray();
            Assert.False(options[0].IsChecked);
            Assert.True(options[1].IsChecked);
        }
        finally { second.Close(); }
    }

    [AvaloniaFact]
    public void MaterialFormatRadioPassesSelectedModeOnSave()
    {
        AssetSourceFormat? selected = null;
        var window = new SettingsWindow(Path.GetTempPath(), AssetSourceFormat.Default,
            (_, format) => { selected = format; return Task.CompletedTask; });
        window.Show();
        try
        {
            var panel = (StackPanel)((ScrollViewer)window.Content!).Content!;
            panel.Children.OfType<RadioButton>().Single(option => option.Name == "SanxiuAssetFormat").IsChecked = true;
            var buttons = panel.Children.OfType<StackPanel>().Last();
            var save = buttons.Children.OfType<Button>().Last();
            save.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal(AssetSourceFormat.Sanxiu, selected);
        }
        finally { window.Close(); }
    }

    [Fact]
    public void OldSettingsDefaultToExistingFormatAndNewChoicePersists()
    {
        using var files = new TemporaryAssets();
        Directory.CreateDirectory(files.Root);
        string file = Path.Combine(files.Root, "settings.json");
        File.WriteAllText(file, "{\"LibraryFolder\":\"C:\\\\Data\",\"Language\":\"zh-CN\"}");
        var store = new SettingsStore(file);
        Assert.Equal(AssetSourceFormat.Default, store.Load().AssetFormat);
        store.Save(store.Load() with { AssetFormat = AssetSourceFormat.Sanxiu });
        Assert.Equal(AssetSourceFormat.Sanxiu, store.Load().AssetFormat);
        Assert.Equal("zh-CN", store.Load().Language);
    }

    private sealed class TemporaryAssets : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "CrystalStudio-asset-format-" + Guid.NewGuid().ToString("N"));

        public void WriteGif(string name)
        {
            byte[] image = Convert.FromHexString("47494638396101000100800000000000FFFFFF21F90401000000002C00000000010001000002024401003B");
            string map = Path.Combine(Root, "Map");
            Directory.CreateDirectory(map);
            using var header = new MemoryStream();
            using (var writer = new BinaryWriter(header, System.Text.Encoding.UTF8, true))
            {
                writer.Write(1);
                writer.Write((short)0); writer.Write((short)0); writer.Write((short)3); writer.Write((short)-4);
                writer.Write((short)0); writer.Write((short)0); writer.Write((byte)0);
                writer.Write((byte)1); writer.Write(image.Length); writer.Write(0);
            }
            byte[] head = header.ToArray();
            using var file = new BinaryWriter(File.Create(Path.Combine(map, name)));
            file.Write(6); file.Write((byte)0); file.Write(head.Length); file.Write(head); file.Write(image);
        }

        public void Write(string name, int version, byte blue, byte green, int size = 2)
        {
            string map = Path.Combine(Root, "Map");
            Directory.CreateDirectory(map);
            byte[] pixels = Enumerable.Range(0, size * size).SelectMany(_ => new byte[] { blue, green, 200, 255 }).ToArray();
            using var compressed = new MemoryStream();
            using (var gzip = new GZipStream(compressed, CompressionLevel.SmallestSize, true)) gzip.Write(pixels);
            using var header = new MemoryStream();
            using (var writer = new BinaryWriter(header, System.Text.Encoding.UTF8, true))
            {
                writer.Write(1);
                writer.Write((short)size); writer.Write((short)size); writer.Write((short)3); writer.Write((short)-4);
                writer.Write((short)0); writer.Write((short)0); writer.Write((byte)0);
                if (version == 6) writer.Write((byte)0);
                writer.Write(checked((int)compressed.Length));
                writer.Write(0);
            }
            byte[] head = header.ToArray();
            string path = Path.Combine(map, name);
            using (var writer = new BinaryWriter(File.Create(path)))
            {
                writer.Write(version); writer.Write((byte)0); writer.Write(head.Length); writer.Write(head);
                writer.Write(compressed.ToArray());
            }
        }

        public void Dispose() { if (Directory.Exists(Root)) Directory.Delete(Root, true); }
    }
}
