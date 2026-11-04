namespace CrystalStudio;

// The original shoulders have longer ports than a single extension tile. Route
// cramped returns geometrically, keeping their full native artwork intact.
internal static class WallTurnRouting
{
    private static readonly (int X,int Y)[] Neighbours=[(1,0),(0,1),(-1,0),(0,-1)];

    public static HashSet<(int X,int Y)> Fit(WallFamily family, HashSet<(int X,int Y)> occupied)
    {
        int clearance=family.TurnClearancePieces;
        var grid=occupied.Select(p=>family.LatticeCoordinates(p.X,p.Y))
            .Select(p=>((int)Math.Round(p.U),(int)Math.Round(p.V))).ToHashSet();
        var result=new HashSet<(int X,int Y)>();
        foreach(var component in Components(grid))
        {
            var paths=Outline(component);
            if(!paths.Any(p=>Cramped(p,clearance))) {result.UnionWith(component);continue;}
            var important=paths.SelectMany(p=>Turns(p,clearance)).ToArray();
            HashSet<(int X,int Y)>? best=null;
            long bestScore=long.MaxValue;
            for(int ox=0;ox<clearance;ox++)
            for(int oy=0;oy<clearance;oy++)
            {
                var moved=paths.Select(path=>Move(path,clearance,ox,oy)).ToArray();
                var candidate=Raster(moved);
                if(candidate.Count==0)continue;
                Join(candidate,component,clearance,ox,oy);
                var outline=Outline(candidate);
                // Connecting two branches must not introduce another cramped
                // shoulder. Finish just those new returns on the native grid.
                if(outline.Any(p=>Cramped(p,clearance)))
                {
                    moved=outline.Select(path=>Move(path,clearance,ox,oy)).ToArray();
                    candidate=Raster(moved);
                    if(candidate.Count==0)continue;
                    Join(candidate,component,clearance,ox,oy);
                    outline=Outline(candidate);
                }
                if(outline.Any(p=>Cramped(p,clearance)))continue;
                long score=component.Count(p=>!candidate.Contains(p))+candidate.Count(p=>!component.Contains(p));
                var turns=outline.SelectMany(p=>Turns(p,clearance)).ToArray();
                // Keep substantial recesses. Tiny alternating jags can merge,
                // but deleting a long return carries a much higher cost.
                foreach(var turn in important)
                    score+=12*(turns.Where(t=>t.Normal==turn.Normal).Select(t=>
                        (long)Math.Abs(t.Point.X-turn.Point.X)+Math.Abs(t.Point.Y-turn.Point.Y)).DefaultIfEmpty(64).Min());
                if(score<bestScore){best=candidate;bestScore=score;}
            }
            result.UnionWith(best??component);
        }
        return result.Select(p=>family.GridPoint(p.X,p.Y)).ToHashSet();
    }

    private static List<HashSet<(int X,int Y)>> Components(HashSet<(int X,int Y)> cells)
    {
        var left=cells.ToHashSet();var groups=new List<HashSet<(int X,int Y)>>();
        while(left.Count>0)
        {
            var start=left.Min();left.Remove(start);var group=new HashSet<(int X,int Y)>{start};var queue=new Queue<(int X,int Y)>();queue.Enqueue(start);
            while(queue.TryDequeue(out var p))foreach(var d in Neighbours)
            {
                var n=(p.X+d.X,p.Y+d.Y);
                if(left.Remove(n)){group.Add(n);queue.Enqueue(n);}
            }
            groups.Add(group);
        }
        return groups;
    }

    // Doubled integer coordinates keep half-cell boundary vertices exact.
    private static List<List<(int X,int Y)>> Outline(HashSet<(int X,int Y)> cells)
    {
        var edges=new HashSet<((int X,int Y) A,(int X,int Y) B)>();
        foreach(var p in cells)
        {
            var a=(2*p.X-1,2*p.Y-1);var b=(2*p.X+1,2*p.Y-1);var c=(2*p.X+1,2*p.Y+1);var d=(2*p.X-1,2*p.Y+1);
            if(!cells.Contains((p.X,p.Y-1)))edges.Add((a,b));
            if(!cells.Contains((p.X+1,p.Y)))edges.Add((b,c));
            if(!cells.Contains((p.X,p.Y+1)))edges.Add((c,d));
            if(!cells.Contains((p.X-1,p.Y)))edges.Add((d,a));
        }
        var paths=new List<List<(int X,int Y)>>();
        while(edges.Count>0)
        {
            var edge=edges.Min();edges.Remove(edge);var start=edge.A;var previous=start;var end=edge.B;var path=new List<(int X,int Y)>{start};
            while(end!=start)
            {
                path.Add(end);
                int dx=end.X-previous.X,dy=end.Y-previous.Y;
                var next=edges.Where(e=>e.A==end).OrderByDescending(e=>Math.Atan2(dx*(e.B.Y-end.Y)-dy*(e.B.X-end.X),dx*(e.B.X-end.X)+dy*(e.B.Y-end.Y))).First();
                edges.Remove(next);previous=end;end=next.B;
            }
            paths.Add(Simplify(path));
        }
        return paths;
    }

    private static List<(int X,int Y)> Simplify(List<(int X,int Y)> path)
    {
        var unique=new List<(int X,int Y)>();
        foreach(var p in path)if(unique.Count==0 || unique[^1]!=p)unique.Add(p);
        if(unique.Count>1 && unique[0]==unique[^1])unique.RemoveAt(unique.Count-1);
        return unique.Where((p,i)=>
        {
            var a=unique[(i+unique.Count-1)%unique.Count];var b=unique[(i+1)%unique.Count];
            return (p.X-a.X)*(b.Y-p.Y)!=(p.Y-a.Y)*(b.X-p.X);
        }).ToList();
    }

    private static bool Concave(List<(int X,int Y)> path,int i)
    {
        var p=path[i];var a=path[(i+path.Count-1)%path.Count];var b=path[(i+1)%path.Count];
        return (p.X-a.X)*(b.Y-p.Y)-(p.Y-a.Y)*(b.X-p.X)<0;
    }
    private static int Length((int X,int Y) a,(int X,int Y) b)=>Math.Abs(a.X-b.X)+Math.Abs(a.Y-b.Y);
    private static bool Cramped(List<(int X,int Y)> path,int size)=>path.Where((p,i)=>
        (Concave(path,i)||Concave(path,(i+1)%path.Count)) && Length(p,path[(i+1)%path.Count])<2*size).Any();

    private static IEnumerable<((int X,int Y) Point,(int X,int Y) Normal)> Turns(List<(int X,int Y)> path,int size)
    {
        for(int i=0;i<path.Count;i++)
        {
            var p=path[i];var a=path[(i+path.Count-1)%path.Count];var b=path[(i+1)%path.Count];
            if(Concave(path,i) && Math.Max(Length(p,a),Length(p,b))>=2*size)
                yield return (p,(Math.Sign(a.X+b.X-2*p.X),Math.Sign(a.Y+b.Y-2*p.Y)));
        }
    }

    private static List<(int X,int Y)> Move(List<(int X,int Y)> path,int size,int ox,int oy)
    {
        var moveX=new HashSet<int>();var moveY=new HashSet<int>();
        for(int i=0;i<path.Count;i++)
        {
            int j=(i+1)%path.Count;var a=path[i];var b=path[j];
            if(!(Concave(path,i)||Concave(path,j)) || Length(a,b)>=2*size)continue;
            var axis=a.Y==b.Y?moveX:moveY;axis.Add(a.Y==b.Y?a.X:a.Y);axis.Add(a.Y==b.Y?b.X:b.Y);
        }
        int Snap(int value,int origin)=>2*(origin+size*(int)Math.Floor(((value+1)/2.0-origin)/size+0.5))-1;
        return Simplify(path.Select(p=>(moveX.Contains(p.X)?Snap(p.X,ox):p.X,moveY.Contains(p.Y)?Snap(p.Y,oy):p.Y)).ToList());
    }

    private static HashSet<(int X,int Y)> Raster(IEnumerable<List<(int X,int Y)>> paths)
    {
        var polygons=paths.Where(p=>p.Count>=4).ToArray();var result=new HashSet<(int X,int Y)>();
        if(polygons.Length==0)return result;
        int x0=polygons.Min(p=>p.Min(v=>v.X)),x1=polygons.Max(p=>p.Max(v=>v.X));
        int y0=polygons.Min(p=>p.Min(v=>v.Y)),y1=polygons.Max(p=>p.Max(v=>v.Y));
        for(int x=(x0+1)/2;x<(x1+1)/2;x++)for(int y=(y0+1)/2;y<(y1+1)/2;y++)
        {
            bool inside=false;
            foreach(var path in polygons)for(int i=0;i<path.Count;i++)
            {
                var a=path[i];var b=path[(i+path.Count-1)%path.Count];
                if((a.Y>2*y)!=(b.Y>2*y) && 2*x<(b.X-a.X)*(2.0*y-a.Y)/(b.Y-a.Y)+a.X)inside=!inside;
            }
            if(inside)result.Add((x,y));
        }
        return result;
    }

    private static void Join(HashSet<(int X,int Y)> cells,HashSet<(int X,int Y)> source,int size,int ox,int oy)
    {
        // A rounded vertex can separate a narrow existing connection. Restore
        // that connection with a complete shoulder-width bank, not a tiny bridge.
        var groups=Components(cells);
        while(groups.Count>1)
        {
            var pair=(from a in groups[0] from b in groups.Skip(1).SelectMany(g=>g)
                      orderby Length(a,b),a.X,a.Y,b.X,b.Y select (a,b)).First();
            var p=pair.a;
            while(p!=pair.b)
            {
                var options=Neighbours.Select(d=>(X:p.X+d.X,Y:p.Y+d.Y)).Where(n=>Length(n,pair.b)<Length(p,pair.b));
                p=options.OrderByDescending(n=>source.Contains(n)).ThenBy(n=>n.X).ThenBy(n=>n.Y).First();
                int x=ox+size*(int)Math.Floor((p.X-ox)/(double)size),y=oy+size*(int)Math.Floor((p.Y-oy)/(double)size);
                for(int i=0;i<size;i++)for(int j=0;j<size;j++)cells.Add((x+i,y+j));
            }
            groups=Components(cells);
        }
    }
}
