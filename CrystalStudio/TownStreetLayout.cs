namespace CrystalStudio;

public enum TownLayout { Automatic, HighStreet, Crossroads, MarketSquare, VillageGreen, Grid, WindingStreet, ForkedLanes, LinkedHamlets }

internal sealed record TownFrontage(int X, int Y, bool Horizontal, int Setback = 4);

// Lots use original street edges, never other buildings' driveways.
internal sealed record TownStreetLayout(string Name, HashSet<(int X, int Y)> Roads,
    List<TownFrontage> Frontages, List<(int X, int Y)> Centres)
{
    internal static TownStreetLayout Create(int size, Random random, TownLayout requested = TownLayout.Automatic)
    {
        // Automatic towns favour village forms, with the strict grid available
        // as an explicit choice for users who want it.
        TownLayout[] choices = [TownLayout.HighStreet, TownLayout.Crossroads, TownLayout.MarketSquare,
            TownLayout.VillageGreen, TownLayout.WindingStreet, TownLayout.ForkedLanes, TownLayout.LinkedHamlets];
        var kind = requested == TownLayout.Automatic ? choices[random.Next(choices.Length)] : requested;
        bool transpose = random.Next(2) == 0;
        var roads = new HashSet<(int X, int Y)>();
        var frontages = new List<TownFrontage>();
        var centres = new List<(int X, int Y)>();
        int Scale(double fraction) => (int)Math.Round(fraction * size / 2) * 2;
        double bendX = kind == TownLayout.Grid ? 0 : (random.NextDouble() * 2 - 1) * size * .055;
        double bendY = kind == TownLayout.Grid ? 0 : (random.NextDouble() * 2 - 1) * size * .055;
        (int X, int Y) Turn(int x, int y)
        {
            // A shared smooth bend preserves every junction and map exit.
            double envelope = Math.Sin(Math.PI * (x - 4) / (size - 9)) * Math.Sin(Math.PI * (y - 4) / (size - 9));
            var p = (X: x + (int)Math.Round(bendX * envelope), Y: y + (int)Math.Round(bendY * envelope));
            return transpose ? (p.Y, p.X) : p;
        }
        void Lane(int x1, int y1, int x2, int y2)
        {
            bool horizontal = Math.Abs(x2 - x1) >= Math.Abs(y2 - y1);
            int steps = Math.Max(Math.Abs(x2 - x1), Math.Abs(y2 - y1));
            int setback = kind == TownLayout.Grid || size < 80 ? 4 : random.Next(3, 6);
            (int X, int Y)? previous = null;
            for (int i = 0; i <= steps; i++)
            {
                double t = steps == 0 ? 0 : i / (double)steps;
                var p = Turn((int)Math.Round(x1 + (x2 - x1) * t), (int)Math.Round(y1 + (y2 - y1) * t));
                void Paint(int x, int y)
                {
                    for (int dy = -1; dy <= 1; dy++) for (int dx = -1; dx <= 1; dx++)
                        if (Math.Abs(dx) + Math.Abs(dy) <= 1 && x + dx >= 4 && y + dy >= 4 && x + dx < size - 4 && y + dy < size - 4)
                            roads.Add((x + dx, y + dy));
                }
                // Supercover the transformed segments so gentle diagonals
                // remain walkable with four-way movement.
                if (previous is { } old)
                {
                    int x = old.X, y = old.Y;
                    while (x != p.X || y != p.Y)
                    {
                        x += Math.Sign(p.X - x); y += Math.Sign(p.Y - y); Paint(x, y);
                    }
                }
                else Paint(p.X, p.Y);
                previous = p;
                if (i % 2 == 0 && p.X >= 10 && p.Y >= 10 && p.X < size - 10 && p.Y < size - 10)
                    frontages.Add(new(p.X, p.Y, horizontal != transpose, setback));
            }
        }
        void Route(params (double X, double Y)[] nodes)
        {
            for (int i = 1; i < nodes.Length; i++)
                Lane(Math.Clamp(Scale(nodes[i - 1].X), 4, size - 5), Math.Clamp(Scale(nodes[i - 1].Y), 4, size - 5),
                    Math.Clamp(Scale(nodes[i].X), 4, size - 5), Math.Clamp(Scale(nodes[i].Y), 4, size - 5));
        }
        void Square(int x, int y, int radius)
        {
            var centre = Turn(x, y);
            // Translate courts as a whole: warping each cell separately can
            // leave diagonal-only corners when the rounding changes.
            for (int dy = -radius; dy <= radius; dy++) for (int dx = -radius; dx <= radius; dx++)
                if (centre.X + dx >= 4 && centre.Y + dy >= 4 && centre.X + dx < size - 4 && centre.Y + dy < size - 4)
                    roads.Add((centre.X + dx, centre.Y + dy));
            centres.Add(centre);
        }
        int low = Scale(.36), high = Scale(.76), mid = Scale(.56);
        var rows = size >= 128 ? new[] { Scale(.27), Scale(.52), Scale(.77) } : new[] { low, high };
        string name;
        switch (kind)
        {
            case TownLayout.WindingStreet:
                name = "Winding high street";
                Route((0, .32), (.30, .38), (.58, .32), (.76, .52), (1, .62));
                Route((.30, .38), (.27, .68), (.50, .82), (.80, .78), (1, .86));
                Route((.58, .32), (.68, 0));
                centres.Add(Turn(Scale(.30), Scale(.38)));
                centres.Add(Turn(Scale(.50), Scale(.82)));
                break;
            case TownLayout.ForkedLanes:
                name = "Forked village lanes";
                Route((0, .60), (.30, .56), (.52, .42), (.70, .29), (1, .34));
                Route((.30, .56), (.51, .78), (.76, .74), (1, .84));
                Route((.52, .42), (.36, .20), (.42, 0));
                centres.Add(Turn(Scale(.30), Scale(.56)));
                centres.Add(Turn(Scale(.70), Scale(.29)));
                break;
            case TownLayout.LinkedHamlets:
                name = "Linked hamlets";
                Route((0, .34), (.28, .36), (.46, .51), (.68, .69), (1, .72));
                Route((.28, .36), (.33, .70), (.17, .84));
                Route((.46, .51), (.73, .32), (.77, 0));
                Route((.68, .69), (.58, .87), (.83, .91));
                Square(Scale(.28), Scale(.36), 3);
                Square(Scale(.68), Scale(.69), 3);
                break;
            case TownLayout.HighStreet:
                name = "High street village";
                foreach (int row in rows) Lane(4, row, size - 5, row);
                Lane(mid, rows[0], mid, rows[^1]);
                centres.Add(Turn(mid, rows[0]));
                break;
            case TownLayout.Crossroads:
                name = "Planned crossroads";
                foreach (int row in rows) Lane(4, row, size - 5, row);
                Lane(mid, 4, mid, size - 5);
                centres.Add(Turn(mid, rows[^1]));
                break;
            case TownLayout.MarketSquare:
                name = "Market square";
                foreach (int row in rows) Lane(4, row, size - 5, row);
                Lane(mid, 4, mid, size - 5);
                Square(mid, rows[0], Math.Max(4, size / 18));
                break;
            case TownLayout.VillageGreen:
                name = "Village green";
                foreach (int row in rows) Lane(4, row, size - 5, row);
                Lane(Scale(.32), rows[0], Scale(.32), rows[^1]);
                Lane(Scale(.78), rows[0], Scale(.78), rows[^1]);
                centres.Add(Turn(Scale(.32), rows[0]));
                centres.Add(Turn(Scale(.78), rows[^1]));
                break;
            default:
                name = "Town blocks";
                foreach (int row in rows) Lane(4, row, size - 5, row);
                Lane(Scale(.36), 4, Scale(.36), size - 5);
                Lane(Scale(.78), rows[0], Scale(.78), size - 5);
                centres.Add(Turn(Scale(.36), rows[0]));
                break;
        }
        return new(name, roads, frontages.Distinct().ToList(), centres);
    }
}
