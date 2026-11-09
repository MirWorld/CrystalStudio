namespace CrystalStudio;

internal static class ModuleLayoutSnapshot
{
    internal static RasterImage Create(ModuleGenerationPlan plan)
    {
        const int width = 320, height = 200;
        var pixels = new byte[width * height * 4];
        for (int index = 0; index < width * height; index++) pixels[index * 4 + 3] = 255;
        int number = 0;
        foreach (var placement in plan.Placement.Placements)
        {
            int color = number++ % 6;
            foreach (var cell in placement.Module.Cells)
            {
                int column = (int)((long)(placement.X + cell.X) * width / plan.Placement.Source.Width);
                int row = (int)((long)(placement.Y + cell.Y) * height / plan.Placement.Source.Height);
                if (column < 0 || row < 0 || column >= width || row >= height) continue;
                int index = (row * width + column) * 4;
                pixels[index] = (byte)(70 + color * 28); pixels[index + 1] = (byte)(190 - color * 17); pixels[index + 2] = (byte)(100 + (color * 53) % 140);
            }
        }
        return RasterImage.Create(width, height, 96, 96, RasterFormats.Bgra32, null, pixels, width * 4);
    }
}
