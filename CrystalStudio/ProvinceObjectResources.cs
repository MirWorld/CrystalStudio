using System.IO;

namespace CrystalStudio;

// Both province scene themes require complete, reviewed, visibly decoded objects.
internal static class ProvinceObjectResources
{
    internal static IReadOnlyList<(string Role, BrushTree Tree)> Load(
        (string Role, int[] Ids)[] groups, AssetStore? assets, CancellationToken token)
    {
        var result = new List<(string, BrushTree)>();
        foreach (var (role, ids) in groups)
        foreach (int id in ids)
        {
            token.ThrowIfCancellationRequested();
            string name = $"mir/mir_{id:00000}";
            if (ProvinceSceneryPalette.GroundSetId(name) != null) continue;
            try
            {
                string path = DataPaths.ResolveEditor("Objects", "mir", $"mir_{id:00000}.X");
                var tree = BrushTree.FromCells(name, path, CellCodec.LoadStamp(path, assets?.Format ?? AssetSourceFormat.Default), assets);
                // Some catalogue objects decode successfully but contain only
                // transparent cards. They cannot stand in for a visible scene object.
                bool visible = assets == null;
                if (assets != null)
                foreach (var front in tree.Cells.SelectMany(c => c.Cell.ForegroundImages).Distinct())
                {
                    var sprite = assets.Libraries[front.Library].Decode((front.Image & 0x7fff) - 1)
                        ?? throw new InvalidDataException("Missing province scene sprite.");
                    byte[] pixels = new byte[sprite.Width * sprite.Height * 4];
                    sprite.Bitmap.CopyPixels(pixels, sprite.Width * 4, 0);
                    for (int i = 3; i < pixels.Length; i += 4) if (pixels[i] != 0) { visible = true; break; }
                }
                if (visible) result.Add((role, tree));
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException)
            {
                StudioLog.Warning("Province.Objects", $"Skipping unavailable object; Name={name}", ex);
            }
        }
        return result;
    }
}
