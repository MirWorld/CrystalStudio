using System.IO;
using System.Text.Json;

namespace CrystalStudio;

/// <summary>Native shore sprites projected onto cells, with measured neighbour joins.</summary>
public sealed class TerrainWaterCatalogue
{
    public int SchemaVersion { get; set; }
    public string Status { get; set; } = "";
    public string Name { get; set; } = "";
    public string ReviewBasis { get; set; } = "";
    public string LandId { get; set; } = "";
    public string WaterId { get; set; } = "";
    public List<WaterState> States { get; set; } = [];
    public List<List<WaterJoin>> Joins { get; set; } = [];
    public List<WaterSprite> Sprites { get; set; } = [];
    public List<WaterSource> Sources { get; set; } = [];
    public List<WaterStrip> Strips { get; set; } = [];
    public List<WaterStripJoin> StripJoins { get; set; } = [];
    public static TerrainWaterCatalogue Load(string path, AssetSourceFormat format = AssetSourceFormat.Default)
    {
        var result = StudioJson.Deserialize<TerrainWaterCatalogue>(File.ReadAllText(path), new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? throw new InvalidDataException("Empty water catalogue.");
        if (result.SchemaVersion != 1 || result.Status != "reviewed" || string.IsNullOrWhiteSpace(result.ReviewBasis) || result.Sources.Count < 2 || result.Sources.Any(s => s.Sha256.Length != 64) || result.States.Count is < 3 or > 4096 || result.Joins.Count != 4) throw new InvalidDataException("Invalid reviewed water catalogue.");
        for (int i = 0; i < result.States.Count; i++)
        {
            var s = result.States[i];
                if (s.Id != i || s.Weight <= 0 || s.Phases is < 1 or > 15 || s.Fragments.Any(f => f.Dx != 0 || f.Dy is < 0 or > 16 || !result.Sprites.Any(p => p.Library == f.Library && p.Image == f.Image && p.OccupiedRows.Contains(f.Dy)))) throw new InvalidDataException("Invalid shore cell or sprite fragment.");
        }
        if (result.States[0].Water || !result.States[1].Water || result.States.Take(2).Any(s => s.Fragments.Count != 0) || result.Sprites.Any(s => s.Width != 48 || s.Height is < 1 or > 544 || s.PixelSha256.Length != 64 || s.Image <= 0 || s.Image > short.MaxValue)) throw new InvalidDataException("Invalid shore artwork dimensions or identities.");
        foreach (var joins in result.Joins)
            if (joins.Any(j => j.A < 0 || j.B < 0 || j.A >= result.States.Count || j.B >= result.States.Count || j.Count <= 0 || j.A < 2 && j.B < 2 && j.A != j.B || !double.IsFinite(j.PixelCheck.MeanDistance) || j.PixelCheck.MeanDistance > 36 || j.PixelCheck.LongestGap > 12 || (j.A > 1 || j.B > 1) && (j.Example == null || !result.Sources.Any(s => s.FileName == j.Example.FileName)))) throw new InvalidDataException("Invalid water join, pixel seam or native evidence. Bare land/water edges are forbidden.");
        var allowed = result.Joins.Select(js => js.Select(j => (j.A,j.B)).ToHashSet()).ToArray();
        for (int i=0;i<result.Strips.Count;i++)
        {
            var strip=result.Strips[i];
            if(strip.Id!=i || strip.Phase is < 0 or > 1 || strip.StartPhase is < 0 or > 1 || strip.LeftWater==strip.RightWater || strip.Weight<=0 || strip.Cells.Length is < 1 or > 24 || strip.Cells.Any(s=>s<0||s>=result.States.Count)) throw new InvalidDataException("Invalid native bank cross-section.");
            var row=new[]{strip.LeftWater?1:0}.Concat(strip.Cells).Append(strip.RightWater?1:0).ToArray();
            if(row.Zip(row.Skip(1)).Any(p=>!allowed[strip.Vertical?0:1].Contains(p))) throw new InvalidDataException("A bank cross-section contains an unreviewed seam.");
        }
        foreach(var join in result.StripJoins)
        {
            if(join.A<0||join.B<0||join.A>=result.Strips.Count||join.B>=result.Strips.Count||join.Count<=0||Math.Abs(join.Shift)>12||join.Example==null||!result.Sources.Any(s=>s.FileName==join.Example.FileName)) throw new InvalidDataException("Invalid native bank cross-section evidence.");
            var a=result.Strips[join.A];var b=result.Strips[join.B];
            if(a.Vertical!=b.Vertical||a.LeftWater!=b.LeftWater||a.Phase==b.Phase||((a.StartPhase+join.Shift)&1)!=b.StartPhase) throw new InvalidDataException("Bank cross-section phases disagree.");
            int At(WaterStrip s,int start,int p)=>p<start?(s.LeftWater?1:0):p>=start+s.Cells.Length?(s.RightWater?1:0):s.Cells[p-start];
            for(int p=Math.Min(0,join.Shift)-1;p<=Math.Max(a.Cells.Length,join.Shift+b.Cells.Length);p++)
                if(!allowed[a.Vertical?1:0].Contains((At(a,0,p),At(b,join.Shift,p))))throw new InvalidDataException("A bank cross-section shift contains an unreviewed seam.");
        }
        if (format == AssetSourceFormat.Sanxiu)
        {
            foreach (var sprite in result.Sprites)
                sprite.Library = AssetLibraryIds.Resolve(sprite.Library, MapLayer.Objects, format);
            foreach (var fragment in result.States.SelectMany(state => state.Fragments))
                fragment.Library = AssetLibraryIds.Resolve(fragment.Library, MapLayer.Objects, format);
        }
        return result;
    }
    public IReadOnlyList<TerrainSet>? LandRoute(TerrainCatalogue terrain, TerrainSet basis)
    {
        var land = terrain.Sets.Single(s => s.Id == LandId);
        bool SameCentres(TerrainSet other) => other.Shapes[15].Select(v => v.PixelSha256).Order().SequenceEqual(land.Shapes[15].Select(v => v.PixelSha256).Order());
        if (SameCentres(basis)) return [];
        // A centre alias is allowed only when the actual pixels are identical.
        // In particular dirt-on-grass centres match the plain dirt shore base.
        return terrain.Sets.Where(SameCentres).Select(s => terrain.GenerationLayers(basis, s)).Where(r => r != null).OrderBy(r => r!.Count).FirstOrDefault();
    }
    public bool MatchesAssets(AssetStore assets)
    {
        foreach (var s in Sprites)
        {
            if (!assets.Libraries.TryGetValue(s.Library, out var library)) return false;
            var sprite = library.Decode(s.Image - 1);
            if (sprite == null || sprite.Width != s.Width || sprite.Height != s.Height) return false;
        }
        return true;
    }
}
public sealed class WaterState { public int GroundIndex { get; set; } = -1; public int GroundKey => GroundIndex >= 0 ? GroundIndex : Water ? 1800 : 0; public int Id { get; set; } public bool Water { get; set; } public int Phases { get; set; } public long Weight { get; set; } public List<WaterFragment> Fragments { get; set; } = []; }
public sealed record WaterFragment { public int Library { get; set; } public int Image { get; set; } public int Dx { get; set; } public int Dy { get; set; } }
public sealed class WaterJoin { public int Phases { get; set; } = 15; public int A { get; set; } public int B { get; set; } public long Count { get; set; } public WaterExample? Example { get; set; } public WaterPixelCheck PixelCheck { get; set; } = new(); }
public sealed class WaterPixelCheck { public double MeanDistance { get; set; } public int LongestGap { get; set; } }
public sealed class WaterStrip { public int Id { get; set; } public bool Vertical { get; set; } public int Phase { get; set; } public int StartPhase { get; set; } public bool LeftWater { get; set; } public bool RightWater { get; set; } public int[] Cells { get; set; } = []; public long Weight { get; set; } }
public sealed class WaterStripJoin { public int A { get; set; } public int B { get; set; } public int Shift { get; set; } public long Count { get; set; } public WaterExample? Example { get; set; } }
public sealed class WaterExample { public string FileName { get; set; } = ""; public int X { get; set; } public int Y { get; set; } }
public sealed class WaterSource { public string FileName { get; set; } = ""; public string Title { get; set; } = ""; public string Sha256 { get; set; } = ""; }
public sealed class WaterSprite { public int Library { get; set; } public int Image { get; set; } public int Width { get; set; } public int Height { get; set; } public List<int> OccupiedRows { get; set; } = []; public string PixelSha256 { get; set; } = ""; }
