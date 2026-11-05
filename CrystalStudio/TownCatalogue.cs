using System.IO;
using System.Text.Json;

namespace CrystalStudio;

public sealed record TownBounds(int X, int Y, int Width, int Height);
public sealed record TownSource(int Id, string FileName, string Title, string Sha256, List<int> StudyArea);
public sealed record TownSpriteProof(int Library, int Image, string PixelSha256);
public sealed record TownPoint(int X, int Y);
public sealed record TownPiece(string Id, string Name, string Role, string? Direction, string SourceSha256,
    TownBounds Bounds, List<StampCell> Cells, List<TownPoint> Footprint, List<TownPoint> NativeAnchors, bool IsTree);
public sealed record TownFamily(string Id, string Name, TownSource SourceMap, double MedianHouseAnchorDistance,
    string ReviewBasis, string BaseTerrainId, string PathTerrainId, List<TownPiece> Pieces, List<TownSpriteProof> SpriteProofs)
{
    public override string ToString() => Name;
}
public sealed record TownCatalogue(int SchemaVersion, List<TownFamily> Families)
{
    public static TownCatalogue Load(string? path = null)
    {
        var catalogue = StudioJson.Deserialize<TownCatalogue>(File.ReadAllText(path ?? DataPaths.ResolveEditor("catalogue", "towns.json")),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? throw new InvalidDataException("Empty town catalogue.");
        if (catalogue.SchemaVersion != 1 || catalogue.Families.Count == 0 || catalogue.Families.Select(f => f.Id).Distinct().Count() != catalogue.Families.Count)
            throw new InvalidDataException("Invalid town catalogue.");
        foreach (var family in catalogue.Families)
        {
            if (family.SourceMap.Sha256.Length != 64 || !double.IsFinite(family.MedianHouseAnchorDistance) || family.Pieces.Count > 500 ||
                !family.Pieces.Any(p => p.Role == "house") || family.Pieces.Select(p => p.Id).Distinct().Count() != family.Pieces.Count)
                throw new InvalidDataException("Invalid town family.");
            foreach (var piece in family.Pieces)
                if (piece.Role is not ("house" or "market" or "landmark" or "greenery" or "prop") ||
                    piece.Role is "house" or "market" or "landmark" && piece.Direction is not ("SE" or "SW") ||
                    piece.Bounds.Width is < 1 or > 80 || piece.Bounds.Height is < 1 or > 80 || piece.Cells.Count is < 1 or > 10000 || piece.Footprint.Count is < 1 or > 10000 ||
                    piece.Cells.Select(c => (c.X, c.Y)).Distinct().Count() != piece.Cells.Count ||
                    piece.Cells.Any(c => c.X < piece.Bounds.X || c.Y < piece.Bounds.Y || c.X >= piece.Bounds.X + piece.Bounds.Width || c.Y >= piece.Bounds.Y + piece.Bounds.Height ||
                        (c.Cell.BackImage & ~0x60000000) != 0 || c.Cell.MiddleImage != 0 ))
                    throw new InvalidDataException("Invalid town piece: " + piece.Id);
        }
        return catalogue;
    }

    // 允许替换素材；房屋引用的库和图片编号仍须可读取。
    public static TownFamily Prepare(TownFamily family, AssetStore assets, Dictionary<(int Library, int Image), Sprite> sprites, out int skipped,
        CancellationToken cancellationToken = default)
    {
        var valid = new HashSet<(int, int)>();
        foreach (var proof in family.SpriteProofs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var key = (proof.Library, proof.Image);
            if (!sprites.TryGetValue(key, out var sprite) && assets.Libraries.TryGetValue(proof.Library, out var library))
            {
                sprite = library.Decode(proof.Image - 1);
                if (sprite != null) sprites[key] = sprite;
            }
            if (sprite == null) continue;
            valid.Add(key);
        }
        var available = family.Pieces.Where(p => p.Cells.SelectMany(c => c.Cell.ForegroundImages).All(f => valid.Contains((f.Library, f.Image & 0x7FFF)))).ToList();
        skipped = family.Pieces.Count - available.Count;
        if (!available.Any(p => p.Role == "house" && p.Direction == "SE") || !available.Any(p => p.Role == "house" && p.Direction == "SW"))
            throw new InvalidDataException(LocalText.Format("{0} is missing readable SE or SW house images. Check the library files and image indices.", family.Name));
        return family with { Pieces = available };
    }
}
