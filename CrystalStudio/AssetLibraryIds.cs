namespace CrystalStudio;

internal static class AssetLibraryIds
{
    internal static int Resolve(int library, MapLayer layer, AssetSourceFormat format)
    {
        if (format != AssetSourceFormat.Sanxiu) return library;

        return layer switch
        {
            MapLayer.Ground when library is >= 100 and <= 109 => library - 100,
            MapLayer.Ground => library,
            MapLayer.Middle when library is >= 1000 and < 2000 => library,
            MapLayer.Middle when library is 0 or 1 => 1000,
            MapLayer.Middle when library is >= 110 and <= 119 => 1000 + library - 110,
            MapLayer.Middle => library,
            MapLayer.Objects when library is >= 2000 and < 3000 => library,
            MapLayer.Objects when library is >= 2 and <= 99 => 2000 + library - 2,
            MapLayer.Objects when library is >= 120 and <= 150 => 2000 + library - 120,
            MapLayer.Objects when library is 0 or 1 => 2000 + library,
            _ => library
        };
    }

    internal static void NormalizeStamp(Cell cell, AssetSourceFormat format)
    {
        if (format != AssetSourceFormat.Sanxiu) return;
        if ((cell.BackImage & 0x1fffffff) != 0)
            cell.BackLibrary = checked((short)Resolve(cell.BackLibrary, MapLayer.Ground, format));
        if (cell.MiddleImage != 0)
            cell.MiddleLibrary = checked((short)Resolve(cell.MiddleLibrary, MapLayer.Middle, format));
        if (cell.Foreground is { } front && (front.Image & 0x7fff) != 0)
            cell.Foreground = front with { Library = checked((short)Resolve(front.Library, MapLayer.Objects, format)) };
    }

    internal static void NormalizeMap(Cell cell, AssetSourceFormat format)
    {
        if (format != AssetSourceFormat.Sanxiu) return;
        if (cell.MiddleImage != 0 && cell.MiddleLibrary is >= 0 and < 1000)
            cell.MiddleLibrary = checked((short)(cell.MiddleLibrary + 1000));
        if (cell.Foreground is { Library: >= 0 and < 2000 } front && (front.Image & 0x7fff) != 0)
            cell.Foreground = front with { Library = checked((short)(front.Library + 2000)) };
    }
}
