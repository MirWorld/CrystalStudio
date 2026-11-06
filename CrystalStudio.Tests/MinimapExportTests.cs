using System.IO.Compression;
using Avalonia.Headless.XUnit;
using CrystalStudio.Rendering;

namespace CrystalStudio.Tests;

public sealed class MinimapExportTests
{
    [AvaloniaFact]
    public void PresetsFillEditablePixelDimensionsAndRejectInvalidInput()
    {
        var window = new MinimapExportWindow(new(100, 60));
        foreach (var (preset, width, height) in new[] { (0, 1200, 480), (1, 2400, 960), (2, 4800, 1920) })
        {
            window.Preset.SelectedIndex = preset;
            Assert.Equal(width, window.OutputWidth); Assert.Equal(height, window.OutputHeight);
            Assert.True(window.ExportButton.IsEnabled);
        }
        window.WidthInput.Text = "123"; window.HeightInput.Text = "456";
        Assert.Equal(123, window.OutputWidth); Assert.Equal(456, window.OutputHeight);
        Assert.True(window.ExportButton.IsEnabled);
        foreach (string invalid in new[] { "", "0", "-1", "1.5", "2147483648" })
        {
            window.WidthInput.Text = invalid;
            Assert.False(window.ExportButton.IsEnabled);
        }
        window.WidthInput.Text = "2147483647"; window.HeightInput.Text = "2147483647";
        Assert.False(window.ExportButton.IsEnabled);
        window.Preset.SelectedIndex = 0;
        Assert.True(window.ExportButton.IsEnabled);
        window.Close();
    }

    [AvaloniaFact]
    public async Task ExportedPngUsesRequestedDimensionsAndFullSizeKeepsNativePixelDetail()
    {
        string root = Path.Combine(Path.GetTempPath(), "CrystalStudio-Minimap-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            // Alternating pixels become grey if a quarter-size sprite is enlarged.
            byte[] pixels = new byte[96 * 64 * 4];
            for (int i = 0; i < 96 * 64; i++)
            {
                byte value = (byte)(i % 2 == 0 ? 0 : 255);
                pixels[i * 4] = value; pixels[i * 4 + 1] = value; pixels[i * 4 + 2] = value; pixels[i * 4 + 3] = 255;
            }
            using (var compressed = new MemoryStream())
            {
                using (var gzip = new GZipStream(compressed, CompressionLevel.Fastest, true)) gzip.Write(pixels);
                using var writer = new BinaryWriter(File.Create(Path.Combine(root, "Tiles.lib")));
                writer.Write(3); writer.Write(1); writer.Write(0); writer.Write(16);
                writer.Write((short)96); writer.Write((short)64);
                for (int i = 0; i < 4; i++) writer.Write((short)0);
                writer.Write((byte)0); writer.Write((int)compressed.Length); writer.Write(compressed.ToArray());
            }
            using var assets = AssetStore.Load(root);
            var document = new MapDocument(2, 2); document.Set(0, 0, new Cell { BackImage = 1 });
            foreach (int divisor in new[] { 4, 2, 1 })
            {
                int width = 96 / divisor, height = 64 / divisor;
                string path = Path.Combine(root, $"minimap-{divisor}.png");
                await MinimapExporter.ExportAsync(document, assets, path, width, height);
                using var image = RasterImage.Load(path);
                Assert.Equal((width, height), (image.PixelWidth, image.PixelHeight));
                var actual = Fixture.Pixels(image);
                Assert.All(Enumerable.Range(0, width * height), i => Assert.Equal(255, actual[i * 4 + 3]));
                if (divisor == 1) Assert.Equal(pixels, actual);
            }
            using var custom = await MinimapExporter.CreateAsync(document, assets, 51, 37);
            Assert.Equal((51, 37), (custom.PixelWidth, custom.PixelHeight));
        }
        finally { Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData(0, 100)]
    [InlineData(100, -1)]
    [InlineData(int.MaxValue, int.MaxValue)]
    public async Task InvalidDimensionsFailBeforeArtworkIsLoaded(int width, int height)
    {
        using var assets = new AssetStore();
        await Assert.ThrowsAsync<InvalidOperationException>(() => MinimapExporter.CreateAsync(new(2, 2), assets, width, height));
        Assert.Equal(0, assets.OverviewCached);
    }
}
