namespace CrystalStudio;

internal static class MapLimits
{
    internal const int MaxCells = 16_000_000;
    internal static bool CanRead(int width, int height) => width > 0 && height > 0 &&
        width <= short.MaxValue && height <= short.MaxValue && (long)width * height <= MaxCells;
    internal static bool CanCreate(int width, int height) => width is >= 16 and <= 4096 &&
        height is >= 16 and <= 4096 && CanRead(width, height);
}
