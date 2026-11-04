namespace CrystalStudio;

// Offsets describe native doorway objects relative to the first straight card
// they replace. Wider entrances consume several complete cards, never corners.
public sealed record WallEntrance(string Role, int Length, int DX, int DY, int X, int Y, WallPiece Piece);

internal static class WallEntrances
{
    internal const int MaximumCount = 32;
    private sealed record Slot((int X,int Y) Owner, WallPart Part, string Role, int X, int Y);
    private sealed record Candidate(WallEntrance Entrance, Slot[] Slots, int X, int Y, uint Rank);

    internal static Dictionary<(int X,int Y),(int Count,int Seed)> Prepare(MapDocument document, WallFamily family,
        Dictionary<(int X,int Y),List<WallPart>> parts, HashSet<(int X,int Y)> occupied,
        Dictionary<(int X,int Y),PlacedStamp> existing, IReadOnlyList<(int X,int Y)> requested, bool erase, int? seed)
    {
        var settings = new Dictionary<(int X,int Y),(int Count,int Seed)>();
        var components = WallSeparatePlacement.Components(family,occupied);
        var touched = requested.Concat(requested.SelectMany(p=>family.Directions.Select(d=>(p.X+d.X,p.Y+d.Y))))
            .Where(components.ContainsKey).Select(p=>components[p]).ToHashSet();
        var roles = family.Pieces.ToDictionary(p=>p.Id,p=>p.Role);
        var entranceRoles = family.Entrances.Select(e=>e.Role).ToHashSet();
        foreach (var group in occupied.GroupBy(p=>family.DoorCountPerMap ? 0 : components[p]))
        {
            var previous = group.Where(existing.ContainsKey).Select(p=>existing[p].Wall!).Where(n=>n.DoorCount>0)
                .OrderBy(n=>n.DoorSeed).FirstOrDefault();
            int count = Math.Clamp(!erase && (family.DoorCountPerMap || touched.Contains(group.Key)) && family.RequestedDoorCount is int requestedCount
                ? requestedCount : previous?.DoorCount ?? 0,0,MaximumCount);
            int randomSeed = count==0 ? 0 : previous?.DoorSeed ?? seed ?? family.DoorRandomSeed;
            foreach (var p in group) settings[p]=(count,randomSeed);
            if (count==0) continue;
            var slots = group.SelectMany(p=>parts[p].Where(part=>part.CellSelection==null && part.FrontSelection==null &&
                    part.FrontColumnSelection==null && part.Attachment==null && entranceRoles.Contains(roles[part.PieceId]))
                .Select(part=>new Slot(p,part,roles[part.PieceId],p.X+part.X,p.Y+part.Y))).ToArray();
            var byPosition = slots.GroupBy(s=>(s.Role,s.X,s.Y)).ToDictionary(g=>g.Key,g=>g.ToArray());
            var candidates = new List<Candidate>();
            foreach (var entrance in family.Entrances)
            foreach (var slot in byPosition.Values.Select(s=>s[0]).Where(s=>s.Role==entrance.Role))
            {
                // A complete regular card at either end separates the opening
                // from shoulders, intersections, trimmed returns and map edges.
                var run = Enumerable.Range(-1,entrance.Length+2).Select(i=>
                    byPosition.GetValueOrDefault((slot.Role,slot.X+i*entrance.DX,slot.Y+i*entrance.DY))).ToArray();
                if (run.Any(s=>s==null)) continue;
                int x=slot.X+entrance.X,y=slot.Y+entrance.Y;
                if (entrance.Piece.Cells.Any(c=>!document.Contains(x+c.X,y+c.Y))) continue;
                // Neighbouring sections can share the same complete native
                // card. Replace every owner so a retained duplicate cannot
                // paint the solid wall back over its entrance.
                candidates.Add(new(entrance,run.Skip(1).Take(entrance.Length).SelectMany(s=>s!)
                    .OrderBy(s=>s.Owner.Y).ThenBy(s=>s.Owner.X).ThenBy(s=>s.Y).ThenBy(s=>s.X).ToArray(),x,y,
                    Rank(randomSeed,x,y,entrance.Piece.Id)));
            }
            var selected = new List<Candidate>();
            while (selected.Count<count && candidates.Count>0)
            {
                // Random first entrance; then maximize the distance from the
                // nearest chosen entrance, with seeded random tie breaking.
                var next = candidates.OrderByDescending(c=>selected.Count==0 ? 0 : selected.Min(s=>Distance(c,s)))
                    .ThenBy(c=>c.Rank).ThenBy(c=>c.Y).ThenBy(c=>c.X).First();
                selected.Add(next);
                candidates.RemoveAll(c=>c.Slots.Intersect(next.Slots).Any() ||
                    Distance(c,next)<Math.Pow(Math.Max(2*family.Step,Radius(c)+Radius(next)+family.Step),2));
            }
            foreach (var choice in selected)
            {
                foreach (var slot in choice.Slots)
                {
                    parts[slot.Owner].Remove(slot.Part);
                    parts[slot.Owner].RemoveAll(p=>p.Attachment is { } a && a.PieceId==slot.Part.PieceId &&
                        a.X==slot.Part.X && a.Y==slot.Part.Y);
                }
                var owner=choice.Slots[0].Owner;
                parts[owner].Add(new(choice.Entrance.Piece.Id,choice.X-owner.X,choice.Y-owner.Y));
            }
            if (family.DoorCountPerMap)
            {
                // Save the allocation on each component so later manual wall
                // edits preserve it without multiplying the generator's total.
                var counts=selected.GroupBy(c=>components[c.Slots[0].Owner]).ToDictionary(g=>g.Key,g=>g.Count());
                foreach (var p in group) settings[p]=(counts.GetValueOrDefault(components[p]),randomSeed);
            }
        }
        return settings;
    }

    private static double Distance(Candidate a,Candidate b) =>
        (double)(a.X-b.X)*(a.X-b.X)+(double)(a.Y-b.Y)*(a.Y-b.Y);
    private static double Radius(Candidate c) => c.Entrance.Length*Math.Sqrt(
        c.Entrance.DX*c.Entrance.DX+c.Entrance.DY*c.Entrance.DY)/2;
    private static uint Rank(int seed,int x,int y,string id)
    {
        uint hash=unchecked((uint)seed ^ (uint)x*2246822519u ^ (uint)y*3266489917u);
        foreach (char c in id) hash=unchecked((hash^c)*16777619u);
        hash^=hash>>16;hash=unchecked(hash*0x7feb352d);return hash^(hash>>15);
    }
}
