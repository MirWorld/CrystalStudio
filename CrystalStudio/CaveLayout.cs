namespace CrystalStudio;

internal static class CaveLayout
{
    internal static void AddInteriorWalls(HashSet<(int U, int V)> nodes, IReadOnlyList<(int U, int V)> centres,
        int passageWidth, Random random, CancellationToken cancellation)
    {
        // Work after map fitting, on the same coarse lattice that reserves
        // space for complete native corners. Empty islands become inner walls
        // through the ordinary wall brush, including their blocked interiors.
        var candidates = nodes.OrderBy(p => p.V).ThenBy(p => p.U).ToArray();
        random.Shuffle(candidates);
        int remaining = Math.Clamp(nodes.Count / 80, 1, 12);
        int clearance = Math.Max(2, passageWidth);
        foreach (var p in candidates)
        {
            cancellation.ThrowIfCancellationRequested();
            if (remaining == 0) break;
            if (random.Next(100) >= 60) continue;
            int width = random.Next(2, 5), height = random.Next(2, 5);
            if (centres.Any(c => c.U >= p.U - 1 && c.U <= p.U + width &&
                c.V >= p.V - 1 && c.V <= p.V + height)) continue;
            bool fits = true;
            for (int u = -clearance; fits && u < width + clearance; u++)
            for (int v = -clearance; v < height + clearance; v++)
                if (!nodes.Contains((p.U + u, p.V + v))) { fits = false; break; }
            if (!fits) continue;
            // A complete ring of floor preserves connectivity and keeps islands
            // away from narrow routes, other walls and diagonal corner contacts.
            for (int u = 0; u < width; u++)
            for (int v = 0; v < height; v++) nodes.Remove((p.U + u, p.V + v));
            remaining--;
        }
    }

    internal static (HashSet<(int U, int V)> Nodes, (int U, int V)[] Centres) Create(
        int extent, int size, int count, int passageWidth, bool tunnelsOnly, Random random, CancellationToken cancellation)
    {
        var nodes = new HashSet<(int U, int V)>();
        var centres = new List<(int U, int V)>();
        var current = (U: extent / 2, V: extent / 2);
        int maxRadius = Math.Max(1, Math.Min(size, (extent - 1) / 2));
        int minRadius = Math.Max(1, maxRadius - 1);
        int edge = tunnelsOnly ? 0 : Math.Min(maxRadius, (extent - 1) / 2);
        // Overlapping rounded pockets and broad, changing passages create one
        // irregular cavern. All carving stays on the native wall lattice.
        for (int pocket = 0; pocket < count; pocket++)
        {
            cancellation.ThrowIfCancellationRequested();
            if (centres.Count > 0 && random.Next(4) == 0) current = centres[random.Next(centres.Count)];
            double angle = random.NextDouble() * Math.Tau;
            int distance = random.Next(Math.Max(2, size), Math.Max(3, size * 3));
            var target = (U: Math.Clamp(current.U + (int)Math.Round(Math.Cos(angle) * distance), edge, extent - 1 - edge),
                V: Math.Clamp(current.V + (int)Math.Round(Math.Sin(angle) * distance), edge, extent - 1 - edge));
            int radius = tunnelsOnly ? 0 : random.Next(minRadius, maxRadius + 1);
            Carve(current, radius);
            while (current != target)
            {
                // Variable bends avoid the regular room-and-corridor grid.
                if (current.V == target.V || current.U != target.U && random.Next(2) == 0)
                    current.U += Math.Sign(target.U - current.U);
                else current.V += Math.Sign(target.V - current.V);
                Carve(current, tunnelsOnly ? 0 : Math.Max(1, radius - 2 + passageWidth));
                if (!tunnelsOnly && random.Next(4) == 0) radius = random.Next(minRadius, maxRadius + 1);
            }
            Carve(current, radius);
            centres.Add(current);
        }
        // A tiny map can clamp a walk to its start; keep tunnels non-empty.
        if (tunnelsOnly && nodes.Count == 1 && extent > 1)
            nodes.Add((current.U == 0 ? 1 : current.U - 1, current.V));
        return (nodes, centres.ToArray());

        void Carve((int U, int V) centre, int radius)
        {
            for (int u = -radius; u <= radius; u++)
            for (int v = -radius; v <= radius; v++)
            {
                var p = (U: centre.U + u, V: centre.V + v);
                if (u * u + v * v <= radius * radius && p.U >= 0 && p.V >= 0 && p.U < extent && p.V < extent)
                    nodes.Add(p);
            }
        }
    }
}
