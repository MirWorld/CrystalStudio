namespace CrystalStudio;

public enum MapCreationKind { Blank, Terrain, Province, Dungeon }

internal static class MapDimensions
{
    public const long MaximumCells = 16_000_000;
    public const long MaximumGeneratedCells = 1_000_000;
    public const long MaximumGroundTiles = 300_000;

    public static bool CanRoundTrip(int width, int height) => width is > 0 and <= short.MaxValue &&
        height is > 0 and <= short.MaxValue && (long)width * height <= MaximumCells;

    public static void Validate(int width, int height)
    {
        if (!CanRoundTrip(width, height))
            throw new ArgumentOutOfRangeException(nameof(width), "Invalid or excessive map dimensions.");
    }

    public static string? CreationError(int width, int height, MapCreationKind kind)
    {
        if (!Enum.IsDefined(kind)) return "Choose a map type.";
        if (width is < 16 or > 4096 || height is < 16 or > 4096)
            return "Enter dimensions between 16 and 4096.";
        if (!CanRoundTrip(width, height)) return "A map supports at most 16,000,000 cells.";
        if (kind == MapCreationKind.Province && (width < 128 || height < 128 || (long)width * height > MaximumGeneratedCells))
            return "Province generation needs at least 128×128 cells and supports up to 1,000,000 cells.";
        if (kind == MapCreationKind.Dungeon && (width < 64 || height < 64 || (long)width * height > MaximumGeneratedCells))
            return "Dungeon generation needs at least 64×64 cells and supports up to 1,000,000 cells.";
        if (kind == MapCreationKind.Terrain && (long)((width + 1) / 2) * ((height + 1) / 2) > MaximumGroundTiles)
            return "Generate at most 300,000 ground tiles at once. Choose a smaller map.";
        return null;
    }
}
