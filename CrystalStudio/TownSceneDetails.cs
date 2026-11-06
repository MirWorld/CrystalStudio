namespace CrystalStudio;

// Use complete native objects, grouped by purpose around the buildings that
// use them. The town planner still owns collision, spacing and access checks.
internal static class TownSceneDetails
{
    internal static void Place(TownFamily family, List<TownBuilding> buildings, HashSet<(int X, int Y)> roads,
        IReadOnlyList<(int X, int Y)> centres, TownOptions options,
        Func<TownPiece, int, int, bool, bool> place, CancellationToken token)
    {
        var random = new Random(options.Seed ^ 0x51CE7);
        var homes = buildings.Where(b => b.Piece.Role == "house").ToArray();
        var publicBuildings = buildings.Where(b => b.Piece.Role is "market" or "landmark").ToArray();
        var props = family.Pieces.Where(p => p.Role == "prop").ToArray();
        var greenery = family.Pieces.Where(p => p.Role == "greenery" && !p.IsTree && Use(p) != "crop").ToArray();

        // One shared meeting place, rather than a well or notice board in
        // every backyard. Keep public objects within reach of the street.
        foreach (string use in new[] { "well", "notice", "bench" })
        {
            var piece = props.Where(p => Use(p) == use).OrderBy(_ => random.Next()).FirstOrDefault();
            if (piece == null) continue;
            foreach (var host in (publicBuildings.Length > 0 ? publicBuildings : homes)
                .OrderBy(h => centres.Min(c => Distance(h.Lot.X, h.Lot.Y, c.X, c.Y))))
                if (Fit(piece, host, publicUse: true) != null) break;
        }

        foreach (var home in homes.OrderBy(h => h.Lot.X + h.Lot.Y))
        {
            token.ThrowIfCancellationRequested();
            // Some households grow food; others have a working/storage yard.
            // A second crop bed follows the first, instead of being scattered
            // independently across the whole settlement.
            var crops = family.Pieces.Where(p => Use(p) == "crop").ToArray();
            TownBuilding? bed = null;
            if (options.Greenery && crops.Length > 0 && random.Next(3) != 0)
            {
                var crop = crops[random.Next(crops.Length)];
                bed = Fit(crop, home);
                if (bed != null) Fit(crop, home, neighbour: bed);
            }
            var work = props.Where(p => Use(p) is "storage" or "cart" or "hay").ToArray();
            if (work.Length > 0) Fit(work[random.Next(work.Length)], home, neighbour: bed);
            if (options.Greenery && greenery.Length > 0)
                Fit(greenery[random.Next(greenery.Length)], home);
        }

        TownBuilding? Fit(TownPiece piece, TownBuilding host, bool publicUse = false, TownBuilding? neighbour = null)
        {
            int margin = options.Spacing + Math.Max(piece.Bounds.Width, piece.Bounds.Height) + 4;
            var candidates = new List<(int X, int Y, double Score)>();
            var reference = neighbour?.Lot ?? host.Lot;
            for (int y = host.Lot.Y - margin; y <= host.Lot.Y + host.Lot.Height + margin; y += 2)
            for (int x = host.Lot.X - margin; x <= host.Lot.X + host.Lot.Width + margin; x += 2)
            {
                token.ThrowIfCancellationRequested();
                int gapX = Math.Max(0, Math.Max(host.Lot.X - x - piece.Bounds.Width, x - host.Lot.X - host.Lot.Width));
                int gapY = Math.Max(0, Math.Max(host.Lot.Y - y - piece.Bounds.Height, y - host.Lot.Y - host.Lot.Height));
                if (Math.Max(gapX, gapY) > options.Spacing + 5) continue;
                int cx = x + piece.Bounds.Width / 2, cy = y + piece.Bounds.Height / 2;
                if (publicUse && !roads.Any(r => Distance(cx, cy, r.X, r.Y) <= 5)) continue;
                double score = Distance(cx, cy, reference.X + reference.Width / 2, reference.Y + reference.Height / 2)
                    + random.NextDouble() * 3;
                candidates.Add((x - piece.Bounds.X, y - piece.Bounds.Y, score));
            }
            foreach (var site in candidates.OrderBy(c => c.Score))
            {
                token.ThrowIfCancellationRequested();
                // Crops and working yards receive a walkable spur; decorative
                // plants stay off those access lanes.
                if (place(piece, site.X, site.Y, piece.Role == "prop" || Use(piece) == "crop")) return buildings[^1];
            }
            return null;
        }
    }

    internal static string Use(TownPiece piece) => piece.Name.Split(' ')[0].ToLowerInvariant() switch
    {
        "crop" => "crop", "storage" or "barrels" => "storage", "cart" => "cart", "haystack" => "hay",
        "well" => "well", "notice" => "notice", "bench" => "bench", _ => "garden"
    };

    private static int Distance(int x, int y, int xx, int yy) => Math.Abs(x - xx) + Math.Abs(y - yy);
}
