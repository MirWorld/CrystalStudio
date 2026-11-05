namespace CrystalStudio;

public enum TilePatternLayout { None, Mir2, Mir3, Mir3Mini, Small }
public sealed class AutoTileBrush
{
    // Pattern classification and atlas offsets are adapted from
    // the reference MapEditor Main.cs auto-tile methods.
    private readonly MapDocument Document;
    private readonly EditHistory history;
    private readonly Func<int, int, Cell> readCell;
    private readonly Action<int, int> invalidate;
    private readonly MapLayer layer;
    private readonly int library, spriteCount, selectTilesIndex;
    private readonly Random random;
    private const int Mir2BigTileBlock = 50, Mir3BigTileBlock = 30, smTileBlock = 60;
    private readonly TilePatternLayout layout;
    public AutoTileBrush(MapDocument document, EditHistory edits, MapLayer layer, int library, int spriteIndex, int spriteCount, TilePatternLayout layout, Action<int, int> invalidate, int? seed = null, Func<int, int, Cell>? readCell = null) { random = seed.HasValue ? new Random(seed.Value) : new Random(); Document = document; this.readCell = readCell ?? document.Get; history = edits; this.layer = layer; this.library = library; this.spriteCount = spriteCount; this.layout = layout; this.invalidate = invalidate; selectTilesIndex = spriteIndex / BlockSize(layout); if (layout == TilePatternLayout.Mir3 && spriteIndex % 30 is >= 5 and <= 9) selectTilesIndex += (spriteCount + (spriteCount % 10 == 0 ? 0 : 1)) / 30; }
    public static int BlockSize(TilePatternLayout layout) => layout switch { TilePatternLayout.Mir2 => 50, TilePatternLayout.Mir3 => 30, TilePatternLayout.Mir3Mini => 5, TilePatternLayout.Small => 60, _ => 1 };
    public static TilePatternLayout Layout(int library, MapLayer layer, AssetSourceFormat format = AssetSourceFormat.Default) =>
        format == AssetSourceFormat.Sanxiu ? layer switch
        {
            MapLayer.Ground when library is >= 0 and < 1000 => TilePatternLayout.Mir2,
            MapLayer.Middle when library is >= 1000 and < 2000 => TilePatternLayout.Small,
            _ => TilePatternLayout.None
        } : layer switch
    {
        MapLayer.Ground when library == 0 || library is >= 100 and <= 109 => TilePatternLayout.Mir2,
        MapLayer.Ground when library >= 200 && ((library % 100) % 15) == 1 => TilePatternLayout.Mir3,
        MapLayer.Ground when library >= 200 && ((library % 100) % 15) == 2 => TilePatternLayout.Mir3Mini,
        MapLayer.Middle when library == 1 || library is >= 110 and <= 119 => TilePatternLayout.Small,
        _ => TilePatternLayout.None
    };
    public void Paint(int x, int y, int size)
    {
        int step = layer == MapLayer.Ground ? 2 : 1;
        if (step == 2) { x &= ~1; y &= ~1; }
        int radius = (size - 3) / 2;
        if (layout == TilePatternLayout.Mir3Mini)
        {
            for (int dy = -radius - 1; dy <= radius + 1; dy++)
                for (int dx = -radius - 1; dx <= radius + 1; dx++) Put(x + dx * step, y + dy * step, Center());
            return;
        }

        // Each atlas shape describes terrain coverage at four shared vertices.
        // Read the original layout before writing, so joins do not depend on scan order.
        var vertices = new Dictionary<(int X, int Y), bool>();
        var pending = new Queue<(int X, int Y)>();
        var queued = new HashSet<(int X, int Y)>();
        var result = new Dictionary<(int X, int Y), TileType>();
        void Enqueue(int tx, int ty)
        {
            if (Document.Contains(tx, ty) && queued.Add((tx, ty))) pending.Enqueue((tx, ty));
        }
        bool Covered(int vx, int vy)
        {
            if (vertices.TryGetValue((vx, vy), out bool value)) return value;
            value = (Coverage(TypeAt(vx, vy)) & 1) != 0 ||
                (Coverage(TypeAt(vx-step, vy)) & 2) != 0 ||
                (Coverage(TypeAt(vx, vy-step)) & 4) != 0 ||
                (Coverage(TypeAt(vx-step, vy-step)) & 8) != 0;
            vertices[(vx, vy)] = value;
            return value;
        }
        void Cover(int vx, int vy)
        {
            if (Covered(vx, vy)) return;
            vertices[(vx, vy)] = true;
            Enqueue(vx, vy); Enqueue(vx-step, vy); Enqueue(vx, vy-step); Enqueue(vx-step, vy-step);
        }
        for (int dy = -radius; dy <= radius; dy++)
            for (int dx = -radius; dx <= radius; dx++)
            {
                int tx = x + dx * step, ty = y + dy * step;
                if (!Document.Contains(tx, ty)) continue;
                Cover(tx, ty); Cover(tx+step, ty); Cover(tx, ty+step); Cover(tx+step, ty+step);
                for (int oy = -1; oy <= 1; oy++) for (int ox = -1; ox <= 1; ox++) Enqueue(tx+ox*step, ty+oy*step);
            }
        while (pending.TryDequeue(out var tile))
        {
            queued.Remove(tile);
            int mask = (Covered(tile.X, tile.Y) ? 1 : 0) | (Covered(tile.X+step, tile.Y) ? 2 : 0) |
                (Covered(tile.X, tile.Y+step) ? 4 : 0) | (Covered(tile.X+step, tile.Y+step) ? 8 : 0);
            // The atlas has no disconnected diagonal shape. Join the two islands,
            // then resolve the neighbouring tiles from the same updated vertices.
            if (mask is 6 or 9)
            {
                Cover(tile.X, tile.Y); Cover(tile.X+step, tile.Y);
                Cover(tile.X, tile.Y+step); Cover(tile.X+step, tile.Y+step);
                mask = 15;
            }
            result[tile] = Shape(mask);
        }
        foreach (var pair in result.OrderBy(p => p.Key.Y).ThenBy(p => p.Key.X))
        {
            var tile = pair.Key; var shape = pair.Value;
            if (shape == TileType.None || TypeAt(tile.X, tile.Y) == shape) continue;
            int image = layout switch { TilePatternLayout.Mir2 => RandomAutoMir2Tile(shape), TilePatternLayout.Mir3 => RandomAutoMir3Tile(shape), _ => RandomAutoSmTile(shape) };
            Put(tile.X, tile.Y, image);
        }
    }
    private TileType TypeAt(int x, int y) => layout switch { TilePatternLayout.Mir2 => GetAutoMir2TileType(x,y), TilePatternLayout.Mir3 => GetAutoMir3TileType(x,y), _ => GetAutoSmTileType(x,y) };
    private static int Coverage(TileType type) => type switch
    {
        TileType.Center => 15, TileType.Up => 12, TileType.Right => 5, TileType.Down => 3, TileType.Left => 10,
        TileType.UpLeft => 8, TileType.UpRight => 4, TileType.DownLeft => 2, TileType.DownRight => 1,
        TileType.InUpLeft => 14, TileType.InUpRight => 13, TileType.InDownLeft => 11, TileType.InDownRight => 7, _ => 0
    };
    private static TileType Shape(int mask) => mask switch
    {
        15 => TileType.Center, 12 => TileType.Up, 5 => TileType.Right, 3 => TileType.Down, 10 => TileType.Left,
        8 => TileType.UpLeft, 4 => TileType.UpRight, 2 => TileType.DownLeft, 1 => TileType.DownRight,
        14 => TileType.InUpLeft, 13 => TileType.InUpRight, 11 => TileType.InDownLeft, 7 => TileType.InDownRight, _ => TileType.None
    };
    private int Center() => layout switch { TilePatternLayout.Mir2 => RandomAutoMir2Tile(TileType.Center), TilePatternLayout.Mir3 => RandomAutoMir3Tile(TileType.Center), TilePatternLayout.Small => RandomAutoSmTile(TileType.Center), _ => selectTilesIndex * 5 + random.Next(5) };
    private int GetTile(int x, int y) { if (!Document.Contains(x, y)) return -1; var c = readCell(x, y); return c.BackLibrary == library ? (c.BackImage & 0x1FFFFFFF) - 1 : -1; }
    private int GetSmTile(int x, int y) { if (!Document.Contains(x, y)) return -1; var c = readCell(x, y); return c.MiddleLibrary == library ? c.MiddleImage - 1 : -1; }
    private void Put(int x, int y, int image) { if (!Document.Contains(x, y) || image < 0 || image >= spriteCount) return; var original = readCell(x, y); int previous = layer == MapLayer.Ground ? (original.BackImage & 0x1FFFFFFF) : original.MiddleImage; int oldLibrary = layer == MapLayer.Ground ? original.BackLibrary : original.MiddleLibrary; if (previous == image + 1 && oldLibrary == library) return; history.Record(Document, x, y); var c = original.Copy(); if (layer == MapLayer.Ground) { c.BackLibrary = (short)library; c.BackImage = (c.BackImage & unchecked((int)0xE0000000)) | (image + 1); } else { c.MiddleLibrary = (short)library; c.MiddleImage = checked((ushort)(image + 1)); c.MiddleFrames = 0; c.MiddleTick = 0; } Document.Set(x, y, c); invalidate(x, y); }
    private enum TileType { None = -1, Center, Up, UpRight, Right, DownRight, Down, DownLeft, Left, UpLeft, InUpRight, InDownRight, InDownLeft, InUpLeft }
    private TileType GetAutoMir2TileType(int x, int y)
    {
        var imageIndex = GetTile(x, y);
        if (imageIndex / Mir2BigTileBlock != selectTilesIndex)
        {
            return TileType.None;
        }
        switch (imageIndex % Mir2BigTileBlock)
        {
            case 0:
            case 1:
            case 2:
            case 3:
            case 4:
                return TileType.Center;
            case 5:
                return TileType.UpLeft;
            case 6:
                return TileType.UpRight;
            case 7:
                return TileType.DownLeft;
            case 8:
                return TileType.DownRight;
            case 10:
                return TileType.InUpLeft;
            case 11:
                return TileType.InUpRight;
            case 12:
                return TileType.InDownLeft;
            case 13:
                return TileType.InDownRight;
            case 15:
            case 16:
                return TileType.Up;
            case 17:
            case 18:
                return TileType.Down;
            case 20:
            case 22:
                return TileType.Left;
            case 21:
            case 23:
                return TileType.Right;
        }
        return TileType.None;
    }

    private TileType GetAutoMir3TileType(int x, int y)
    {
        var imageIndex = GetTile(x, y);

        int flag;
        if (spriteCount % 10 != 0)
        {
            flag = (spriteCount + 1) / Mir3BigTileBlock;
        }
        else
        {
            flag = spriteCount / Mir3BigTileBlock;
        }

        if ((imageIndex / Mir3BigTileBlock != selectTilesIndex) &&
            imageIndex / Mir3BigTileBlock != selectTilesIndex - flag)
        {
            return TileType.None;
        }
        if (selectTilesIndex < flag)
        {
            switch (imageIndex % Mir3BigTileBlock)
            {
                case 0:
                case 1:
                case 2:
                case 3:
                case 4:
                    return TileType.Center;
                case 10:
                    return TileType.UpLeft;
                case 11:
                    return TileType.UpRight;
                case 12:
                    return TileType.DownLeft;
                case 13:
                    return TileType.DownRight;
                case 15:
                    return TileType.InUpLeft;
                case 16:
                    return TileType.InUpRight;
                case 17:
                    return TileType.InDownLeft;
                case 18:
                    return TileType.InDownRight;
                case 20:
                case 21:
                    return TileType.Up;
                case 22:
                case 23:
                    return TileType.Down;
                case 25:
                case 27:
                    return TileType.Left;
                case 26:
                case 28:
                    return TileType.Right;
            }
        }
        else
        {
            switch (imageIndex % Mir3BigTileBlock)
            {
                case 5:
                case 6:
                case 7:
                case 8:
                case 9:
                    return TileType.Center;
                case 18:
                    return TileType.UpLeft;
                case 17:
                    return TileType.UpRight;
                case 16:
                    return TileType.DownLeft;
                case 15:
                    return TileType.DownRight;
                case 13:
                    return TileType.InUpLeft;
                case 12:
                    return TileType.InUpRight;
                case 11:
                    return TileType.InDownLeft;
                case 10:
                    return TileType.InDownRight;
                case 22:
                case 23:
                    return TileType.Up;
                case 20:
                case 21:
                    return TileType.Down;
                case 26:
                case 28:
                    return TileType.Left;
                case 25:
                case 27:
                    return TileType.Right;
            }
        }
        return TileType.None;
    }

    private TileType GetAutoSmTileType(int iX, int iY)
    {

        int iImageIndex;

        iImageIndex = GetSmTile(iX, iY);

        if (iImageIndex >= selectTilesIndex * smTileBlock && iImageIndex < (selectTilesIndex + 1) * smTileBlock)
        {

            iImageIndex -= selectTilesIndex * smTileBlock;
            if (iImageIndex < 8)
            {
                return 0;
            }
            return (TileType)((iImageIndex - 8) / 4 + 1);
        }


        return TileType.None;
    }

    private int RandomAutoMir2Tile(TileType tileType)
    {
        if (selectTilesIndex < 0) return -1;

        switch (tileType)
        {
            case TileType.Center:
                return selectTilesIndex * Mir2BigTileBlock + random.Next(5);
            case TileType.Up:
                return selectTilesIndex * Mir2BigTileBlock + random.Next(15, 17);
            case TileType.Down:
                return selectTilesIndex * Mir2BigTileBlock + random.Next(17, 19);
            case TileType.Left:
                if (random.Next(2) == 0)
                {
                    return selectTilesIndex * Mir2BigTileBlock + 20;
                }
                return selectTilesIndex * Mir2BigTileBlock + 22;
            case TileType.Right:
                if (random.Next(2) == 0)
                {
                    return selectTilesIndex * Mir2BigTileBlock + 21;
                }
                return selectTilesIndex * Mir2BigTileBlock + 23;
            case TileType.UpLeft:
                return selectTilesIndex * Mir2BigTileBlock + 5;

            case TileType.UpRight:
                return selectTilesIndex * Mir2BigTileBlock + 6;

            case TileType.DownLeft:
                return selectTilesIndex * Mir2BigTileBlock + 7;
            case TileType.DownRight:
                return selectTilesIndex * Mir2BigTileBlock + 8;

            case TileType.InUpLeft:
                return selectTilesIndex * Mir2BigTileBlock + 10;
            case TileType.InUpRight:
                return selectTilesIndex * Mir2BigTileBlock + 11;
            case TileType.InDownLeft:
                return selectTilesIndex * Mir2BigTileBlock + 12;
            case TileType.InDownRight:
                return selectTilesIndex * Mir2BigTileBlock + 13;
        }
        return -1;
    }

    private int RandomAutoMir3Tile(TileType tileType)
    {
        if (selectTilesIndex < 0) return -1;
        int flag;
        if (spriteCount % 10 != 0)
        {
            flag = (spriteCount + 1) / Mir3BigTileBlock;
        }
        else
        {
            flag = spriteCount / Mir3BigTileBlock;
        }

        if (selectTilesIndex < flag)
        {
            switch (tileType)
            {
                case TileType.Center:
                    return selectTilesIndex * Mir3BigTileBlock + random.Next(5);
                case TileType.Up:
                    return selectTilesIndex * Mir3BigTileBlock + random.Next(20, 22);
                case TileType.Down:
                    return selectTilesIndex * Mir3BigTileBlock + random.Next(22, 24);
                case TileType.Left:
                    if (random.Next(2) == 0)
                    {
                        return selectTilesIndex * Mir3BigTileBlock + 25;
                    }
                    return selectTilesIndex * Mir3BigTileBlock + 27;
                case TileType.Right:
                    if (random.Next(2) == 0)
                    {
                        return selectTilesIndex * Mir3BigTileBlock + 26;
                    }
                    return selectTilesIndex * Mir3BigTileBlock + 28;
                case TileType.UpLeft:
                    return selectTilesIndex * Mir3BigTileBlock + 10;

                case TileType.UpRight:
                    return selectTilesIndex * Mir3BigTileBlock + 11;

                case TileType.DownLeft:
                    return selectTilesIndex * Mir3BigTileBlock + 12;
                case TileType.DownRight:
                    return selectTilesIndex * Mir3BigTileBlock + 13;

                case TileType.InUpLeft:
                    return selectTilesIndex * Mir3BigTileBlock + 15;
                case TileType.InUpRight:
                    return selectTilesIndex * Mir3BigTileBlock + 16;
                case TileType.InDownLeft:
                    return selectTilesIndex * Mir3BigTileBlock + 17;
                case TileType.InDownRight:
                    return selectTilesIndex * Mir3BigTileBlock + 18;
            }
        }
        else
        {
            switch (tileType)
            {
                case TileType.Center:
                    return (selectTilesIndex - flag) * Mir3BigTileBlock + random.Next(5, 10);
                case TileType.Up:
                    return (selectTilesIndex - flag) * Mir3BigTileBlock + random.Next(22, 24);
                case TileType.Down:
                    return (selectTilesIndex - flag) * Mir3BigTileBlock + random.Next(20, 22);
                case TileType.Left:
                    if (random.Next(2) == 0)
                    {
                        return (selectTilesIndex - flag) * Mir3BigTileBlock + 26;
                    }
                    return (selectTilesIndex - flag) * Mir3BigTileBlock + 28;
                case TileType.Right:
                    if (random.Next(2) == 0)
                    {
                        return (selectTilesIndex - flag) * Mir3BigTileBlock + 25;
                    }
                    return (selectTilesIndex - flag) * Mir3BigTileBlock + 27;
                case TileType.UpLeft:
                    return (selectTilesIndex - flag) * Mir3BigTileBlock + 18;

                case TileType.UpRight:
                    return (selectTilesIndex - flag) * Mir3BigTileBlock + 17;

                case TileType.DownLeft:
                    return (selectTilesIndex - flag) * Mir3BigTileBlock + 16;
                case TileType.DownRight:
                    return (selectTilesIndex - flag) * Mir3BigTileBlock + 15;

                case TileType.InUpLeft:
                    return (selectTilesIndex - flag) * Mir3BigTileBlock + 13;
                case TileType.InUpRight:
                    return (selectTilesIndex - flag) * Mir3BigTileBlock + 12;
                case TileType.InDownLeft:
                    return (selectTilesIndex - flag) * Mir3BigTileBlock + 11;
                case TileType.InDownRight:
                    return (selectTilesIndex - flag) * Mir3BigTileBlock + 10;
            }
        }

        return -1;
    }

    private int RandomAutoSmTile(TileType iTileType)
    {
        if ((int)iTileType >= 1)
        {
            return selectTilesIndex * smTileBlock + 8 + ((int)iTileType - 1) * 4 + random.Next(4);
        }
        return selectTilesIndex * smTileBlock + random.Next(8);
    }
}


