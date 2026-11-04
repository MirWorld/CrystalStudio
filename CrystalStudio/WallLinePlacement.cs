namespace CrystalStudio;

// A native freestanding tower can meet its two arms at different cell phases.
// Propagate those measured offsets through the run; leave logical brush anchors
// on their snapping lattice so erasing, saved topology and undo stay stable.
internal static class WallLinePlacement
{
    internal static bool TryOffsets(WallFamily family, HashSet<(int X,int Y)> occupied,
        out Dictionary<(int X,int Y),int> offsets)
    {
        offsets = [];
        if (family.LinePortOffsets.Count == 0) return true;
        var ports = family.LinePortOffsets.ToDictionary(p => (p.Mask,p.Direction), p => p.Y);
        var masks = occupied.ToDictionary(p => p, p => family.Directions
            .Where(d => occupied.Contains((p.X+d.X,p.Y+d.Y))).Sum(d => d.Bit));
        bool Junction((int X,int Y) p) => System.Numerics.BitOperations.PopCount((uint)masks[p]) > 2;
        foreach (var start in occupied.OrderBy(p => p.Y).ThenBy(p => p.X))
        {
            if (offsets.ContainsKey(start)) continue;
            offsets[start] = 0;
            if (Junction(start)) continue;
            var pending = new Queue<(int X,int Y)>();
            pending.Enqueue(start);
            while (pending.TryDequeue(out var point))
            foreach (var direction in family.Directions)
            {
                var next = (point.X+direction.X,point.Y+direction.Y);
                if (!masks.TryGetValue(next, out int nextMask)) continue;
                // Each native run keeps its measured corner heights. A junction
                // fits each arm to that run, instead of requiring all branches
                // (including independently drawn loops) to have one height.
                if (Junction(next)) continue;
                int opposite = direction.Bit <= 2 ? direction.Bit*4 : direction.Bit/4;
                int offset = offsets[point] + ports.GetValueOrDefault((masks[point],direction.Bit))
                    - ports.GetValueOrDefault((nextMask,opposite));
                if (offsets.TryGetValue(next, out int previous))
                {
                    if (previous != offset) return false;
                }
                else { offsets[next] = offset; pending.Enqueue(next); }
            }
        }
        return true;
    }

    internal static List<WallPart> JunctionParts(WallFamily family, (int X,int Y) point,
        int mask, int corners, HashSet<(int X,int Y)> occupied, Dictionary<(int X,int Y),int> offsets)
    {
        var heights = new Dictionary<int,int>();
        foreach (var direction in family.Directions.Where(d => (mask & d.Bit) != 0))
        {
            var next = (X:point.X+direction.X,Y:point.Y+direction.Y);
            int nextMask = family.Directions.Where(d => occupied.Contains((next.X+d.X,next.Y+d.Y))).Sum(d => d.Bit);
            int opposite = direction.Bit <= 2 ? direction.Bit*4 : direction.Bit/4;
            int port = family.LinePortOffsets.FirstOrDefault(p => p.Mask==nextMask && p.Direction==opposite)?.Y ?? 0;
            heights[direction.Bit] = offsets.GetValueOrDefault(next)+port;
        }
        return family.Variant(mask,corners).Parts.Select(part => part.ConnectionDirection == 0 ? part :
            part with { Y=part.Y+heights.GetValueOrDefault(part.ConnectionDirection) }).ToList();
    }

    internal static WallLayout Translate(WallLayout layout, int y) => new(
        layout.Cells.Select(c => c with { Y=c.Y+y }).ToArray(),
        layout.Priorities.ToDictionary(p => (p.Key.X,p.Key.Y+y),p => p.Value),
        layout.Depths.ToDictionary(p => (p.Key.X,p.Key.Y+y),p => (p.Value.Y+y,p.Value.X,p.Value.Id)));
}
