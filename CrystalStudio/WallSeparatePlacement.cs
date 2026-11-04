namespace CrystalStudio;

internal static class WallSeparatePlacement
{
    internal static Dictionary<(int X,int Y),int> Components(WallFamily family,HashSet<(int X,int Y)> points)
    {
        var result = new Dictionary<(int X,int Y),int>();
        var queue = new Queue<(int X,int Y)>();
        int component = 0;
        foreach (var point in points)
        {
            if (result.ContainsKey(point)) continue;
            result[point] = component;
            queue.Enqueue(point);
            while (queue.TryDequeue(out var current))
                foreach (var direction in family.Directions)
                {
                    var next = (current.X+direction.X,current.Y+direction.Y);
                    if (points.Contains(next) && result.TryAdd(next,component)) queue.Enqueue(next);
                }
            component++;
        }
        return result;
    }

    private readonly record struct Span(int Y,int End,ulong Mask,int Component);

    internal static bool Overlaps(MapDocument document,WallFamily family,Dictionary<(int X,int Y),int> components,
        List<WallReplacement> replacements,HashSet<int> growing)
    {
        if (growing.Count==0 || components.Values.Distinct().Take(2).Count()<2) return false;
        var footprints = family.SpriteFootprints.ToDictionary(p=>(p.Library,p.Image));
        var columns = new Dictionary<int,List<Span>>();
        // Check the complete native layouts before cell ownership resolves
        // competing sprites. Otherwise that resolution hides the collision by
        // silently deleting part of one of the two disconnected walls.
        foreach (var replacement in replacements)
        {
            int component = components[(replacement.X,replacement.Y)];
            foreach (var cell in replacement.Cells)
            foreach (var front in cell.Cell.ForegroundImages)
            {
                // Cells clipped by the map edge never become placed sprites.
                if (!document.Contains(replacement.X+cell.X,replacement.Y+cell.Y)) continue;
                if (!footprints.TryGetValue((front.Library,front.Image&0x7fff),out var footprint)) continue;
                foreach (var span in footprint.Spans)
                {
                    int column = replacement.X+cell.X+span.Column;
                    int y = (replacement.Y+cell.Y)*32+span.Y;
                    int end = Math.Min(document.Height*32,y+span.Height);
                    y = Math.Max(0,y);
                    if (column<0 || column>=document.Width || y>=end) continue;
                    if (!columns.TryGetValue(column,out var spans)) columns[column]=spans=[];
                    spans.Add(new(y,end,span.Mask,component));
                }
            }
        }
        foreach (var spans in columns.Values)
        {
            spans.Sort((a,b)=>a.Y.CompareTo(b.Y));
            for (int i=0;i<spans.Count;i++)
            {
                var a=spans[i];
                for (int j=i+1;j<spans.Count && spans[j].Y<a.End;j++)
                {
                    var b=spans[j];
                    if (a.Component!=b.Component && (growing.Contains(a.Component) || growing.Contains(b.Component)) &&
                        (a.Mask&b.Mask)!=0) return true;
                }
            }
        }
        return false;
    }
}
