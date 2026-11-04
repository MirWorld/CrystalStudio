namespace CrystalStudio;

/// <summary>A native atlas step, including an exact-pixel centre alias when needed.</summary>
public sealed record TerrainSurfaceStep(TerrainSet Previous,TerrainSet Background,TerrainSet Overlay)
{
    public bool UsesCentreAlias=>Previous.Id!=Background.Id;
}

/// <summary>Routes through reviewed blends and byte-identical centre surfaces.</summary>
public static class TerrainSurfaceRoute
{
    public static bool SameCentres(TerrainSet a,TerrainSet b)=>a.Shapes[15].Select(v=>v.PixelSha256).Order(StringComparer.Ordinal)
        .SequenceEqual(b.Shapes[15].Select(v=>v.PixelSha256).Order(StringComparer.Ordinal));

    public static IReadOnlyList<TerrainSurfaceStep>? Find(TerrainCatalogue catalogue,TerrainSet basis,TerrainSet target)
    {
        if(!catalogue.Sets.Contains(basis)||!catalogue.Sets.Contains(target))throw new ArgumentException("Choose catalogue surfaces for native transition routing.");
        if(SameCentres(basis,target))return [];
        var sets=catalogue.Sets.ToDictionary(s=>s.Id);var queue=new Queue<(TerrainSet Surface,List<TerrainSurfaceStep> Steps)>();
        var visited=new HashSet<string>(StringComparer.Ordinal);
        string Key(TerrainSet set)=>string.Join(',',set.Shapes[15].Select(v=>v.PixelSha256).Order(StringComparer.Ordinal));
        queue.Enqueue((basis,[]));visited.Add(Key(basis));
        while(queue.TryDequeue(out var current))
        {
            foreach(var blend in catalogue.Blends.Where(b=>b.Status=="reviewed"&&SameCentres(current.Surface,sets[b.BaseId]))
                .OrderBy(b=>b.OverlayId,StringComparer.Ordinal).ThenBy(b=>b.BaseId,StringComparer.Ordinal))
            {
                var overlay=sets[blend.OverlayId];if(!visited.Add(Key(overlay)))continue;
                var steps=new List<TerrainSurfaceStep>(current.Steps){new(current.Surface,sets[blend.BaseId],overlay)};
                if(SameCentres(overlay,target))return steps;
                queue.Enqueue((overlay,steps));
            }
        }
        return null;
    }
}
