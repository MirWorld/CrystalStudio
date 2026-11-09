using System.IO;

namespace CrystalStudio;

public readonly record struct TerrainArea(int X, int Y, int Width, int Height);
public sealed record TerrainOptions(TerrainSet Base, TerrainSet? Patches, TerrainSet? Path, int Seed = 1, double Coverage = .35, double PatchScale = 12, double PathWidth = 3, int PathCount = 1, TerrainWaterMode WaterMode = TerrainWaterMode.None, double WaterWidth = 18, double WaterCoverage = .4)
{
    public int AlgorithmVersion { get; init; } = GenerationAlgorithms.Legacy;
    internal MapDesignSettings? Design { get; init; }
    public bool BridgeCrossing { get; init; }
    public bool Island { get; init; }
    // Provinces leave winding clearings in their native dirt base.
    public bool KeepPathsOnBase { get; init; }
    public bool CompactBeach { get; init; }
}
public sealed record TerrainTile(int X, int Y, int Library, int Index, string SetId, int Mask, string BackgroundId);
public sealed record TerrainPlan(MapDocument Source, TerrainArea Area, IReadOnlyList<TerrainTile> Tiles, int Seed, TerrainWaterPlan? Water = null)
{
    public IReadOnlyList<TerrainCoastPlacement> CoastObjects { get; init; } = [];
    public IReadOnlyList<string> Notes { get; init; } = [];
    // Actual route footprint, independent of materials also used by patches.
    public IReadOnlyList<(int X, int Y)> PathCells { get; init; } = [];
    internal long SourceRevision { get; init; } = Source.PickupRevision;
}

/// <summary>Independent terrain generator. Brush selection and painting are unaffected.</summary>
public static class TerrainGenerator
{
    public static TerrainArea AlignArea(MapDocument document, TerrainArea requested)
    {
        if (requested.Width <= 0 || requested.Height <= 0 || requested.X < 0 || requested.Y < 0 || (long)requested.X + requested.Width > document.Width || (long)requested.Y + requested.Height > document.Height) throw new ArgumentException("Terrain area must be inside the map.");
        int left = (requested.X + 1) & ~1, top = (requested.Y + 1) & ~1;
        // Odd final map cells are valid anchors; partial selection tiles are excluded.
        int right = requested.X + requested.Width, bottom = requested.Y + requested.Height;
        if (right < document.Width) right &= ~1;
        if (bottom < document.Height) bottom &= ~1;
        return new(left, top, Math.Max(0, right - left), Math.Max(0, bottom - top));
    }
    public static TerrainPlan Plan(MapDocument document, TerrainArea requested, TerrainCatalogue catalogue, TerrainOptions options, CancellationToken cancellationToken = default, IReadOnlyList<RiverCoastLayout>? coastLayouts = null, RiverCoastPieces? coastPieces = null, RiverShoreComponents? shoreComponents = null,IslandCoastObjects? islandCoast = null)
    {
        GenerationAlgorithms.Validate(options.AlgorithmVersion);
        var source = document; long sourceRevision = source.PickupRevision;
        var design = GenerationAlgorithms.UsesConstraints(options.AlgorithmVersion) ? options.Design : null;
        if (design != null) GenerationDiagnostics.ValidateInputs(document, design);
        var area = AlignArea(document, requested);
        int width = (area.Width + 1) / 2, height = (area.Height + 1) / 2, stride = width + 1;
        if (width < 3 || height < 3) throw new ArgumentException("Select at least 6×6 cells for terrain generation.");
        if ((long)width * height > MapDimensions.MaximumGroundTiles) throw new ArgumentException("Generate at most 300,000 ground tiles at once. Select a smaller area.");
        if (!catalogue.Sets.Contains(options.Base) || !options.Base.Base || options.Patches != null && (!catalogue.Sets.Contains(options.Patches) || !catalogue.CanGenerate(options.Base, options.Patches)) || options.Path != null && (!catalogue.Sets.Contains(options.Path) || !catalogue.CanGenerate(options.Base, options.Path))) throw new ArgumentException("Choose a reviewed base and compatible terrain transitions.");
        if (!double.IsFinite(options.Coverage) || options.Coverage is < 0 or > 1 || !double.IsFinite(options.PatchScale) || options.PatchScale is < 2 or > 100 || !double.IsFinite(options.PathWidth) || options.PathWidth is < 1 or > 20 || options.PathCount is < 1 or > 3) throw new ArgumentException("Invalid terrain generation settings.");
        if(options.BridgeCrossing&&options.WaterMode is not (TerrainWaterMode.River or TerrainWaterMode.Waterways or TerrainWaterMode.RiverToSea))
            throw new ArgumentException("A bridge crossing needs a river.");
        bool savedShore = options.WaterMode == TerrainWaterMode.Beach && shoreComponents != null;
        bool savedRiver = options.WaterMode is TerrainWaterMode.River or TerrainWaterMode.Waterways or TerrainWaterMode.RiverToSea;
        if (savedRiver && coastLayouts == null && coastPieces == null) throw GenerationDiagnostics.MissingArgument("Verified reusable .X bank pieces are unavailable. Load the coast catalogue before generating a river.");
        document.CheckTerrainBankBoundary(area);
        if (TerrainCoastObjects.Replaced(document, area).Any() || savedRiver || savedShore || options.Island)
            document = TerrainCoastObjects.WorkingCopy(document, area);
        CheckBoundary(document, area, options.Base);
        cancellationToken.ThrowIfCancellationRequested();
        TerrainWaterPlan? waterPlan = null;
        TerrainCoastPlacement? coast = null;
        var notes=new List<string>();
        TerrainSet[] waterRoute = [];
        var islandAliases=new HashSet<(string,string)>();
        if(options.Island)
        {
            if(area!=new TerrainArea(0,0,document.Width,document.Height))throw new ArgumentException("Island generation uses the whole map.");
            if(islandCoast==null)throw GenerationDiagnostics.MissingArgument("The native 5.map island coast artwork is not available.");
            var sand=catalogue.Sets.Single(s=>s.Id=="0:0:2300");
            var steps=TerrainSurfaceRoute.Find(catalogue,options.Base,sand)??throw new ArgumentException("Choose an island base with a compatible sand transition.");
            waterRoute=steps.Select(s=>s.Overlay).ToArray();
            foreach(var step in steps.Where(s=>s.UsesCentreAlias))islandAliases.Add((step.Previous.Id,step.Overlay.Id));
            coast=islandCoast.Plan(document,options.Seed,cancellationToken);
        }
        else if (options.WaterMode != TerrainWaterMode.None)
        {
            if (!Enum.IsDefined(options.WaterMode) || !double.IsFinite(options.WaterWidth) || options.WaterWidth is < 12 or > 48 || !double.IsFinite(options.WaterCoverage) || options.WaterCoverage is < .2 or > .7 || catalogue.Water == null || catalogue.Water.LandRoute(catalogue, options.Base) is not { } route) throw new ArgumentException("Choose a base with reviewed shore transitions and valid water settings.");
            waterRoute = route.ToArray();
            if (savedShore || savedRiver && coastPieces != null)
            {
                var waterCandidates=new List<TerrainOptions>{options};
                if(options.BridgeCrossing)waterCandidates.Add(options with{BridgeCrossing=false});
                if(options.WaterMode==TerrainWaterMode.RiverToSea)
                {
                    waterCandidates.Add(options with{WaterMode=TerrainWaterMode.Beach,BridgeCrossing=false});
                    waterCandidates.Add(options with{WaterMode=TerrainWaterMode.River,BridgeCrossing=false});
                }
                string failure="";
                foreach(var candidate in waterCandidates)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    try
                    {
                        var candidatePieces=!candidate.BridgeCrossing&&options.BridgeCrossing&&shoreComponents!=null
                            ?shoreComponents.Banks:coastPieces;
                        var proposed=candidate.WaterMode==TerrainWaterMode.RiverToSea&&shoreComponents!=null
                            ?TerrainCoastObjects.PlanRiverAndBeach(document,area,candidate,candidatePieces!,shoreComponents,cancellationToken)
                            :candidate.WaterMode==TerrainWaterMode.Beach&&shoreComponents!=null
                            ?TerrainCoastObjects.PlanShorePieces(document,area,candidate,shoreComponents,cancellationToken)
                            :TerrainCoastObjects.PlanPieces(document,area,candidate,candidatePieces!,cancellationToken);
                        TerrainCoastObjects.VerifyAccess(document,proposed,cancellationToken);
                        coast=proposed;
                        if(candidate.BridgeCrossing!=options.BridgeCrossing)notes.Add("The native bridge does not fit this layout and was omitted.");
                        if(candidate.WaterMode!=options.WaterMode)notes.Add(candidate.WaterMode==TerrainWaterMode.Beach
                            ?"Only the beach fits this area; there is not enough clear land for a complete native river."
                            :"Only the river fits this area; a complete native beach needs more space.");
                        break;
                    }
                    catch(ArgumentException error){failure=error.Message;}
                }
                if(coast==null)
                {
                    waterRoute=[];
                    notes.Add("Water could not fit safely in this area; generated ground instead. "+failure);
                }
            }
            else if (savedRiver)
            {
                coast = coastPieces != null
                    ? TerrainCoastObjects.PlanPieces(document, area, options, coastPieces, cancellationToken)
                    : TerrainCoastObjects.Plan(document, area, options, coastLayouts!, cancellationToken);
                TerrainCoastObjects.VerifyAccess(document, coast, cancellationToken);
            }
            else waterPlan = TerrainWaterGenerator.Plan(document,area,catalogue.Water,options,cancellationToken);
        }
        var pathRoute = options.Path == null ? Array.Empty<TerrainSet>() : catalogue.GenerationLayers(options.Base, options.Path)!.ToArray();
        var patchRoute = options.Patches == null ? Array.Empty<TerrainSet>() : catalogue.GenerationLayers(options.Base, options.Patches)!.ToArray();
        int length = stride * (height + 1);
        bool Interior(int x, int y, int margin) => (area.X == 0 || x >= margin) && (area.Y == 0 || y >= margin) && (area.X + area.Width == document.Width || x <= width - margin) && (area.Y + area.Height == document.Height || y <= height - margin);
        int[] Corners(int x, int y) => [y * stride + x, y * stride + x + 1, (y + 1) * stride + x, (y + 1) * stride + x + 1];
        void Normalize(bool[] field, bool fill, int margin)
        {
            if (GenerationAlgorithms.UsesConstraints(options.AlgorithmVersion))
            {
                TerrainFieldNormalizer.Normalize(field, width, height, fill, (x, y) => Interior(x, y, margin), options.Seed, cancellationToken);
                return;
            }
            // Monotone updates resolve unsupported disconnected diagonal shapes.
            // Path coverage fills tips; noise patches trim tips. All nested fields
            // are resolved before tile selection, preserving shared vertices.
            var pending = new Queue<(int X, int Y)>();
            for (int y = 0; y < height; y++) for (int x = 0; x < width; x++) pending.Enqueue((x, y));
            while (pending.TryDequeue(out var t))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var keys = Corners(t.X, t.Y); int mask = 0;
                for (int k = 0; k < 4; k++) if (field[keys[k]]) mask |= 1 << k;
                if (mask is not (6 or 9)) continue;
                bool value = fill && keys.All(k => Interior(k % stride, k / stride, margin));
                foreach (int k in keys)
                {
                    if (field[k] == value) continue;
                    field[k] = value; int vx = k % stride, vy = k / stride;
                    for (int oy = -1; oy <= 0; oy++) for (int ox = -1; ox <= 0; ox++)
                        if (vx + ox >= 0 && vx + ox < width && vy + oy >= 0 && vy + oy < height) pending.Enqueue((vx + ox, vy + oy));
                }
            }
        }
        bool[] Expand(bool[] field)
        {
            var expanded = new bool[length];
            for (int y = 0; y <= height; y++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                for (int x = 0; x <= width; x++)
                    if (field[y * stride + x])
                        for (int oy = -2; oy <= 2; oy++) for (int ox = -2; ox <= 2; ox++)
                            if (x + ox >= 0 && x + ox <= width && y + oy >= 0 && y + oy <= height && Interior(x + ox, y + oy, 2)) expanded[(y + oy) * stride + x + ox] = true;
            }
            Normalize(expanded, true, 2); return expanded;
        }
        bool[][] NestedFields(bool[] inner, int count)
        {
            var fields=new bool[count][];
            if(count>0){fields[^1]=inner;for(int i=count-2;i>=0;i--)fields[i]=Expand(fields[i+1]);}
            return fields;
        }
        bool[] NaturalExpand(bool[] field,double minimumRadius,double maximumRadius,int salt,int surroundingMargin)
        {
            // Keep every required native underlay vertex. Only the surrounding
            // generated terrain changes; saved bank/cover/ground cells do not.
            var expanded=(bool[])field.Clone();int reach=(int)Math.Ceiling(maximumRadius);
            double maximumSquared=maximumRadius*maximumRadius;
            for(int y=0;y<=height;y++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                for(int x=0;x<=width;x++)
                {
                    int key=y*stride+x;
                    if(field[key]||!Interior(x,y,2))continue;
                    if(coast?.Layout.BoundaryEdges.Count==0&&(x<surroundingMargin||y<surroundingMargin
                        ||x>width-surroundingMargin||y>height-surroundingMargin))continue;
                    int nearest=int.MaxValue;
                    for(int oy=-reach;oy<=reach;oy++)for(int ox=-reach;ox<=reach;ox++)
                    {
                        int distance=ox*ox+oy*oy;
                        if(distance>=nearest||distance>maximumSquared||x+ox<0||x+ox>width||y+oy<0||y+oy>height)continue;
                        if(field[(y+oy)*stride+x+ox])nearest=distance;
                    }
                    if(nearest==int.MaxValue)continue;
                    double radius=minimumRadius+(maximumRadius-minimumRadius)*Noise((x+area.X/2.0)/8,(y+area.Y/2.0)/8,options.Seed^salt);
                    if(nearest<=radius*radius)expanded[key]=true;
                }
            }
            Normalize(expanded,true,2);return expanded;
        }
        var shore = new bool[length];
        if (waterPlan != null)
        {
            for (int y = 0; y < area.Height; y++) for (int x = 0; x < area.Width; x++)
            {
                var state = waterPlan.At(x,y);
                if (!state.Water && state.Fragments.Count == 0) continue;
                foreach (int key in Corners(x / 2,y / 2)) shore[key] = true;
            }
            Normalize(shore,true,2);
        }
        if (coast != null)
        {
            // The saved native underlay is surrounded by the catalogue's reviewed land transitions.
            // The complete object's ground, banks, covers and collision bits stay byte-for-byte intact.
            foreach (var cell in coast.Layout.Cells.Where(c => (c.Cell.BackImage & 0x1fffffff) != 0))
                foreach (int key in Corners((coast.X + cell.X - area.X) / 2, (coast.Y + cell.Y - area.Y) / 2)) shore[key] = true;
            Normalize(shore,true,2);
        }
        if(coast!=null&&waterRoute.Length>0)shore=NaturalExpand(shore,.2,3.5,0x5137A,3);
        var waterFields = coast!=null&&waterRoute.Length==2
            ?new[]{NaturalExpand(shore,2,3.2,0x137A5,2),shore}:NestedFields(shore,waterRoute.Length);
        var waterEnvelope = waterFields.Length == 0 ? shore : waterFields[0];
        bool NearWater(int x, int y, int distance)
        {
            if (waterPlan == null && coast == null) return false;
            for (int oy = -distance; oy <= distance; oy++) for (int ox = -distance; ox <= distance; ox++)
                if (x + ox >= 0 && x + ox <= width && y + oy >= 0 && y + oy <= height && waterEnvelope[(y + oy) * stride + x + ox]) return true;
            return false;
        }
        bool[] path = new bool[length]; var random = new Random(options.Seed);
        int pathMargin = 2 + Math.Max(0, pathRoute.Length - 1) * 2;
        if (options.Path != null || options.KeepPathsOnBase)
        {
            (double X, double Y)[]? firstRoute = null;
            for (int route = 0; route < options.PathCount; route++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var routeRandom = GenerationAlgorithms.UsesConstraints(options.AlgorithmVersion) ? GenerationRandom.Stream(options.Seed, 0x3000 + route) : random;
                var points = RoutePoints(width, height, routeRandom, firstRoute).ToArray();
                firstRoute ??= points;
                foreach (var (px, py) in points)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    double radius = options.PathWidth / 2;
                    for (int y = Math.Max(0, (int)Math.Floor(py - radius)); y <= Math.Min(height, (int)Math.Ceiling(py + radius)); y++)
                        for (int x = Math.Max(0, (int)Math.Floor(px - radius)); x <= Math.Min(width, (int)Math.Ceiling(px + radius)); x++)
                            if (Interior(x, y, pathMargin) && !NearWater(x,y,3 + Math.Max(0,pathRoute.Length-1)*2) && Math.Pow(x - px, 2) + Math.Pow(y - py, 2) <= radius * radius) path[y * stride + x] = true;
                }
            }
            Normalize(path, true, pathMargin);
            if (!path.Any(v => v) && waterPlan == null && coast == null) throw new ArgumentException("The selected area is too small for the chosen path and its terrain transitions. Extend the selection.");
        }
        var pathFields = NestedFields(path, pathRoute.Length);
        bool[] envelope = pathFields.Length == 0 ? path : pathFields[0];
        bool[] patches = new bool[length]; var noise = new double[length]; var candidates = new List<int>();
        int patchMargin = 2 + Math.Max(0, patchRoute.Length - 1) * 2;
        int separation = 3 + Math.Max(0, patchRoute.Length - 1) * 2;
        if (options.Patches != null)
        {
            for (int y = 0; y <= height; y++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                for (int x = 0; x <= width; x++)
                {
                    if (!Interior(x, y, patchMargin) || NearWater(x,y,separation)) continue;
                    bool nearPath = false;
                    for (int oy = -separation; oy <= separation && !nearPath; oy++) for (int ox = -separation; ox <= separation; ox++)
                        if (x + ox >= 0 && x + ox <= width && y + oy >= 0 && y + oy <= height && envelope[(y + oy) * stride + x + ox]) { nearPath = true; break; }
                    if (nearPath) continue;
                    int key = y * stride + x;
                    noise[key] = Noise(x / options.PatchScale, y / options.PatchScale, options.Seed) * .7 + Noise(x * 2 / options.PatchScale, y * 2 / options.PatchScale, options.Seed + 17) * .3;
                    candidates.Add(key);
                }
            }
            foreach (int key in candidates.OrderByDescending(k => noise[k]).Take((int)(candidates.Count * options.Coverage))) patches[key] = true;
            Normalize(patches, false, patchMargin);
        }
        var patchFields = NestedFields(patches, patchRoute.Length);
        var vertices = new int[length];
        var surfaces = new List<(TerrainSet Set, int Parent)> { (options.Base, -1) };
        void PlaceFields(TerrainSet[] route, bool[][] fields)
        {
            int parent = 0;
            for (int i = 0; i < route.Length; i++)
            {
                int label = surfaces.Count; surfaces.Add((route[i], parent));
                for (int k = 0; k < length; k++)
                    if (fields[i][k])
                    {
                        if (vertices[k] != parent) throw new InvalidDataException("Terrain transition strips overlap another material.");
                        vertices[k] = label;
                    }
                parent = label;
            }
        }
        PlaceFields(waterRoute,waterFields); PlaceFields(pathRoute, pathFields); PlaceFields(patchRoute, patchFields);
        var result = new List<TerrainTile>(width * height);
        var orderedVariants = new Dictionary<(TerrainSet Set, int Mask), List<TerrainVariant>>();
        for (int y = 0; y < height; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (int x = 0; x < width; x++)
            {
                var corners = Corners(x, y); var labels = corners.Select(k => vertices[k]).Distinct().ToArray();
                if (labels.Length > 2) throw new InvalidDataException("Unsupported three-material junction.");
                int label = labels[0], mask = 15;
                if (labels.Length == 2)
                {
                    label = surfaces[labels[0]].Parent == labels[1] ? labels[0] : surfaces[labels[1]].Parent == labels[0] ? labels[1] : throw new InvalidDataException("Adjacent materials do not have a reviewed transition.");
                    mask = 0; for (int k = 0; k < 4; k++) if (vertices[corners[k]] == label) mask |= 1 << k;
                }
                var set = surfaces[label].Set;
                if (waterPlan != null && waterPlan.At(x * 2,y * 2).Water) { set = catalogue.Sets.Single(s => s.Id == catalogue.Water!.WaterId); mask = 15; labels = [label]; }
                if (!set.Shapes.TryGetValue(mask, out var variants)) throw new InvalidDataException($"Missing terrain shape {mask}: {set.Name}");
                string background = labels.Length == 1 ? set.Id : surfaces[surfaces[label].Parent].Set.Id;
                if (labels.Length == 2 && !catalogue.CanBlend(surfaces[surfaces[label].Parent].Set, set)
                    && !islandAliases.Contains((surfaces[surfaces[label].Parent].Set.Id,set.Id))) throw new InvalidDataException("Missing reviewed blend for a terrain transition.");
                if (GenerationAlgorithms.UsesConstraints(options.AlgorithmVersion))
                {
                    if (!orderedVariants.TryGetValue((set, mask), out var ordered))
                        orderedVariants[(set, mask)] = ordered = variants.OrderBy(variant => variant.Index).ToList();
                    variants = ordered;
                }
                long sum = variants.Sum(v => v.Weight), roll = (long)(Unit(x + area.X / 2, y + area.Y / 2, options.Seed ^ set.Start) * sum);
                var chosen = variants[^1]; foreach (var v in variants) { roll -= v.Weight; if (roll < 0) { chosen = v; break; } }
                result.Add(new(area.X + x * 2, area.Y + y * 2, set.Library, chosen.Index, set.Id, mask, background));
            }
        }
        if (design != null)
        {
            var snapshot = GeneratedMapSnapshot.Terrain(new(source, area, result, options.Seed, waterPlan) { CoastObjects = coast == null ? [] : [coast] }, cancellationToken);
            GenerationConstraints.EnsureSatisfied(snapshot, design, cancellationToken);
        }
        if (source.PickupRevision != sourceRevision) throw new InvalidOperationException("The map changed while generating the terrain preview.");
        var pathCells = result.Where(t => Corners((t.X - area.X) / 2, (t.Y - area.Y) / 2).Any(k => envelope[k]))
            .Select(t => (t.X, t.Y)).ToArray();
        return new(source, area, result, options.Seed,waterPlan) { SourceRevision = sourceRevision, CoastObjects = coast == null ? [] : [coast],Notes=notes,PathCells=pathCells };
    }

    private static IEnumerable<(double X, double Y)> RoutePoints(int width, int height, Random random, (double X, double Y)[]? firstRoute)
    {
        // Choose each route independently, including its entry/exit edges. Keep
        // endpoints well apart so adjacent-edge choices do not become tiny tips.
        (double X, double Y) Edge(int side)
        {
            double position = .08 + random.NextDouble() * .84;
            return side switch { 0 => (position, 0), 1 => (1, position), 2 => (position, 1), _ => (0, position) };
        }
        int entry = random.Next(4);
        var start = Edge(entry);
        var end = Edge((entry + 2) % 4);
        for (int attempt = 0; attempt < 16; attempt++)
        {
            var candidate = Edge((entry + 1 + random.Next(3)) % 4);
            double dx = candidate.X - start.X, dy = candidate.Y - start.Y;
            if (dx * dx + dy * dy < .45) continue;
            end = candidate; break;
        }
        if (firstRoute != null)
        {
            // Interleave endpoints around the perimeter with the first route.
            // The curves must then meet, without forcing a horizontal/vertical cross.
            double Perimeter((double X, double Y) p)
            {
                double x = p.X / width, y = p.Y / height;
                return y == 0 ? x : x == 1 ? 1 + y : y == 1 ? 3 - x : 4 - y;
            }
            (double X, double Y) At(double value)
            {
                value %= 4;
                return value < 1 ? (value, 0) : value < 2 ? (1, value - 1) : value < 3 ? (3 - value, 1) : (0, 4 - value);
            }
            double a = Perimeter(firstRoute[0]), b = Perimeter(firstRoute[^1]);
            if (a > b) (a, b) = (b, a);
            start = At((a + b) / 2); end = At((a + b) / 2 + 2);
            for (int attempt = 0; attempt < 16; attempt++)
            {
                var p = At(a + (b - a) * (.08 + random.NextDouble() * .84));
                var q = At(b + (4 - b + a) * (.08 + random.NextDouble() * .84));
                double dx = q.X - p.X, dy = q.Y - p.Y;
                if (dx * dx + dy * dy < .45) continue;
                start = p; end = q; break;
            }
        }
        start = (start.X * width, start.Y * height);
        end = (end.X * width, end.Y * height);
        double vx = end.X - start.X, vy = end.Y - start.Y;
        double distance = Math.Sqrt(vx * vx + vy * vy);
        int bends = random.Next(2, 6);
        double amplitude = Math.Min(width, height) * (.08 + random.NextDouble() * .2);
        var knots = new List<(double X, double Y)> { start };
        for (int i = 1; i <= bends; i++)
        {
            double t = (i + (random.NextDouble() - .5) * .5) / (bends + 1);
            double offset = (random.NextDouble() * 2 - 1) * amplitude;
            knots.Add((Math.Clamp(start.X + vx * t - vy / distance * offset, width * .04, width * .96),
                Math.Clamp(start.Y + vy * t + vx / distance * offset, height * .04, height * .96)));
        }
        knots.Add(end);
        // Quadratic segments meet at knot midpoints with matching tangents.
        // Their convex hull keeps bends within the area without clipping curves.
        var from = start;
        for (int i = 1; i < knots.Count - 1; i++)
        {
            var control = knots[i];
            var to = i == knots.Count - 2 ? end : ((control.X + knots[i + 1].X) / 2, (control.Y + knots[i + 1].Y) / 2);
            double Length((double X, double Y) a, (double X, double Y) b) => Math.Sqrt(Math.Pow(a.X - b.X, 2) + Math.Pow(a.Y - b.Y, 2));
            int steps = Math.Max(1, (int)Math.Ceiling((Length(from, control) + Length(control, to)) * 4));
            for (int step = 0; step <= steps; step++)
            {
                double t = step / (double)steps, u = 1 - t;
                yield return (u * u * from.X + 2 * u * t * control.X + t * t * to.Item1,
                    u * u * from.Y + 2 * u * t * control.Y + t * t * to.Item2);
            }
            from = to;
        }
    }

    private static void CheckBoundary(MapDocument document, TerrainArea area, TerrainSet basis)
    {
        var centres = basis.Shapes[15].Select(v => v.Index + 1).ToHashSet();
        void Check(int x, int y)
        {
            if (!document.Contains(x, y)) return;
            var c = document.Get(x, y); int image = c.BackImage & 0x1FFFFFFF;
            if (image != 0 && (c.BackLibrary != basis.Library || !centres.Contains(image))) throw new ArgumentException($"Selection edge meets another terrain at {x},{y}. Extend the selection or choose the matching base terrain.");
        }
        for (int x = area.X; x < area.X + area.Width; x += 2) { Check(x, area.Y - 2); Check(x, area.Y + ((area.Height + 1) / 2) * 2); }
        for (int y = area.Y; y < area.Y + area.Height; y += 2) { Check(area.X - 2, y); Check(area.X + ((area.Width + 1) / 2) * 2, y); }
    }
    private static double Noise(double x, double y, int seed)
    {
        int ix = (int)Math.Floor(x), iy = (int)Math.Floor(y); double tx = x - ix, ty = y - iy;
        tx = tx * tx * (3 - 2 * tx); ty = ty * ty * (3 - 2 * ty);
        double a = Unit(ix, iy, seed) * (1 - tx) + Unit(ix + 1, iy, seed) * tx;
        double b = Unit(ix, iy + 1, seed) * (1 - tx) + Unit(ix + 1, iy + 1, seed) * tx;
        return a * (1 - ty) + b * ty;
    }
    private static double Unit(int x, int y, int seed)
    {
        uint h = unchecked((uint)x * 374761393u + (uint)y * 668265263u + (uint)seed * 2246822519u);
        h = (h ^ (h >> 13)) * 1274126177u; h ^= h >> 16; return h / 4294967296.0;
    }
}
