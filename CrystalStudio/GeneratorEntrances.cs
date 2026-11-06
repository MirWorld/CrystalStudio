using System.IO;

namespace CrystalStudio;

public sealed record GeneratedEntrance(int X, int Y, string PieceId);

internal static class GeneratorEntrances
{
    internal static bool SupportsDungeon(WallFamily family) => family.SupportsDoors || family.Id=="stone-temple";

    internal static IReadOnlyList<GeneratedEntrance> Locations(WallPlan plan) => plan.Replacements.SelectMany(r=>
        r.SpriteDepths?.Values.Where(d=>plan.Family.Entrances.Any(e=>e.Piece.Id==d.Id))
            .Select(d=>new GeneratedEntrance(r.X+d.X,r.Y+d.Y,d.Id)) ?? [])
        .Distinct().OrderBy(p=>p.Y).ThenBy(p=>p.X).ToArray();

    internal static IReadOnlyList<GeneratedEntrance> AddToCliffs(MapDocument map, WallFamily family, int count, int seed, AssetStore? assets=null)
    {
        if (count==0 || !family.SupportsDoors) return [];
        family=family.WithMode(false);
        family.RequestedDoorCount=count;
        family.DoorCountPerMap=true;
        if (assets!=null && family.Availability(assets) is { } error) throw new InvalidDataException(error);
        var anchors=map.PlacedStamps.Where(s=>s.Wall?.FamilyId==family.Id).Select(s=>(s.X,s.Y)).ToArray();
        if (anchors.Length==0) return [];
        var plan=WallBrush.PlanStroke(map,family,anchors,variationSeed:seed);
        if (!plan.Allowed) throw new InvalidDataException(plan.Error);
        if (plan.HasChanges && !map.ApplyWallPlan(plan,new(),(_,_)=>{}))
            throw new InvalidDataException("The generated cliff entrances could not be placed.");
        return Locations(plan);
    }

    internal static IReadOnlyList<GeneratedEntrance> AddStoneStairs(MapDocument map, int count, int seed,
        AssetStore? assets, CancellationToken cancellation, IReadOnlyList<(int X,int Y)> centres)
    {
        if (count==0) return [];
        // D711 uses these complete native floor stair objects, away from its
        // wall faces: 377 descends and 381 ascends. They never replace walls.
        var pieces=new[]{377,381}.Select(n=>(Id:$"mir/mir_{n:00000}",
            Cells:CellCodec.LoadStamp(DataPaths.ResolveEditor("Objects","mir",$"mir_{n:00000}.X"), assets?.Format ?? AssetSourceFormat.Default))).ToArray();
        if (assets!=null)
        foreach (var front in pieces.SelectMany(p=>p.Cells).SelectMany(c=>c.Cell.ForegroundImages).Distinct())
            if (!assets.Libraries.TryGetValue(front.Library,out var library) || front.Image>library.Count || library.Decode(front.Image-1)==null)
                throw new InvalidDataException("StoneTemple stair artwork is unavailable in the selected libraries.");
        var random=new Random(seed ^ 0x570AE);
        var candidates=new List<(int X,int Y,int Piece,int Rank)>();
        for (int y=4;y<map.Height-4;y+=4)
        {
            cancellation.ThrowIfCancellationRequested();
            for (int x=4;x<map.Width-4;x+=4)
            {
                int piece=random.Next(pieces.Length);
                var cells=pieces[piece].Cells;
                int left=x+cells.Min(c=>c.X)-3,right=x+cells.Max(c=>c.X)+3;
                int top=y+cells.Min(c=>c.Y)-3,bottom=y+cells.Max(c=>c.Y)+3;
                if (centres.Any(p=>p.X>=left && p.X<=right && p.Y>=top && p.Y<=bottom)) continue;
                bool fits=true;
                // Reserve a walkable approach on every side; stairs cannot
                // block a narrow corridor or overwrite native wall artwork.
                for (int yy=top;fits && yy<=bottom;yy++)
                for (int xx=left;xx<=right;xx++)
                    if (!map.Contains(xx,yy) || (map.Get(xx,yy).BackImage&0x60000000)!=0 || map.Get(xx,yy).ForegroundImages.Count>0)
                    { fits=false; break; }
                if (fits) candidates.Add((x,y,piece,random.Next()));
            }
        }
        var selected=new List<GeneratedEntrance>();
        const int spacing=20;
        while (selected.Count<count && candidates.Count>0)
        {
            cancellation.ThrowIfCancellationRequested();
            var next=candidates.OrderByDescending(c=>selected.Count==0 ? 0 : selected.Min(s=>Distance(c.X,c.Y,s.X,s.Y)))
                .ThenBy(c=>c.Rank).First();
            var piece=pieces[next.Piece];
            if (map.PlaceStamp(piece.Cells,next.X,next.Y,"Generated entrance · StoneTemple stairs",new(),(_,_)=>{})==null)
                throw new InvalidDataException("The complete native stairs could not be placed.");
            selected.Add(new(next.X,next.Y,piece.Id));
            candidates.RemoveAll(c=>Distance(c.X,c.Y,next.X,next.Y)<spacing*spacing);
        }
        return selected;
    }

    private static long Distance(int x,int y,int a,int b) => (long)(x-a)*(x-a)+(long)(y-b)*(y-b);
}
