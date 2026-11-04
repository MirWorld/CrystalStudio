namespace CrystalStudio;

internal static class OverviewPixels
{
    // Average premultiplied colour, then return straight BGRA for the atlas.
    // Transparent RGB must not darken the edges of trees and other cutouts.
    internal static byte[] Reduce(byte[] input, int width, int height, int targetWidth, int targetHeight, CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        var output = new byte[targetWidth * targetHeight * 4];
        for (int y = 0; y < targetHeight; y++)
        {
            cancellation.ThrowIfCancellationRequested();
            int top = y * height / targetHeight, bottom = (y + 1) * height / targetHeight;
            for (int x = 0; x < targetWidth; x++)
            {
                int left = x * width / targetWidth, right = (x + 1) * width / targetWidth;
                long blue = 0, green = 0, red = 0, alpha = 0;
                for (int sy = top; sy < bottom; sy++)
                {
                    if ((sy & 63) == 0) cancellation.ThrowIfCancellationRequested();
                    for (int sx = left; sx < right; sx++)
                    {
                        int offset = (sy * width + sx) * 4;
                        int a = input[offset + 3];
                        blue += input[offset] * a; green += input[offset + 1] * a;
                        red += input[offset + 2] * a; alpha += a;
                    }
                }
                int destination = (y * targetWidth + x) * 4;
                int count = (right - left) * (bottom - top);
                if (alpha > 0)
                {
                    output[destination] = (byte)((blue + alpha / 2) / alpha);
                    output[destination + 1] = (byte)((green + alpha / 2) / alpha);
                    output[destination + 2] = (byte)((red + alpha / 2) / alpha);
                }
                output[destination + 3] = (byte)((alpha + count / 2) / count);
            }
        }
        return output;
    }
}
