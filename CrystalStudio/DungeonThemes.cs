namespace CrystalStudio;

public enum DungeonTheme { Classic, CelestialPalace, AbyssalReef, AlienPlanet }

internal static class DungeonThemes
{
    internal static TempleDefinition Definition(DungeonTheme theme) => theme switch
    {
        DungeonTheme.CelestialPalace => new("Celestial Palace", "wooma-temple", "0:0:2700"),
        DungeonTheme.AbyssalReef => new("Abyssal Reef", "bug-cave", "0:0:4500"),
        DungeonTheme.AlienPlanet => new("Alien Planet", "bug-cave-dungeon", "0:0:3500"),
        _ => throw new ArgumentOutOfRangeException(nameof(theme))
    };

    internal static string Exterior(DungeonTheme theme) => theme switch
    {
        DungeonTheme.CelestialPalace => "0:0:4450",
        DungeonTheme.AbyssalReef => "0:0:3000",
        DungeonTheme.AlienPlanet => "0:0:3350",
        _ => throw new ArgumentOutOfRangeException(nameof(theme))
    };

    internal static DungeonType Type(DungeonTheme theme) => theme == DungeonTheme.CelestialPalace ? DungeonType.Temple : DungeonType.Cave;

    internal static string Description(DungeonTheme theme) => LocalText.T(theme switch
    {
        DungeonTheme.CelestialPalace => "Axial halls, paired courts and bridges above a white cloud sea",
        DungeonTheme.AbyssalReef => "Coral chambers, tidal loops and a submerged central sanctuary",
        DungeonTheme.AlienPlanet => "Offset impact basins, fractured routes and crystalline life",
        _ => "Classic dungeon and cave layouts"
    });
}

// Layouts are authored on the native wall lattice. Topology is independent of
// artwork: fitting stretches occupied regions, never native sprite cards.
internal static class DungeonThemeLayout
{
    internal static (HashSet<(int U, int V)> Nodes, (int U, int V)[] Centres) Create(
        DungeonTheme theme, int extent, int size, int requested, int width, Random random, CancellationToken token)
    {
        var nodes = new HashSet<(int U, int V)>(); var centres = new List<(int U, int V)>();
        int radius = theme == DungeonTheme.AlienPlanet && extent < 16 ? 1 : Math.Max(1, size / 2);
        int pitch = radius * 2 + 4;
        if (theme != DungeonTheme.CelestialPalace) width = Math.Min(width, Math.Max(1, radius - 1));
        if (theme == DungeonTheme.CelestialPalace)
        {
            int levels = (requested + 2) / 3;
            if ((requested - levels) % 2 != 0) levels++;
            levels = Math.Min(levels, Math.Max(1, (2 * extent - 4 * radius - 2 * pitch) / pitch + 1));
            for (int i = 0; i < levels; i++)
            {
                var p = (U: 0, V: i * pitch); centres.Add(p); Box(p, radius);
                if (i > 0) Link(centres[i - 1], p, Math.Max(1, width));
            }
            for (int i = 0; i < levels && centres.Count + 2 <= requested; i++)
            {
                foreach (int side in new[] { -1, 1 })
                {
                    var p = (U: side * pitch, V: i * pitch); centres.Add(p); Box(p, radius);
                    Link((0, i * pitch), p, Math.Max(1, width));
                    if (i > 0) Link((side * pitch, (i - 1) * pitch), p, Math.Max(1, width));
                }
            }
        }
        else if (theme == DungeonTheme.AbyssalReef)
        {
            int ringRadius = Math.Max(1, (int)(extent * .65) - radius - 2);
            var hub = (U: 0, V: 0); centres.Add(hub); Disc(hub, radius + 1, radius);
            int pockets = Math.Min(requested - 1, Math.Max(2, ringRadius * 2));
            double phase = random.NextDouble() * Math.Tau;
            for (int i = 0; i < pockets; i++)
            {
                token.ThrowIfCancellationRequested();
                double angle = phase + i * Math.Tau / pockets;
                var p = (U: (int)Math.Round(Math.Cos(angle) * ringRadius), V: (int)Math.Round(Math.Sin(angle) * ringRadius * .8));
                if (centres.Contains(p)) continue;
                centres.Add(p); Disc(p, radius + random.Next(2), radius);
            }
            for (int i = 1; i < centres.Count; i++)
            {
                var a = centres[i]; var b = centres[i + 1 < centres.Count ? i + 1 : 1];
                var mid = (U: (a.U + b.U) / 2, V: (a.V + b.V) / 2);
                Link(a, mid, width); Link(mid, b, width);
                if (i == 1 || i == centres.Count / 3 || i == centres.Count * 2 / 3) Link(hub, a, width);
            }
        }
        else if (theme == DungeonTheme.AlienPlanet)
        {
            int spread = Math.Max(1, (int)(extent * .55) - radius - 2);
            var basins = new List<(int U, int V)> { (-spread / 3, spread / 4) };
            for (int attempt = 0; basins.Count < requested && attempt < requested * 80; attempt++)
            {
                token.ThrowIfCancellationRequested();
                var p = (U: random.Next(-spread, spread + 1), V: random.Next(-spread, spread + 1));
                if (Math.Abs(p.U) + Math.Abs(p.V) > Math.Max(1, extent - (radius + 2) * 2) || basins.Any(b => Math.Pow(b.U - p.U, 2) + Math.Pow(b.V - p.V, 2) < Math.Pow(radius * 2 + 3, 2))) continue;
                basins.Add(p);
            }
            foreach (var b in basins)
            {
                int outer = radius + 2;
                Disc(b, outer, outer - 1);
                // A broad crater rim surrounds an inaccessible impact core.
                // Its landmark lies on the rim, never in the blocked centre.
                int core = radius > 1 ? 1 : 0;
                for (int u = -core; u <= core; u++) for (int v = -core; v <= core; v++) nodes.Remove((b.U + u, b.V + v));
                centres.Add((b.U + outer - 1, b.V));
            }
            var joined = new HashSet<int> { 0 }; var edges = new HashSet<(int, int)>();
            while (joined.Count < centres.Count)
            {
                token.ThrowIfCancellationRequested();
                var edge = joined.Order().SelectMany(a => Enumerable.Range(0, centres.Count).Where(b => !joined.Contains(b)).Select(b => (A: a, B: b)))
                    .OrderBy(e => Distance(centres[e.A], centres[e.B])).ThenBy(e => e.A).ThenBy(e => e.B).First();
                edges.Add((edge.A, edge.B)); joined.Add(edge.B);
            }
            // Secondary fractures form return routes without regularising the
            // basin field into the temple's room grid or the reef's main ring.
            for (int i = 0; i < centres.Count; i++)
            {
                int next = Enumerable.Range(0, centres.Count).Where(j => j != i && !edges.Contains((i, j)) && !edges.Contains((j, i)))
                    .OrderBy(j => Distance(centres[i], centres[j])).DefaultIfEmpty(-1).First();
                if (next >= 0 && random.Next(3) == 0) edges.Add((i, next));
            }
            foreach (var (a, b) in edges.OrderBy(e => e.Item1).ThenBy(e => e.Item2))
            {
                var bend = (U: centres[a].U, V: centres[b].V);
                Link(centres[a], bend, width); Link(bend, centres[b], width);
            }
        }
        else throw new ArgumentOutOfRangeException(nameof(theme));
        token.ThrowIfCancellationRequested();
        return (nodes, centres.ToArray());

        static int Distance((int U, int V) a, (int U, int V) b) => Math.Abs(a.U - b.U) + Math.Abs(a.V - b.V);
        void Box((int U, int V) p, int r)
        {
            for (int u = -r; u <= r; u++) for (int v = -r; v <= r; v++) nodes.Add((p.U + u, p.V + v));
        }
        void Disc((int U, int V) p, int ru, int rv)
        {
            for (int u = -ru; u <= ru; u++) for (int v = -rv; v <= rv; v++)
                if ((double)u * u / (ru * ru) + (double)v * v / (rv * rv) <= 1) nodes.Add((p.U + u, p.V + v));
        }
        void Link((int U, int V) a, (int U, int V) b, int r)
        {
            while (true)
            {
                token.ThrowIfCancellationRequested(); Box(a, Math.Max(0, r - 1));
                if (a == b) break;
                if (Math.Abs(b.U - a.U) >= Math.Abs(b.V - a.V) && a.U != b.U) a.U += Math.Sign(b.U - a.U);
                else a.V += Math.Sign(b.V - a.V);
            }
        }
    }
}
