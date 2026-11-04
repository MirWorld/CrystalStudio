namespace CrystalStudio;

// Fit each boundary edge between its two native corner ports. Nearby corners
// on other edges impose no radius and never move the authored outline.
internal static class WallJoinClearance
{
    internal readonly record struct Conflict(int Kind, int U, int V, int OtherU, int OtherV);

    internal static HashSet<Conflict> Conflicts(WallFamily family, IEnumerable<(int X, int Y)> points)
    {
        var result = new HashSet<Conflict>();
        if (!family.FitsNativeCorners && !family.PaintsAreas) return result;
        var cells = points.Select(p => family.LatticeCoordinates(p.X, p.Y))
            .Select(p => (U: (int)Math.Round(p.U), V: (int)Math.Round(p.V))).ToHashSet();
        var spans = family.BoundarySpans.Where(s => s.Interior == family.DrawInterior)
            .ToDictionary(s => (s.AlongU,s.FromCorner,s.ToCorner),s => s.MinimumPieces);
        var corners = new Dictionary<(int U,int V),int>();
        // Cells around a vertex: 1=(u,v), 2=(u+1,v), 4=(u,v+1), 8=(u+1,v+1).
        foreach (var p in cells)
        foreach (var vertex in new[] { p, (p.U-1,p.V), (p.U,p.V-1), (p.U-1,p.V-1) })
        {
            if (corners.ContainsKey(vertex)) continue;
            var (u,v) = vertex;
            int mask = (cells.Contains((u,v)) ? 1 : 0) | (cells.Contains((u+1,v)) ? 2 : 0) |
                (cells.Contains((u,v+1)) ? 4 : 0) | (cells.Contains((u+1,v+1)) ? 8 : 0);
            corners[vertex] = mask;
            if (mask is 6 or 9) result.Add(new(1,u,v,u,v));
        }
        var edges = new Dictionary<(bool AlongU,int Line),SortedSet<int>>();
        foreach (var (u,v) in cells)
        {
            if (!cells.Contains((u-1,v))) Add(false,u-1,v-1);
            if (!cells.Contains((u+1,v))) Add(false,u,v-1);
            if (!cells.Contains((u,v-1))) Add(true,v-1,u-1);
            if (!cells.Contains((u,v+1))) Add(true,v,u-1);
        }
        foreach (var ((alongU,line),units) in edges)
        {
            int start = int.MinValue, end = int.MinValue;
            foreach (int unit in units)
            {
                // A vertex-only contact cannot disguise two short edges as
                // one long edge that could fit a complete native corner.
                var vertex = alongU ? (unit,line) : (line,unit);
                if (unit != end || corners[vertex] is 6 or 9)
                {
                    Check(alongU,line,start,end);
                    start = unit;
                }
                end = unit+1;
            }
            Check(alongU,line,start,end);
        }
        return result;

        void Add(bool alongU,int line,int start)
        {
            if (!edges.TryGetValue((alongU,line),out var units)) edges[(alongU,line)] = units = [];
            units.Add(start);
        }
        void Check(bool alongU,int line,int start,int end)
        {
            if (start == int.MinValue) return;
            var a = alongU ? (start,line) : (line,start);
            var b = alongU ? (end,line) : (line,end);
            if (spans.TryGetValue((alongU,corners[a],corners[b]),out int minimum) && end-start < minimum)
                result.Add(new(2,a.Item1,a.Item2,b.Item1,b.Item2));
        }
    }
}
