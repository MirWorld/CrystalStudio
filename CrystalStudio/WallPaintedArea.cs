namespace CrystalStudio;

// Only join the floor directly between this brush mark and nearby painted
// sections. Closing a path must not paint the unbrushed area it encloses.
internal static class WallPaintedArea
{
    private static readonly (int U,int V)[] Directions = [(1,0),(-1,0),(0,1),(0,-1)];

    internal static void Merge(WallFamily family,HashSet<(int X,int Y)> occupied,
        HashSet<(int X,int Y)> before,IReadOnlyList<(int X,int Y)> requested)
    {
        var old = before.Select(Grid).ToHashSet();
        foreach (var point in requested.Select(Grid))
        {
            var reaches = Directions.Select(direction =>
                Enumerable.Range(1,family.StrokeGridPieces).FirstOrDefault(distance =>
                    old.Contains((point.U+direction.U*distance,point.V+direction.V*distance)))).ToArray();
            for (int direction=0;direction<Directions.Length;direction++)
            {
                var (du,dv)=Directions[direction];
                for (int i=1;i<reaches[direction];i++)
                    occupied.Add(family.GridPoint(point.U+du*i,point.V+dv*i));
            }
        }

        // A one-node brush can surround another complete native section.
        // Keep that opening until the user paints it explicitly.
        if (family.PaintSizePieces == 1) return;

        // A local join can surround a pocket smaller than the brush itself,
        // where complete native shoulders cannot fit. Close only those new
        // pockets; never flood-fill a room or an existing unpainted island.
        var joined=occupied.Select(Grid).ToHashSet();
        var candidates=joined.Except(old).SelectMany(p=>Directions.Select(d=>(U:p.U+d.U,V:p.V+d.V))).ToHashSet();
        foreach (var seed in candidates)
        {
            var pocket=SmallPocket(joined,seed);
            if (pocket==null || SmallPocket(old,seed)!=null) continue;
            foreach (var p in pocket)
            {
                joined.Add(p);
                occupied.Add(family.GridPoint(p.U,p.V));
            }
        }

        HashSet<(int U,int V)>? SmallPocket(HashSet<(int U,int V)> floor,(int U,int V) seed)
        {
            if (floor.Contains(seed)) return null;
            var found=new HashSet<(int U,int V)> {seed};
            var queue=new Queue<(int U,int V)>();
            queue.Enqueue(seed);
            while (queue.TryDequeue(out var p))
            foreach (var d in Directions)
            {
                var next=(U:p.U+d.U,V:p.V+d.V);
                if (floor.Contains(next) || !found.Add(next)) continue;
                if (found.Count>family.PaintSizePieces*family.PaintSizePieces) return null;
                queue.Enqueue(next);
            }
            return found.Max(p=>p.U)-found.Min(p=>p.U)<family.PaintSizePieces &&
                found.Max(p=>p.V)-found.Min(p=>p.V)<family.PaintSizePieces ? found : null;
        }

        (int U,int V) Grid((int X,int Y) point)
        {
            var (u,v)=family.LatticeCoordinates(point.X,point.Y);
            return ((int)Math.Round(u),(int)Math.Round(v));
        }
    }
}
