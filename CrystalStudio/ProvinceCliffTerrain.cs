namespace CrystalStudio;

/// <summary>Fit province ground to the two visible sides of native cliff artwork.</summary>
public static class ProvinceCliffTerrain
{
    public static string PlateauId(string familyId) => familyId switch
    {
        "bichon-province" => "0:0:1850",
        "mongchon-province" => "0:0:350",
        "canyon" => "0:0:2300",
        _ => throw new ArgumentException("Unsupported province cliff family.", nameof(familyId))
    };

    public static void Apply(MapDocument map, TerrainPlan ground, TerrainCatalogue terrain, WallFamily family,
        int seed, CancellationToken cancellation = default, ProvinceStyle? style = null)
    {
        var nodes = map.PlacedStamps.Where(s => s.Wall?.FamilyId == family.Id).Select(s => (s.X, s.Y)).ToHashSet();
        var rearCorners = map.PlacedStamps.Where(s => s.Wall?.FamilyId == family.Id && s.Wall.Mask == 6)
            .Select(s => (s.X, s.Y)).ToHashSet();
        if (nodes.Count == 0) return;
        var basis = terrain.Sets.Single(s => s.Id == (style?.BaseId ?? "0:0:0"));
        var overlay = terrain.Sets.Single(s => s.Id == (style?.PatchId ?? "0:0:950"));
        var rock = terrain.Sets.Single(s => s.Id == PlateauId(family.Id));
        bool canyon = family.Id == "canyon";
        int columns = (map.Width + 1) / 2, rows = (map.Height + 1) / 2, stride = columns + 1;
        var cover = new bool[stride * (rows + 1)];
        var plateau = new bool[columns * rows];
        var untouched = new bool[columns * rows];
        int[] Corners(int x, int y) => [y * stride + x, y * stride + x + 1, (y + 1) * stride + x, (y + 1) * stride + x + 1];
        foreach (var tile in ground.Tiles.Where(t => t.SetId == overlay.Id))
        {
            var corners = Corners(tile.X / 2, tile.Y / 2);
            for (int k = 0; k < 4; k++) if ((tile.Mask & (1 << k)) != 0) cover[corners[k]] = true;
        }
        // Preserve all cells of native coast objects, including their ground
        // sprites which cover four map cells from each even anchor.
        foreach (var coast in ground.CoastObjects)
        foreach (var cell in coast.Layout.Cells)
        {
            int x = (coast.X + cell.X) / 2, y = (coast.Y + cell.Y) / 2;
            if (x >= 0 && y >= 0 && x < columns && y < rows) untouched[y * columns + x] = true;
        }
        // The foot of the tall front face and the thin rear ridge have
        // different heights relative to their brush lattice. A union starting
        // at zero painted rock below the front feet, yet stopped below the
        // rear ridge. Move both ends up independently.
        var (frontRise, rearRise) = family.Id switch
        {
            "bichon-province" => (6, 20),
            "mongchon-province" => (6, 24),
            "canyon" => (2, 2),
            _ => throw new ArgumentException("Unsupported province cliff family.", nameof(family))
        };
        // Adjacent tiles and successive rises ask about the same even anchor
        // many times. Memoize lattice membership for this immutable cliff layout.
        int solidStride = columns + 3;
        var solid = new byte[solidStride * (rows + rearRise / 2 + 1)];
        bool Solid(int x, int y)
        {
            int key = y / 2 * solidStride + x / 2 + 2;
            if (solid[key] == 0) solid[key] = nodes.Contains(family.Snap(x, y)) ? (byte)2 : (byte)1;
            return solid[key] == 2;
        }
        for (int y = 0; y < rows; y++)
        {
            cancellation.ThrowIfCancellationRequested();
            for (int x = 0; x < columns; x++)
            {
                if (untouched[y * columns + x]) continue;
                for (int rise = frontRise; rise <= rearRise; rise += 2)
                {
                    // Bichon's two rear runs have different native phases.
                    // Only the eastward-facing slope needs the final two rows;
                    // extending the westward slope equally exposes tile steps.
                    if (family.Id == "bichon-province" && rise > 16 && !Solid(x * 2 - 4, y * 2 + rise)) continue;
                    // The convex rear cap extends one extra row beyond its runs.
                    if (family.Id == "bichon-province" && rise > 18 &&
                        !rearCorners.Contains(family.Snap(x * 2, y * 2 + rise))) continue;
                    if (Solid(x * 2, y * 2 + rise) && Solid(x * 2 + 2, y * 2 + rise) &&
                        Solid(x * 2, y * 2 + rise + 2) && Solid(x * 2 + 2, y * 2 + rise + 2))
                    { plateau[y * columns + x] = true; break; }
                }
            }
        }
        // Native shoulder cards cover tile-sized notches at the ridge. Close
        // those single-tile holes and trim lone protrusions; real cliff bays
        // span whole brush sections and are much wider than one ground tile.
        var fitted = (bool[])plateau.Clone();
        for (int y = 1; y < rows - 1; y++)
        for (int x = 1; x < columns - 1; x++)
        {
            int key = y * columns + x;
            if (untouched[key]) continue;
            int neighbours = (plateau[key - 1] ? 1 : 0) + (plateau[key + 1] ? 1 : 0) +
                (plateau[key - columns] ? 1 : 0) + (plateau[key + columns] ? 1 : 0);
            if (neighbours >= 3) fitted[key] = true;
            else if (neighbours <= 1) fitted[key] = false;
        }
        plateau = fitted;
        if (family.Id == "mongchon-province") FitMongchonRear(map, plateau, untouched, columns, rows);
        // Fit an apron between the cliff feet and the surrounding terrain. Adjust
        // shared overlay vertices, then use the same reviewed terrain
        // corners as terrain generation. Mongchon exposes coarse sand at the feet.
        var protectedVertices = new bool[cover.Length];
        for (int y = 0; y < rows; y++)
        for (int x = 0; x < columns; x++)
            if (untouched[y * columns + x]) foreach (int key in Corners(x, y)) protectedVertices[key] = true;
        var original = (bool[])cover.Clone();
        var surface = new bool[cover.Length];
        if (canyon)
        {
            // Canyon cards contain pale sand at BOTH edges. Carry that native
            // sand over the plateau and under every foreground card, then use
            // its reviewed coarse-sand transition outside the cliff footprint.
            void SandAround(int x, int y)
            {
                for (int oy = -2; oy <= 3; oy++) for (int ox = -2; ox <= 3; ox++)
                {
                    int vx = x + ox, vy = y + oy;
                    if (vx < 0 || vy < 0 || vx > columns || vy > rows) continue;
                    int key = vy * stride + vx;
                    if (!protectedVertices[key]) surface[key] = true;
                }
            }
            for (int y = 0; y < rows; y++) for (int x = 0; x < columns; x++)
                if (plateau[y * columns + x]) SandAround(x, y);
            foreach (var stamp in map.PlacedStamps.Where(s => s.Wall?.FamilyId == family.Id))
                foreach (var cell in stamp.Cells.Where(c => c.Cell.ForegroundImages.Count > 0))
                    SandAround((stamp.X + cell.X) / 2, (stamp.Y + cell.Y) / 2);
            // Province dirt and coarse sand share the same native centre tiles.
            // Clear cracked dirt/grass around the sand so unlike transitions
            // never meet directly at a shared tile edge.
            for (int y = 0; y <= rows; y++) for (int x = 0; x <= columns; x++)
            {
                if (!surface[y * stride + x]) continue;
                for (int oy = -2; oy <= 2; oy++) for (int ox = -2; ox <= 2; ox++)
                {
                    int vx = x + ox, vy = y + oy;
                    if (vx < 0 || vy < 0 || vx > columns || vy > rows) continue;
                    int key = vy * stride + vx;
                    if (!protectedVertices[key]) cover[key] = false;
                }
            }
        }
        else if (family.Id == "mongchon-province")
        {
            // The mud atlas has native edges onto coarse sand. Use them where
            // the thin ridge cannot hide a complete rectangular ground tile.
            for (int y = 0; y < rows; y++) for (int x = 0; x < columns; x++)
                if (plateau[y * columns + x]) foreach (int key in Corners(x, y))
                    if (!protectedVertices[key]) surface[key] = true;
        }
        for (int y = 0; y < rows; y++)
        {
            cancellation.ThrowIfCancellationRequested();
            for (int x = 0; x < columns; x++)
            {
                if (!plateau[y * columns + x]) continue;
                for (int oy = -4; oy <= 5; oy++)
                for (int ox = -4; ox <= 5; ox++)
                {
                    int vx = x + ox, vy = y + oy;
                    if (vx < 0 || vy < 0 || vx > columns || vy > rows) continue;
                    int key = vy * stride + vx;
                    if (!protectedVertices[key]) cover[key] = style?.BaseId == "0:0:2300";
                }
            }
        }
        // Changing a band can split an existing overlay corner diagonally.
        // Trim those tips monotonically, preserving neighbouring shared edges.
        if (canyon || family.Id == "mongchon-province") Normalize(surface, new bool[surface.Length]);
        Normalize(cover, original);
        void Normalize(bool[] values, bool[] previous)
        {
            var queue = new Queue<(int X, int Y)>();
            for (int y = 0; y < rows; y++) for (int x = 0; x < columns; x++) queue.Enqueue((x, y));
            while (queue.TryDequeue(out var p))
            {
                cancellation.ThrowIfCancellationRequested();
                var corners = Corners(p.X, p.Y);
                int mask = 0;
                for (int k = 0; k < 4; k++) if (values[corners[k]]) mask |= 1 << k;
                if (mask is not (6 or 9)) continue;
                // A coast can lock a terrain vertex. Keep that component intact by
                // restoring the other original vertices in this rare seam case.
                bool keep = corners.Any(k => protectedVertices[k] && values[k]);
                foreach (int key in corners)
                {
                    bool value = keep ? previous[key] : false;
                    if (!keep && protectedVertices[key]) continue;
                    if (keep) protectedVertices[key] = true;
                    if (values[key] == value) continue;
                    values[key] = value;
                    int vx = key % stride, vy = key / stride;
                    for (int oy = -1; oy <= 0; oy++) for (int ox = -1; ox <= 0; ox++)
                        if (vx + ox >= 0 && vy + oy >= 0 && vx + ox < columns && vy + oy < rows) queue.Enqueue((vx + ox, vy + oy));
                }
            }
        }
        var random = new Random(seed ^ 0x4C1FF);
        for (int y = 0; y < rows; y++)
        {
            cancellation.ThrowIfCancellationRequested();
            for (int x = 0; x < columns; x++)
            {
                if (untouched[y * columns + x]) continue;
                var corners = Corners(x, y);
                bool changed = corners.Any(k => cover[k] != original[k]);
                int surfaceMask = 0;
                for (int k = 0; k < 4; k++) if (surface[corners[k]]) surfaceMask |= 1 << k;
                if (!plateau[y * columns + x] && !changed && surfaceMask == 0) continue;
                int mask = 0;
                for (int k = 0; k < 4; k++) if (cover[corners[k]]) mask |= 1 << k;
                var set = surfaceMask != 0 || plateau[y * columns + x] ? rock : mask == 0 ? basis : overlay;
                var variants = set.Shapes[surfaceMask != 0 ? surfaceMask : set == overlay ? mask : 15];
                var cell = map.Get(x * 2, y * 2).Copy();
                cell.BackLibrary = (short)set.Library;
                cell.BackImage = (cell.BackImage & unchecked((int)0xE0000000)) | (variants[random.Next(variants.Count)].Index + 1);
                map.Set(x * 2, y * 2, cell);
            }
        }
    }

    private static void FitMongchonRear(MapDocument map, bool[] plateau, bool[] untouched, int columns, int rows)
    {
        // Native rear cards are only one column wide. Their authored ground
        // edge follows the card's anchor, not a constant lattice-height offset.
        // These tile phases were checked against repeated uses in 3.map. Values
        // are relative to the anchor's even ground row, ordered EE, EO, OE, OO.
        int[]? Phases(int image) => image switch
        {
            5756 or 5758 or 5763 or 5769 => [0, 2, 0, 2],
            5757 => [-2, 0, 2, 2],
            5759 or 5760 or 5761 => [0, 0, 0, 2],
            5764 or 5765 or 5766 or 5767 => [0, 2, 0, 0],
            5768 => [2, 4, 0, 0],
            _ => null
        };
        var fitted = new Dictionary<int, (int Distance, bool Solid)>();
        foreach (var stamp in map.PlacedStamps.Where(s => s.Wall?.FamilyId == "mongchon-province"))
        foreach (var cell in stamp.Cells)
        foreach (var front in cell.Cell.ForegroundImages)
        {
            if (front.Library != 2 || Phases(front.Image & 0x7fff) is not { } phases) continue;
            int ax = stamp.X + cell.X, ay = stamp.Y + cell.Y;
            int x = ax / 2, edge = ((ay & ~1) + phases[(ax & 1) * 2 + (ay & 1)]) / 2;
            if (ax < 0 || x >= columns) continue;
            for (int y = Math.Max(0, edge - 4); y <= Math.Min(rows - 1, edge + 3); y++)
            {
                int key = y * columns + x, distance = Math.Abs(y - edge);
                if (untouched[key]) continue;
                bool solid = y >= edge;
                if (!fitted.TryGetValue(key, out var old) || distance < old.Distance)
                    fitted[key] = (distance, solid);
                else if (distance == old.Distance) fitted[key] = (distance, solid && old.Solid);
            }
        }
        foreach (var (key, value) in fitted) plateau[key] = value.Solid;
    }
}
