namespace CrystalStudio.Tests;

public sealed class CategoryPreviewCancellationTests
{
    [Fact]
    public async Task RepeatedCancellationAllowsTheNextPreviewToRender()
    {
        string root = Path.Combine(Path.GetTempPath(), "CrystalStudio-preview-cancel-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            Fixture.WriteLibrary(Path.Combine(root, "Objects.lib"), 2, 2, 10, 20, 30);
            using var assets = new AssetStore();
            assets.Libraries[2] = new CrystalLibrary(Path.Combine(root, "Objects.lib"));
            string path = Path.Combine(root, "object.X");
            CellCodec.SaveStamp(path, Enumerable.Range(0, 10000)
                .Select(i => new StampCell(i % 100, i / 100, new Cell { Foreground = new(2, 1) })));
            for (int i = 0; i < 10; i++)
            {
                using var stop = new CancellationTokenSource();
                var task = MainWindow.RenderCategoryPreviewAsync(path, assets, stop.Token);
                stop.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
                Assert.True(task.IsCanceled);
            }
            using var preview = await MainWindow.RenderCategoryPreviewAsync(path, assets, default);
            Assert.NotNull(preview);
            Assert.Equal(120, preview.PixelWidth);
            Assert.Equal(84, preview.PixelHeight);
            await Assert.ThrowsAsync<FileNotFoundException>(() =>
                MainWindow.RenderCategoryPreviewAsync(Path.Combine(root, "missing.X"), assets, default));
        }
        finally { Directory.Delete(root, true); }
    }
}
