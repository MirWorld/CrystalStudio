using System.IO;
using System.Text.Json;

namespace CrystalStudio;

public sealed record WallDirection(int Bit, int X, int Y);
public sealed record WallLinePortOffset(int Mask, int Direction, int Y);
public sealed record WallChoice(string PieceId, int X = 0, int Y = 0);
public sealed record WallCellPoint(int X, int Y);
public sealed record WallPartAttachment(string PieceId, int X, int Y, int FrontColumn);
public sealed record WallPart(string PieceId, int X, int Y, List<WallChoice>? Choices = null,
    List<WallCellPoint>? CellSelection = null, List<WallCellPoint>? FrontSelection = null,
    bool VariableFootprint = false, List<int>? FrontColumnSelection = null, WallPartAttachment? Attachment = null,
    int ConnectionDirection = 0);
public sealed record WallVariant(int Mask, int Corners, string Shape, List<WallPart> Parts);
public sealed record WallNativeTrim(string CornerPieceId, string PieceId, int X, int Y,
    string? ReplacementPieceId = null, List<int>? FrontColumnSelection = null, bool? Interior = null,
    WallChoice? RequiredCorner = null);
public sealed record WallBoundarySpan(bool Interior, bool AlongU, int FromCorner, int ToCorner, int MinimumPieces);
// Opaque coverage only, in 48-pixel columns; artwork stays in the game library.
public sealed record WallSpriteSpan(int Column, int Y, int Height, ulong Mask);
public sealed record WallSpriteFootprint(int Library, int Image, List<WallSpriteSpan> Spans);
public sealed record WallPiece(string Id, string SourceSha256, string File, string Role, int Priority, List<StampCell> Cells, string? SourceId = null, List<WallCellPoint>? OcclusionCells = null);
public sealed record WallNode(string FamilyId, int Mask,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.Never)] int Corners = 3,
    bool Interior = false, int VariationSeed = 0, int LayoutRevision = 0, bool AutomaticConnector = false, int PlacementStep = 0,
    int DoorCount = 0, int DoorSeed = 0, bool SpecialWalls = false);
internal sealed record WallLayout(IReadOnlyList<StampCell> Cells, IReadOnlyDictionary<(int X,int Y),int> Priorities,
    IReadOnlyDictionary<(int X,int Y),(int Y,int X,string Id)> Depths);
public sealed class WallFamily
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Kind { get; init; }
    public int Step { get; init; }
    // Cursor spacing can reserve room for native turns without widening the
    // corridor. Geometry between cursor positions still uses Step-sized cells.
    public int StrokeGridPieces { get; init; } = 1;
    public int PaintSizePieces { get; init; } = 1;
    public bool AreaBrush { get; init; } = true;
    public bool SupportsJunctions { get; init; } = true;
    public List<WallLinePortOffset> LinePortOffsets { get; init; } = [];
    [System.Text.Json.Serialization.JsonIgnore]
    public bool HasFacingModes => InnerVariants.Count > 0;
    [System.Text.Json.Serialization.JsonIgnore]
    public string DisplayName => HasFacingModes ? $"{Name} · {(DrawInterior ? "Inner" : "Outer")}" : Name;
    [System.Text.Json.Serialization.JsonIgnore]
    public bool PaintsAreas => AreaBrush || PaintSizePieces > 1;
    [System.Text.Json.Serialization.JsonIgnore]
    public int? RequestedDoorCount { get; set; }
    internal int DoorRandomSeed { get; } = Random.Shared.Next(1,int.MaxValue);
    public List<WallEntrance> Entrances { get; init; } = [];
    [System.Text.Json.Serialization.JsonIgnore]
    internal bool DoorCountPerMap { get; set; }
    [System.Text.Json.Serialization.JsonIgnore]
    public bool SupportsDoors => PaintsAreas && Entrances.Count > 0;
    public int? InnerStep { get; init; }
    public int? OuterStep { get; init; }
    public List<WallDirection>? InnerDirections { get; init; }
    public List<WallDirection>? OuterDirections { get; init; }
    public int LayoutRevision { get; init; }
    public bool AllowsFilledSections { get; init; }
    public int InteriorCollisionMask { get; init; }
    public bool DrawInterior { get; init; }
    public bool FourVertexCorners { get; init; }
    public int PreserveLegacyStep { get; init; }
    public int? PreserveLegacyInnerStep { get; init; }
    public int? PreserveLegacyOuterStep { get; init; }
    public bool ConnectTouchingSections { get; init; }
    public int TurnClearancePieces { get; init; }
    public List<WallBoundarySpan> BoundarySpans { get; init; } = [];
    public bool FitsNativeCorners => BoundarySpans.Count > 0;
    public List<WallNativeTrim> NativeTrims { get; init; } = [];
    public List<WallSpriteFootprint> SpriteFootprints { get; init; } = [];
    private bool? hasVariations;
    public bool HasVariations => hasVariations ??= Variants.Concat(InnerVariants).Any(v => v.Parts.Any(p => p.Choices is { Count: > 1 }));
    public List<WallDirection> Directions { get; init; } = [];
    public List<WallPiece> Pieces { get; init; } = [];
    public List<WallVariant> Variants { get; init; } = [];
    public List<WallVariant> InnerVariants { get; init; } = [];
    private readonly Dictionary<(int, int), WallLayout> layouts = [];
    private Dictionary<string,WallPiece>? piecesById;
    private Dictionary<(int Mask,int Corners),WallVariant>? variantsByMask;
    private IReadOnlyList<WallDirection>? vertexNeighbours;
    private readonly Dictionary<(int Mask,int Corners,int Seed,int Parts), (WallPart[] Parts,WallLayout Layout)> composedLayouts=[];
    private readonly Queue<(int Mask,int Corners,int Seed,int Parts)> composedOrder=[];
    private int composedCellCount;
    private IReadOnlyList<StampCell>? brushPreview;

    internal WallVariant Variant(int mask,int corners) =>
        (variantsByMask ??= (DrawInterior ? InnerVariants : Variants).ToDictionary(v=>(v.Mask,v.Corners)))[(mask,corners)];

    public IReadOnlyList<StampCell> BrushPreview()
    {
        if (PaintSizePieces==1) return Layout(0);
        if (brushPreview!=null) return brushPreview;
        var map=new MapDocument(512,512);
        var points=new List<(int X,int Y)>();
        for (int u=0;u<PaintSizePieces;u++)
        for (int v=0;v<PaintSizePieces;v++)
        {
            var offset=GridPoint(u,v);
            points.Add((256+offset.X,256+offset.Y));
        }
        var plan=WallBrush.PlanStroke(map,this,points);
        if (!plan.Allowed) throw new InvalidDataException(plan.Error);
        return brushPreview=plan.Cells.Select(p=>new StampCell(p.Key.X-256,p.Key.Y-256,p.Value)).ToArray();
    }

    public WallFamily WithMode(bool interior)
    {
        interior &= HasFacingModes;
        return new()
        {
            Id = Id, Name = Name, Kind = Kind, Step = (interior ? InnerStep : OuterStep) ?? Step,
            StrokeGridPieces = StrokeGridPieces, PaintSizePieces = PaintSizePieces, AreaBrush = AreaBrush, SpriteFootprints = SpriteFootprints,
            SupportsJunctions = SupportsJunctions, LinePortOffsets = LinePortOffsets,
            Entrances = Entrances, RequestedDoorCount = RequestedDoorCount, DoorCountPerMap = DoorCountPerMap,
            InnerStep = InnerStep, OuterStep = OuterStep, InnerDirections = InnerDirections, OuterDirections = OuterDirections,
            LayoutRevision = LayoutRevision, InteriorCollisionMask = InteriorCollisionMask,
            DrawInterior = interior, FourVertexCorners = FourVertexCorners, AllowsFilledSections = AllowsFilledSections,
            PreserveLegacyStep = (interior ? PreserveLegacyInnerStep : PreserveLegacyOuterStep) ?? PreserveLegacyStep,
            PreserveLegacyInnerStep = PreserveLegacyInnerStep, PreserveLegacyOuterStep = PreserveLegacyOuterStep,
            NativeTrims = NativeTrims, ConnectTouchingSections = ConnectTouchingSections,
            TurnClearancePieces = TurnClearancePieces, BoundarySpans = BoundarySpans,
            Directions = (interior ? InnerDirections : OuterDirections) ?? Directions, Pieces = Pieces, Variants = Variants, InnerVariants = InnerVariants
        };
    }

    public (int X, int Y) Snap(int x, int y)
    {
        var (u, v) = LatticeCoordinates(x, y);
        return GridPoint((int)Math.Round(u, MidpointRounding.AwayFromZero),
            (int)Math.Round(v, MidpointRounding.AwayFromZero));
    }

    // Native cliffs can advance different distances in X and Y. Their two
    // authored diagonal directions define the grid, its vertices and floor.
    public (int X, int Y) GridPoint(int u, int v)
    {
        var a = Directions.Single(d => d.Bit == 1);
        var b = Directions.Single(d => d.Bit == 2);
        return (u * b.X + v * a.X, u * b.Y + v * a.Y);
    }

    internal (double U, double V) LatticeCoordinates(int x, int y)
    {
        var a = Directions.Single(d => d.Bit == 1);
        var b = Directions.Single(d => d.Bit == 2);
        double determinant = b.X * a.Y - a.X * b.Y;
        return ((a.Y * (double)x - a.X * (double)y) / determinant,
            (b.X * (double)y - b.Y * (double)x) / determinant);
    }

    public IReadOnlyList<WallDirection> VertexNeighbours
    {
        get
        {
            if (vertexNeighbours!=null) return vertexNeighbours;
            var a = Directions.Single(d => d.Bit == 1);
            var b = Directions.Single(d => d.Bit == 2);
            return vertexNeighbours = [new(1, a.X + b.X, a.Y + b.Y), new(2, -a.X - b.X, -a.Y - b.Y),
                new(4, a.X - b.X, a.Y - b.Y), new(8, b.X - a.X, b.Y - a.Y)];
        }
    }

    public bool ContainsOffset(int x, int y, bool boundary = false)
    {
        var (u, v) = LatticeCoordinates(x, y);
        return boundary ? Math.Max(Math.Abs(u), Math.Abs(v)) <= 0.5 : Math.Max(Math.Abs(u), Math.Abs(v)) < 0.5;
    }

    internal bool ValidLattice => Directions.Count == 4 &&
        Directions.Select(d => d.Bit).Order().SequenceEqual(new[] { 1, 2, 4, 8 }) &&
        Directions.All(d => Math.Abs((long)d.X) is >= 1 and <= 64 && Math.Abs((long)d.Y) is >= 1 and <= 64) &&
        Directions.Single(d => d.Bit == 1) is { X: > 0, Y: < 0 } &&
        Directions.Single(d => d.Bit == 2) is { X: > 0, Y: > 0 } &&
        Directions.Where(d => d.Bit <= 2).All(d => Directions.Any(o => o.Bit == d.Bit * 4 && o.X == -d.X && o.Y == -d.Y)) &&
        VertexNeighbours.All(d => d.X % 2 == 0 && d.Y % 2 == 0);

    public IReadOnlyList<StampCell> Layout(int mask, int corners = -1, int variationSeed = 0)
        => Compose(mask,corners,variationSeed).Cells;

    internal WallLayout Compose(int mask, int corners = -1, int variationSeed = 0, IReadOnlyList<WallPart>? selectedParts = null, bool validateOnly = false)
    {
        if (corners < 0) corners = DrawInterior || FourVertexCorners ? 15 : 3;
        if (selectedParts == null && variationSeed == 0 && layouts.TryGetValue((mask, corners), out var cached)) return cached;
        var parts=selectedParts ?? Variant(mask,corners).Parts;
        // Native trims select a recipe before composition. Cache that exact
        // recipe, including its selections and random seed, so extending one
        // edge does not rebuild every unchanged solid section's artwork.
        int seed=parts.Any(p=>p.Choices is { Count: > 0 }) ? variationSeed : 0;
        var recipeHash=new HashCode();
        foreach (var part in parts)
        {
            recipeHash.Add(part.PieceId); recipeHash.Add(part.X); recipeHash.Add(part.Y);
            AddSelection(part.Choices); AddSelection(part.CellSelection);
            AddSelection(part.FrontSelection); AddSelection(part.FrontColumnSelection);
        }
        var key=(mask,corners,seed,recipeHash.ToHashCode());
        if (composedLayouts.TryGetValue(key,out var previous) && SameParts(previous.Parts,parts)) return previous.Layout;
        piecesById ??= Pieces.Concat(Entrances.Select(e=>e.Piece)).DistinctBy(p=>p.Id).ToDictionary(p=>p.Id);
        var cells = new Dictionary<(int X, int Y), Cell>();
        var priorities = new Dictionary<(int X, int Y), int>();
        var depths = new Dictionary<(int X, int Y), (int Y, int X, string Id)>();
        foreach (var part in parts)
        {
            var choice = new WallChoice(part.PieceId);
            if (variationSeed != 0 && part.Choices is { Count: > 0 } choices)
            {
                // Stable slot hashing keeps an existing segment's artwork when
                // its neighbours change, without depending on recipe list order.
                uint hash = (uint)variationSeed;
                foreach (char character in part.PieceId) hash = (hash ^ character) * 16777619;
                hash = (hash ^ (uint)part.X) * 16777619;
                hash = (hash ^ (uint)part.Y) * 16777619;
                hash ^= hash >> 16; hash *= 0x7feb352d; hash ^= hash >> 15;
                choice = choices[(int)(hash % choices.Count)];
            }
            var piece = piecesById[choice.PieceId];
            var selectedCells = part.CellSelection?.Select(p => (p.X, p.Y)).ToHashSet();
            var selectedFronts = part.FrontSelection?.Select(p => (p.X, p.Y)).ToHashSet();
            var occlusion = piece.OcclusionCells?.Select(p => (p.X, p.Y)).ToHashSet();
            foreach (var source in piece.Cells)
            {
                if (validateOnly && source.Cell.ForegroundImages.Count == 0) continue;
                if (selectedCells != null && !selectedCells.Contains((source.X, source.Y))) continue;
                var point = (source.X + part.X + choice.X, source.Y + part.Y + choice.Y);
                if (!cells.TryGetValue(point, out var cell)) cells[point] = cell = new();
                cell.BackImage |= source.Cell.BackImage & 0x60000000;
                if (selectedFronts != null && !selectedFronts.Contains((source.X + choice.X, source.Y + choice.Y))) continue;
                if (part.FrontColumnSelection != null && !part.FrontColumnSelection.Contains(source.X + choice.X)) continue;
                // A flat core tile must never replace a native tall backing
                // card at the same anchor: doing so opens a hole above it.
                int priority = piece.Role == "nativeOpaqueBacking" ? -1 :
                    occlusion?.Contains((source.X,source.Y)) == true ? 0 : piece.Priority;
                foreach (var front in source.Cell.ForegroundImages)
                {
                    var depth = (part.Y + choice.Y, part.X + choice.X, piece.Id);
                    // Identical sprites still need to retain the nearest owner's
                    // rank: another overlapping piece may compete for this cell.
                    if (AllowsFilledSections && cell.ForegroundImages.Count > 0 &&
                        (priorities[point] > priority || priorities[point] == priority &&
                         Comparer<(int, int, string)>.Default.Compare(depth, depths[point]) <= 0)) continue;
                    if (cell.ForegroundImages.Count > 0 && !cell.ForegroundImages.Contains(front))
                    {
                        if (priorities[point] == priority)
                        {
                            if (!AllowsFilledSections) throw new InvalidDataException($"Overlapping wall sprites in {Id}, mask {mask}.");
                            if (Comparer<(int, int, string)>.Default.Compare(depth, depths[point]) <= 0) continue;
                        }
                        if (priorities[point] > priority) continue;
                        // Front faces cover the bottom of rear occlusion masks at a
                        // shared vertex. Keep one authored sprite per Crystal v1 cell.
                        cell.Foreground = null;
                    }
                    if (!cell.ForegroundImages.Contains(front)) cell.Foreground = front;
                    priorities[point] = priority;
                    depths[point] = depth;
                    cell.Door = piece.Role=="entrance" ? (byte)(source.Cell.Door & 0x7f) : (byte)0;
                    cell.DoorOffset = piece.Role=="entrance" ? source.Cell.DoorOffset : (byte)0;
                }
            }
        }
        // Catalogue validation needs the same sprite overlap checks, but the
        // collision fill, sorted stamp cells and cached layouts are discarded.
        if (validateOnly) return new([], priorities, depths);
        // Optional solid fill belongs to exterior wall families. Room families
        // retain only their boundary and keep the drawn interior open.
        var vertices = VertexNeighbours;
        int extentX = vertices.Max(d => Math.Abs(d.X)) / 2, extentY = vertices.Max(d => Math.Abs(d.Y)) / 2;
        if (!DrawInterior && InteriorCollisionMask != 0)
        for (int y = -extentY; y <= extentY; y++)
        for (int x = -extentX; x <= extentX; x++)
        {
            if (!ContainsOffset(x, y)) continue;
            if (!cells.TryGetValue((x, y), out var cell)) cells[(x, y)] = cell = new();
            cell.BackImage |= InteriorCollisionMask;
        }
        if (cells.Count == 0) cells[(0, 0)] = new(); // Persist an interior node with no exposed boundary.
        var result = cells.OrderBy(p => p.Key.Y).ThenBy(p => p.Key.X)
            .Select(p => new StampCell(p.Key.X, p.Key.Y, p.Value)).ToList();
        var composed = new WallLayout(result,priorities,depths);
        if (selectedParts == null && variationSeed == 0) layouts[(mask, corners)] = composed;
        if (result.Count<=100000)
        {
            if (previous.Layout!=null) composedCellCount-=previous.Layout.Cells.Count;
            else composedOrder.Enqueue(key);
            composedLayouts[key]=(parts.ToArray(),composed); composedCellCount+=result.Count;
            while (composedLayouts.Count>512 || composedCellCount>100000)
                if (composedLayouts.Remove(composedOrder.Dequeue(),out var expired)) composedCellCount-=expired.Layout.Cells.Count;
        }
        return composed;

        void AddSelection<T>(List<T>? selection)
        {
            recipeHash.Add(selection?.Count ?? -1);
            if (selection!=null) foreach (var value in selection) recipeHash.Add(value);
        }
    }

    private static bool SameParts(IReadOnlyList<WallPart> a,IReadOnlyList<WallPart> b)
    {
        if (a.Count!=b.Count) return false;
        for (int i=0;i<a.Count;i++)
        {
            var x=a[i]; var y=b[i];
            if (ReferenceEquals(x,y)) continue;
            if (x.PieceId!=y.PieceId || x.X!=y.X || x.Y!=y.Y || !Same(x.Choices,y.Choices) ||
                !Same(x.CellSelection,y.CellSelection) || !Same(x.FrontSelection,y.FrontSelection) ||
                !Same(x.FrontColumnSelection,y.FrontColumnSelection)) return false;
        }
        return true;
        static bool Same<T>(List<T>? x,List<T>? y) => ReferenceEquals(x,y) || x!=null && y!=null && x.SequenceEqual(y);
    }

    public string? Availability(AssetStore? assets)
    {
        if (assets == null) return "Load game libraries in Settings to draw walls";
        var used = (DrawInterior ? InnerVariants : Variants).SelectMany(v => v.Parts)
            .SelectMany(p => new[] { p.PieceId }.Concat(p.Choices?.Select(c => c.PieceId) ?? [])).ToHashSet();
        foreach (var front in Pieces.Where(p => used.Contains(p.Id))
            .Concat(RequestedDoorCount > 0 ? Entrances.Select(e=>e.Piece) : [])
            .SelectMany(p => p.Cells).SelectMany(c => c.Cell.ForegroundImages).Distinct())
        {
            if (!assets.Libraries.TryGetValue(front.Library, out var library) || (front.Image & 0x7FFF) > library.Count)
                return $"{Name} needs library {front.Library}, sprite {(front.Image & 0x7FFF) - 1}";
            if (library.Decode((front.Image & 0x7FFF) - 1) == null)
                return $"{Name} sprite {(front.Image & 0x7FFF) - 1} is missing";
        }
        return null;
    }
}

public sealed record WallCatalogue(int SchemaVersion, List<WallFamily> Families)
{
    private static readonly JsonSerializerOptions ReadOptions = new() { PropertyNameCaseInsensitive = true };
    public static string ResolvePath()
    {
        return DataPaths.ResolveEditor("catalogue", "walls.json");
    }

    public static IReadOnlyList<string> DefinitionPaths(string path)
    {
        path = Path.GetFullPath(path);
        using var stream = File.OpenRead(path);
        using var json = JsonDocument.Parse(stream);
        if (!json.RootElement.TryGetProperty("schemaVersion",out var schema) || schema.ValueKind != JsonValueKind.Number || !schema.TryGetInt32(out int version))
            throw new InvalidDataException("Invalid wall catalogue version.");
        if (version == 1) return [path];
        if (version != 2 || json.RootElement.TryGetProperty("families",out _) ||
            !json.RootElement.TryGetProperty("familyFiles",out var files) || files.ValueKind != JsonValueKind.Array ||
            files.GetArrayLength() is < 1 or > 512)
            throw new InvalidDataException("Invalid wall catalogue index.");
        string directory = Path.GetDirectoryName(path)!;
        string prefix = directory.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var definitions = new List<string>();
        foreach (var file in files.EnumerateArray())
        {
            string? relative = file.ValueKind == JsonValueKind.String ? file.GetString() : null;
            if (string.IsNullOrWhiteSpace(relative) || relative.Contains('\0') || Path.IsPathRooted(relative))
                throw new InvalidDataException("Wall definitions must use relative paths.");
            string definition = Path.GetFullPath(Path.Combine(directory,relative));
            if (!definition.StartsWith(prefix,StringComparison.OrdinalIgnoreCase) || definition.Equals(path,StringComparison.OrdinalIgnoreCase) ||
                !definition.EndsWith(".json",StringComparison.OrdinalIgnoreCase) || definitions.Contains(definition,StringComparer.OrdinalIgnoreCase))
                throw new InvalidDataException("Invalid or duplicate wall definition path.");
            definitions.Add(definition);
        }
        return definitions;
    }

    public static WallCatalogue Read(string path)
    {
        var definitions = DefinitionPaths(path).Select(file =>
        {
            using var stream = File.OpenRead(file);
            return JsonSerializer.Deserialize<WallCatalogue>(stream, ReadOptions)
                ?? throw new InvalidDataException("Empty wall catalogue.");
        }).ToList();
        if (definitions.Any(d=>d.SchemaVersion!=1 || d.Families==null || d.Families.Count==0))
            throw new InvalidDataException("Invalid wall definition file.");
        var catalogue = new WallCatalogue(1,definitions.SelectMany(d=>d.Families).ToList());
        if (catalogue.SchemaVersion != 1 || catalogue.Families.Count == 0 || catalogue.Families.Select(f => f.Id).Distinct().Count() != catalogue.Families.Count)
            throw new InvalidDataException("Invalid wall catalogue version or families.");
        foreach (var family in catalogue.Families)
        {
            if (string.IsNullOrWhiteSpace(family.Id) || family.Id.Length > 128 || string.IsNullOrWhiteSpace(family.Name) ||
                family.Kind is not ("wall" or "cliff" or "fence") || family.Pieces.Count == 0 ||
                family.Pieces.Select(p => p.Id).Distinct().Count() != family.Pieces.Count || family.Pieces.Concat(family.Entrances.Select(e=>e.Piece)).Any(p =>
                    p.Cells.Count == 0 || p.Cells.Count > 10000 || p.Priority is < 0 or > 100 ||
                    p.OcclusionCells is { } backing && (backing.Count>10000 || backing.Distinct().Count()!=backing.Count ||
                        backing.Any(b=>!p.Cells.Any(c=>c.X==b.X && c.Y==b.Y && c.Cell.ForegroundImages.Count>0))) ||
                    p.Cells.Select(c => (c.X, c.Y)).Distinct().Count() != p.Cells.Count ||
                    p.Cells.Any(c => (c.Cell.BackImage & ~0x60000000) != 0 || c.Cell.MiddleImage != 0  || c.Cell.ForegroundImages.Any(f => f.Library < 0 || f.Image <= 0))))
                throw new InvalidDataException("Invalid wall family or native piece layout.");
            if (family.Entrances.Count>32 || family.Entrances.Any(e=>!family.PaintsAreas || e.Length is < 1 or > 8 ||
                    e.DX is < 1 or > 64 || Math.Abs((long)e.DY)>64 || e.DY==0 || Math.Abs((long)e.X)>64 || Math.Abs((long)e.Y)>64 ||
                    !family.Pieces.Any(p=>p.Role==e.Role) || family.Pieces.Any(p=>p.Id==e.Piece.Id)) ||
                family.Entrances.Select(e=>e.Piece.Id).Distinct().Count()!=family.Entrances.Count)
                throw new InvalidDataException("Invalid native wall entrance recipe.");
            if (family.Step < 1 || family.Step > 64 || family.StrokeGridPieces is < 1 or > 8 || family.PaintSizePieces is < 1 or > 8 || family.InnerStep is < 1 or > 64 || family.OuterStep is < 1 or > 64 ||
                !family.WithMode(false).ValidLattice || !family.WithMode(true).ValidLattice ||
                family.LayoutRevision < 0 || (family.InteriorCollisionMask & ~0x60000000) != 0 || family.Directions.Count != 4 ||
                !family.ValidLattice ||
                family.Variants.Count != (family.FourVertexCorners ? 256 : 64) ||
                family.Variants.Select(v => (v.Mask, v.Corners)).Distinct().Count() != family.Variants.Count ||
                family.Variants.Any(v => v.Mask is < 0 or > 15 || v.Corners < 0 || v.Corners > (family.FourVertexCorners ? 15 : 3)) ||
                (family.InnerVariants.Count != 0 && (family.InnerVariants.Count != 256 ||
                    family.InnerVariants.Select(v => (v.Mask, v.Corners)).Distinct().Count() != 256 ||
                    family.InnerVariants.Any(v => v.Mask is < 0 or > 15 || v.Corners is < 0 or > 15))) ||
                family.DrawInterior && family.InnerVariants.Count == 0)
                throw new InvalidDataException("Invalid wall lattice or connection masks.");
            var pieceIds = family.Pieces.Select(p => p.Id).ToHashSet();
            if (family.LinePortOffsets.Count > 64 ||
                family.LinePortOffsets.Count > 0 && family.PaintsAreas ||
                family.LinePortOffsets.Select(p => (p.Mask,p.Direction)).Distinct().Count() != family.LinePortOffsets.Count ||
                family.LinePortOffsets.Any(p => p.Mask is < 0 or > 15 || p.Direction is not (1 or 2 or 4 or 8) ||
                    (p.Mask & p.Direction) == 0 || Math.Abs((long)p.Y) > 8))
                throw new InvalidDataException("Invalid native line wall ports.");
            if (family.SpriteFootprints.Count > 10000 ||
                family.SpriteFootprints.Select(p=>(p.Library,p.Image)).Distinct().Count()!=family.SpriteFootprints.Count ||
                family.SpriteFootprints.Any(p=>p.Library<0 || p.Image<=0 || p.Spans.Count>100000 ||
                    p.Spans.Any(s=>s.Column is < 0 or > 170 || s.Y is < -8192 or > 8192 ||
                        s.Height is < 1 or > 8192 || s.Mask==0 || s.Mask>>48!=0)))
                throw new InvalidDataException("Invalid native wall sprite coverage.");
            if (family.PreserveLegacyStep is < 0 or > 64 ||
                family.PreserveLegacyInnerStep is < 0 or > 64 || family.PreserveLegacyOuterStep is < 0 or > 64 ||
                family.TurnClearancePieces is < 0 or > 8 ||
                family.BoundarySpans.Count > 64 || family.BoundarySpans.Any(s =>
                    s.MinimumPieces is < 1 or > 8 || s.FromCorner is not (1 or 2 or 4 or 8 or 7 or 11 or 13 or 14) ||
                    s.ToCorner is not (1 or 2 or 4 or 8 or 7 or 11 or 13 or 14)) ||
                family.BoundarySpans.Select(s => (s.Interior,s.AlongU,s.FromCorner,s.ToCorner)).Distinct().Count() != family.BoundarySpans.Count ||
                family.NativeTrims.Count > 1000 ||
                family.NativeTrims.Any(t => !pieceIds.Contains(t.CornerPieceId) || !pieceIds.Contains(t.PieceId) ||
                    Math.Abs((long)t.X) > 64 || Math.Abs((long)t.Y) > 64 ||
                    t.ReplacementPieceId != null && !pieceIds.Contains(t.ReplacementPieceId) ||
                    t.RequiredCorner is { } required && (!pieceIds.Contains(required.PieceId) ||
                        Math.Abs((long)required.X)>64 || Math.Abs((long)required.Y)>64) ||
                    t.FrontColumnSelection is { } columns && (columns.Count==0 || columns.Distinct().Count()!=columns.Count ||
                        columns.Any(x=>!family.Pieces.Single(p=>p.Id==(t.ReplacementPieceId??t.PieceId)).Cells.Any(c=>c.X==x && c.Cell.ForegroundImages.Count>0)))))
                throw new InvalidDataException("Invalid native wall shoulder joins.");
            foreach (var part in family.Variants.Concat(family.InnerVariants).SelectMany(v => v.Parts))
            {
                if (part.ConnectionDirection is not (0 or 1 or 2 or 4 or 8))
                    throw new InvalidDataException("Invalid wall part connection direction.");
                if (part.Attachment is { } attachment && (!pieceIds.Contains(attachment.PieceId) ||
                    Math.Abs((long)attachment.X)>64 || Math.Abs((long)attachment.Y)>64 ||
                    !family.Pieces.Single(p=>p.Id==attachment.PieceId).Cells.Any(c=>c.X==attachment.FrontColumn && c.Cell.ForegroundImages.Count>0)))
                    throw new InvalidDataException("Invalid attached wall backing.");
                if (!pieceIds.Contains(part.PieceId) || part.Choices is { } choices &&
                    (choices.Count == 0 || choices.Count > 64 || choices.Distinct().Count() != choices.Count ||
                        choices.Any(c => !pieceIds.Contains(c.PieceId) || Math.Abs((long)c.X) > 64 || Math.Abs((long)c.Y) > 64)))
                    throw new InvalidDataException("Invalid wall section choices.");
                var native = family.Pieces.Single(p => p.Id == part.PieceId);
                if (part.CellSelection is { } selected && (part.Choices != null || selected.Count == 0 ||
                    selected.Distinct().Count() != selected.Count || selected.Any(p => !native.Cells.Any(c=>c.X==p.X && c.Y==p.Y))) ||
                    part.FrontSelection is { } fronts && (fronts.Count == 0 || fronts.Distinct().Count() != fronts.Count ||
                        fronts.Any(p => !native.Cells.Any(c=>c.X==p.X && c.Y==p.Y && c.Cell.ForegroundImages.Count>0))))
                    throw new InvalidDataException("Invalid native wall cell selection.");
                if (part.FrontColumnSelection is { } columns && (columns.Count == 0 || columns.Distinct().Count() != columns.Count ||
                    columns.Any(x=>!native.Cells.Any(c=>c.X==x && c.Cell.ForegroundImages.Count>0))) ||
                    part.VariableFootprint && (part.Choices==null || part.CellSelection!=null || part.FrontSelection!=null))
                    throw new InvalidDataException("Invalid native wall column selection or variation.");
                if (part.Choices == null) continue;
                var baseline = family.Pieces.Single(p => p.Id == part.PieceId);
                var footprint = baseline.Cells.Where(c => c.Cell.ForegroundImages.Count > 0).Select(c => (c.X,c.Y)).ToHashSet();
                foreach (var choice in part.Choices)
                {
                    var alternate = family.Pieces.Single(p => p.Id == choice.PieceId);
                    var shifted=alternate.Cells.Where(c=>c.Cell.ForegroundImages.Count>0).Select(c=>(X:c.X+choice.X,Y:c.Y+choice.Y)).ToHashSet();
                    bool compatible=part.VariableFootprint
                        ? footprint.Select(p=>p.X).ToHashSet().SetEquals(shifted.Select(p=>p.X)) &&
                          shifted.All(p=>p.Y>=footprint.Where(b=>b.X==p.X).Min(b=>b.Y)-8 && p.Y<=footprint.Where(b=>b.X==p.X).Max(b=>b.Y)+8)
                        : footprint.SetEquals(shifted);
                    if (alternate.Priority != baseline.Priority || !compatible)
                        throw new InvalidDataException("Wall alternatives have incompatible native footprints.");
                }
            }
            var outer = family.WithMode(false);
            foreach (var variant in family.Variants) outer.Compose(variant.Mask, variant.Corners, validateOnly: true);
            if (family.InnerVariants.Count > 0)
            {
                var inner = family.WithMode(true);
                foreach (var variant in family.InnerVariants) inner.Compose(variant.Mask, variant.Corners, validateOnly: true);
            }
        }
        return catalogue;
    }
}

public sealed record WallReplacement(int X, int Y, int Mask, int Corners, IReadOnlyList<StampCell> Cells, int VariationSeed = 0,
    IReadOnlyDictionary<(int X,int Y),int>? SpritePriorities = null, bool AutomaticConnector = false,
    IReadOnlyDictionary<(int X,int Y),(int Y,int X,string Id)>? SpriteDepths = null,
    int DoorCount = 0, int DoorSeed = 0, bool SpecialWalls = false);
public sealed record WallPlan(MapDocument Document, long Revision, WallFamily Family, List<PlacedStamp> Removed, List<WallReplacement> Replacements,
    Dictionary<(int X, int Y), Cell> Cells, List<PlacedStamp> Stamps, string? Error)
{
    public IReadOnlyList<GeneratedSideRoom> SideRooms { get; init; } = [];
    public bool Allowed => Error == null;
    public bool HasChanges => Removed.Count > 0 || Replacements.Count > 0;
}

public static class WallBrush
{
    public static WallPlan Plan(MapDocument document, WallFamily family, int x, int y, bool erase = false)
        => PlanStroke(document, family, [(x, y)], erase);

    public static WallPlan PlanStroke(MapDocument document, WallFamily family, IEnumerable<(int X, int Y)> positions, bool erase = false, int? variationSeed = null, bool clipToMap = true, bool specialWalls = false)
    {
        var requested = positions.Select(p => family.Snap(p.X, p.Y)).Distinct().ToArray();
        if (requested.Length == 0) return new(document, document.PickupRevision, family, [], [], [], [], null);
        var point = requested[^1];
        var existing = document.PlacedStamps.Where(s => s.Wall?.FamilyId == family.Id && s.Wall.Interior == family.DrawInterior &&
                (family.PreserveLegacyStep == 0 || (s.Wall.PlacementStep == 0 ? family.PreserveLegacyStep : s.Wall.PlacementStep) == family.Step))
            .GroupBy(s => (s.X, s.Y)).ToDictionary(g => g.Key, g => g.First());
        bool useSpecialWalls = family.DrawInterior &&
            (family.Id == "stone-temple" && family.Step == 10 || family.Id == "zuma-temple-stone" && family.Step == 6) &&
            (specialWalls || existing.Values.Any(s => s.Wall!.SpecialWalls));
        int Seed((int X, int Y) p) => existing.TryGetValue(p, out var old) ? old.Wall!.VariationSeed :
            !family.HasVariations ? 0 : variationSeed is int requestedSeed
                ? (int)(unchecked((uint)requestedSeed * 2654435761u ^ (uint)p.X * 2246822519u ^ (uint)p.Y * 3266489917u) % (int.MaxValue - 1)) + 1
                : Random.Shared.Next(1, int.MaxValue);
        var specialSeeds = useSpecialWalls ? new Dictionary<(int X, int Y), int>() : null;
        // Connector occupancy is derived again from authored placements. This
        // also discards connector rooms left by older disabled routing schemes.
        var occupied = existing.Where(p=>!p.Value.Wall!.AutomaticConnector).Select(p=>p.Key).ToHashSet();
        var before = family.PaintsAreas && !erase ? occupied.ToHashSet() : null;
        var previousJoins = family.FitsNativeCorners || family.PaintsAreas ? WallJoinClearance.Conflicts(family, occupied) : null;
        if(erase && !occupied.Contains(point) && existing.ContainsKey(point) && occupied.Count>0)
            point=occupied.OrderBy(p=>Math.Abs(p.X-point.X)+Math.Abs(p.Y-point.Y)).ThenBy(p=>p.Y).ThenBy(p=>p.X).First();
        bool membershipChanged = false;
        foreach (var requestedPoint in requested)
            membershipChanged |= erase ? occupied.Remove(requested.Length == 1 ? point : requestedPoint) : occupied.Add(requestedPoint);
        if (!erase && !family.SupportsJunctions && occupied.Any(p =>
            family.Directions.Count(d => occupied.Contains((p.X+d.X,p.Y+d.Y))) > 2))
            return new(document, document.PickupRevision, family, [], [], [], [],
                "This wall supports continuous runs and corners. Branches and crossings have no native wall piece.");
        if (membershipChanged && before != null) WallPaintedArea.Merge(family,occupied,before,requested);
        if (membershipChanged && previousJoins != null &&
            WallJoinClearance.Conflicts(family, occupied).Except(previousJoins).Any())
            return new(document, document.PickupRevision, family, [], [], [], [],
                family.StrokeGridPieces > 1
                    ? "Wall join skipped: the corner pieces do not fit here."
                    : "Wall join skipped: sections must share an edge.");
        var authoredPoints=occupied.ToHashSet();
        var vertexNeighbours = family.VertexNeighbours;
        var connectors = new HashSet<(int X,int Y)>();
        if (family.ConnectTouchingSections)
        {
            var authored = occupied.ToHashSet();
            foreach (var origin in authored)
            foreach (var direction in family.Directions)
                if (authored.Contains((origin.X+2*direction.X,origin.Y+2*direction.Y)) &&
                    !authored.Contains((origin.X+direction.X,origin.Y+direction.Y)))
                {
                    var bridge=(X:origin.X+direction.X,Y:origin.Y+direction.Y);
                    occupied.Add(bridge);connectors.Add(bridge);
                }
            foreach (var origin in authored.OrderBy(p=>p.Y).ThenBy(p=>p.X))
            foreach (var vertex in vertexNeighbours.Where(d=>d.Bit is 1 or 8))
            {
                var neighbour = (X:origin.X+vertex.X,Y:origin.Y+vertex.Y);
                if (!authored.Contains(neighbour)) continue;
                var between = family.Directions.Select(d=>(X:origin.X+d.X,Y:origin.Y+d.Y))
                    .Where(p=>family.Directions.Any(d=>p.X+d.X==neighbour.X && p.Y+d.Y==neighbour.Y)).ToList();
                if (between.Any(authored.Contains)) continue;
                foreach (var bridge in between)
                { occupied.Add(bridge); connectors.Add(bridge); }
            }
        }
        if (family.TurnClearancePieces > 0)
        {
            occupied=WallTurnRouting.Fit(family,occupied);
            connectors=occupied.Except(authoredPoints).ToHashSet();
        }
        if (!WallLinePlacement.TryOffsets(family, occupied, out var lineOffsets))
            return new(document, document.PickupRevision, family, [], [], [], [],
                "Wall join skipped: the native corner heights do not meet on this outline.");
        var components = family.SpriteFootprints.Count > 0 ? WallSeparatePlacement.Components(family,occupied) : null;
        var affected = family.Directions.Select(d => (X: point.X + d.X, Y: point.Y + d.Y)).Append(point)
            .Concat(vertexNeighbours.Where(d => d.Bit <= 2).Select(d => (X: point.X + d.X, Y: point.Y + d.Y))).ToHashSet();
        if (family.DrawInterior || family.FourVertexCorners || family.AllowsFilledSections || family.LinePortOffsets.Count > 0)
        {
            affected.UnionWith(vertexNeighbours.Select(d => (point.X + d.X, point.Y + d.Y)));
            // A boundary can occlude a rear face across another section's
            // vertex. Resolve its whole boundary together, including unchanged
            // neighbour masks, so hidden faces reappear when the outline changes.
            affected.UnionWith(existing.Keys);
            affected.UnionWith(occupied);
            affected.UnionWith(authoredPoints);
        }
        bool rebuildNetwork = (family.DrawInterior || family.FourVertexCorners || family.AllowsFilledSections || family.LinePortOffsets.Count > 0) && (membershipChanged ||
            existing.Values.Any(s => s.Wall!.LayoutRevision != family.LayoutRevision || useSpecialWalls && !s.Wall.SpecialWalls || !Intact(document, s)));
        Dictionary<(int X,int Y),List<WallPart>>? nativeParts = null;
        bool useDoors = family.SupportsDoors && (family.RequestedDoorCount > 0 || existing.Values.Any(s=>s.Wall!.DoorCount>0));
        if (family.NativeTrims.Count > 0 || useDoors || useSpecialWalls)
        {
            // A full native shoulder can cover several one-piece extensions.
            // Resolve its ports across the boundary rather than clipping each
            // small placement as if it were an independent room.
            nativeParts = occupied.ToDictionary(p => p, p =>
            {
                int mask = family.Directions.Where(d => occupied.Contains((p.X+d.X,p.Y+d.Y))).Sum(d=>d.Bit);
                int corners = vertexNeighbours.Where(d => d.Bit <= 2 || family.DrawInterior || family.FourVertexCorners)
                    .Where(d => !occupied.Contains((p.X+d.X,p.Y+d.Y))).Sum(d=>d.Bit);
                return new List<WallPart>(family.Variant(mask,corners).Parts);
            });
            var trimsByCorner=family.NativeTrims.Where(t=>t.Interior==null || t.Interior==family.DrawInterior).ToLookup(t=>t.CornerPieceId);
            var cornerLocations = nativeParts.SelectMany(entry => entry.Value.Select(part =>
                (components?.GetValueOrDefault(entry.Key) ?? 0,part.PieceId,entry.Key.X+part.X,entry.Key.Y+part.Y))).ToHashSet();
            var trims = new Dictionary<(int Component,string Id,int X,int Y),Dictionary<(string Id,int X,int Y),WallNativeTrim>>();
            foreach (var (anchor,parts) in nativeParts)
            foreach (var corner in parts.Where(p=>p.CellSelection==null && p.FrontSelection==null && p.FrontColumnSelection==null))
            foreach (var trim in trimsByCorner[corner.PieceId])
            {
                if (trim.RequiredCorner is { } required && !cornerLocations.Contains(
                    (components?.GetValueOrDefault(anchor) ?? 0,required.PieceId,
                        anchor.X+corner.X+required.X,anchor.Y+corner.Y+required.Y))) continue;
                var target = (components?.GetValueOrDefault(anchor) ?? 0,trim.PieceId,anchor.X+corner.X+trim.X,anchor.Y+corner.Y+trim.Y);
                if (!trims.TryGetValue(target, out var covers)) trims[target] = covers = [];
                // A mode-specific rule supersedes the same corner's generic
                // rule. Different shoulders can cover different columns.
                var owner = family.FitsNativeCorners
                    ? (corner.PieceId,anchor.X+corner.X,anchor.Y+corner.Y) : ("",0,0);
                // A shared cap has a dedicated recipe when both shoulders
                // meet it; either shoulder's individual rule is less specific.
                if (trim.RequiredCorner != null || !covers.TryGetValue(owner,out var previous) || previous.RequiredCorner == null)
                    covers[owner] = trim;
            }
            foreach (var (anchor,parts) in nativeParts)
            for (int i=parts.Count-1;i>=0;i--)
            {
                var part=parts[i];
                if (!trims.TryGetValue((components?.GetValueOrDefault(anchor) ?? 0,part.PieceId,anchor.X+part.X,anchor.Y+part.Y),out var covers)) continue;
                if (covers.Values.Any(t=>t.ReplacementPieceId==null && t.FrontColumnSelection==null)) { parts.RemoveAt(i); continue; }
                var columns = part.FrontColumnSelection;
                foreach (var trim in covers.Values)
                {
                    if (trim.FrontColumnSelection is { } remaining)
                        columns = columns == null ? remaining : columns.Intersect(remaining).ToList();
                    part = part with { PieceId=trim.ReplacementPieceId??part.PieceId, Choices=null, VariableFootprint=false };
                }
                if (columns is { Count: 0 }) parts.RemoveAt(i);
                else parts[i]=part with { FrontColumnSelection=columns };
            }
            foreach (var parts in nativeParts.Values)
            {
                // Opaque backing belongs to its roof card. Shoulder port trims
                // must remove both, including individual discarded columns.
                var roofs=parts.Where(p=>p.Attachment==null).ToArray();
                parts.RemoveAll(p=>p.Attachment is { } a && !roofs.Any(r=>
                    r.PieceId==a.PieceId && r.X==a.X && r.Y==a.Y &&
                    (r.FrontColumnSelection==null || r.FrontColumnSelection.Contains(a.FrontColumn))));
            }
        }
        IReadOnlyList<GeneratedSideRoom> sideRooms = [];
        if (useSpecialWalls)
        {
            foreach (var p in occupied) specialSeeds![p] = Seed(p);
            if (family.Id == "stone-temple") StoneTempleSpecialWalls.Apply(family, nativeParts!, occupied, specialSeeds!);
            else sideRooms = ZumaTempleSideRooms.Apply(document, family, nativeParts!, occupied, specialSeeds!);
            affected.UnionWith(occupied);
        }
        var sideRoomOpen = ZumaTempleSideRooms.OpenCells(family, sideRooms);
        var doorSettings = useDoors ? WallEntrances.Prepare(document,family,nativeParts!,occupied,existing,requested,erase,variationSeed) : [];
        if (useDoors)
        {
            affected.UnionWith(existing.Keys);
            affected.UnionWith(occupied);
            rebuildNetwork |= membershipChanged || occupied.Any(p=>existing.TryGetValue(p,out var s) &&
                (s.Wall!.DoorCount,s.Wall.DoorSeed)!=doorSettings[p]);
        }
        var removed = new List<PlacedStamp>();
        var replacements = new List<WallReplacement>();
        foreach (var p in affected.OrderBy(p => p.Y).ThenBy(p => p.X))
        {
            bool old = existing.TryGetValue(p, out var stamp), added = occupied.Contains(p);
            // Keep the user's brush positions for later extensions, erasing and
            // undo, even when a tight turn has moved its visible native shoulder.
            if(!added && authoredPoints.Contains(p))
            {
                if(!rebuildNetwork && old && stamp!.Wall!.Mask==15 && stamp.Wall.Corners==0 && stamp.Cells.Count==1 && stamp.Cells[0].Cell.Empty)continue;
                if(old)removed.Add(stamp!);
                replacements.Add(new(p.X,p.Y,15,0,[new StampCell(0,0,new Cell())],AutomaticConnector:false));
                continue;
            }
            int mask = family.Directions.Where(d => occupied.Contains((p.X + d.X, p.Y + d.Y))).Sum(d => d.Bit);
            // Two sections touching at an east/west vertex share plain boundaries.
            // Applying both authored corner pieces there would overlap their sprites.
            int corners = vertexNeighbours.Where(d => d.Bit <= 2 || family.DrawInterior || family.FourVertexCorners)
                .Where(d => !occupied.Contains((p.X + d.X, p.Y + d.Y))).Sum(d => d.Bit);
            if (!rebuildNetwork && old && added && stamp!.Wall!.Mask == mask && stamp.Wall.Corners == corners && stamp.Wall.Interior == family.DrawInterior && (family.ConnectTouchingSections ? stamp.Wall.AutomaticConnector==connectors.Contains(p) : !stamp.Wall.AutomaticConnector) && stamp.Wall.LayoutRevision == family.LayoutRevision && Intact(document, stamp)) continue;
            if (old) removed.Add(stamp!);
            if (added)
            {
                int seed = specialSeeds?.GetValueOrDefault(p) ?? Seed(p);
                var parts = nativeParts?.GetValueOrDefault(p);
                if (family.LinePortOffsets.Count > 0 && System.Numerics.BitOperations.PopCount((uint)mask) > 2)
                    parts = WallLinePlacement.JunctionParts(family,p,mask,corners,occupied,lineOffsets);
                var composed = family.Compose(mask,corners,seed,parts);
                if (lineOffsets.TryGetValue(p, out int offset) && offset != 0)
                    composed = WallLinePlacement.Translate(composed, offset);
                var layout = composed.Cells;
                if (family.DrawInterior)
                {
                    var specialCollision = !useSpecialWalls ? null : family.Id == "stone-temple"
                        ? StoneTempleSpecialWalls.Collision(family, parts!) : ZumaTempleSideRooms.Collision(family, parts!);
                    layout = layout.Select(s =>
                    {
                        var cell = s.Cell.Copy();
                        if (InteriorContains(family, occupied, p.X + s.X, p.Y + s.Y)) cell.BackImage &= ~0x60000000;
                        cell.BackImage |= specialCollision?.GetValueOrDefault((s.X, s.Y)) ?? 0;
                        if (sideRoomOpen.Contains((p.X + s.X, p.Y + s.Y))) cell.BackImage &= ~0x60000000;
                        return s with { Cell = cell };
                    }).Where(s => !s.Cell.Empty).ToList();
                    if (layout.Count == 0) layout = new[] { new StampCell(0, 0, new Cell()) };
                }
                replacements.Add(new(p.X, p.Y, mask, corners, layout, seed, composed.Priorities,
                    AutomaticConnector:connectors.Contains(p), SpriteDepths:family.NativeTrims.Count > 0 || useDoors ? composed.Depths : null,
                    DoorCount:doorSettings.GetValueOrDefault(p).Count, DoorSeed:doorSettings.GetValueOrDefault(p).Seed, SpecialWalls:useSpecialWalls));
            }
        }
        if (!erase && membershipChanged && components != null)
        {
            var growing = requested.Where(p=>!existing.ContainsKey(p)).Select(p=>components[p]).ToHashSet();
            if (WallSeparatePlacement.Overlaps(document,family,components,replacements,growing))
                return new(document,document.PickupRevision,family,[],[],[],[],
                    "Wall join skipped: separate areas overlap here.");
        }
        return document.PlanWallReplacement(family, removed, replacements, clipToMap) with { SideRooms = sideRooms };
    }

    private static bool InteriorContains(WallFamily family, HashSet<(int X, int Y)> occupied, int x, int y)
    {
        var nearest = family.Snap(x, y);
        int dx = x - nearest.X, dy = y - nearest.Y;
        if (family.ContainsOffset(dx, dy)) return occupied.Contains(nearest);
        var candidates = family.Directions.Select(d => (nearest.X + d.X, nearest.Y + d.Y)).Append(nearest)
            .Concat(family.VertexNeighbours.Select(d => (nearest.X + d.X, nearest.Y + d.Y)));
        int containing = candidates.Count(p => occupied.Contains(p) && family.ContainsOffset(p.Item1 - x, p.Item2 - y, true));
        var (u, v) = family.LatticeCoordinates(dx, dy);
        return containing >= (Math.Abs(u) == 0.5 && Math.Abs(v) == 0.5 ? 4 : 2);
    }

    private static bool Intact(MapDocument document, PlacedStamp stamp) => stamp.Cells.All(s =>
        s.Cell.ForegroundImages.All(f => document.Get(stamp.X + s.X, stamp.Y + s.Y).ForegroundImages.Contains(f)) &&
        (document.Get(stamp.X + s.X, stamp.Y + s.Y).BackImage & s.Cell.BackImage & 0x60000000) == (s.Cell.BackImage & 0x60000000));

    public static IEnumerable<(int X, int Y)> Line(WallFamily family, (int X, int Y) from, (int X, int Y) to, int spacing = 1)
    {
        var start = family.Snap(from.X, from.Y);
        var end = family.Snap(to.X, to.Y);
        var startGrid = family.LatticeCoordinates(start.X, start.Y);
        var endGrid = family.LatticeCoordinates(end.X, end.Y);
        int u = (int)Math.Round(startGrid.U), v = (int)Math.Round(startGrid.V);
        int eu = (int)Math.Round(endGrid.U), ev = (int)Math.Round(endGrid.V);
        if (spacing < 1 || (eu-u)%spacing != 0 || (ev-v)%spacing != 0)
            throw new ArgumentException("Stroke endpoints must share the same drawing grid.");
        int du = Math.Abs(eu - u)/spacing, dv = Math.Abs(ev - v)/spacing, su = Math.Sign(eu - u), sv = Math.Sign(ev - v);
        int a = 0, b = 0;
        yield return start;
        // A four-connected supercover on the wall lattice keeps fast drags connected.
        while ((a < du || b < dv) && a + b < 4096)
        {
            if (a < du && (b == dv || (1L + 2 * a) * dv <= (1L + 2 * b) * du)) { u += su*spacing; a++; }
            else { v += sv*spacing; b++; }
            yield return family.GridPoint(u, v);
        }
    }
}

public sealed partial class MapDocument
{
    // Stamp instances are immutable history entries. Weak keys keep this index
    // reusable through strokes/undo without retaining discarded map history.
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<PlacedStamp,
        Dictionary<(int X,int Y),(StampCell Source,StampDelta Delta)>> wallFootprints=new();

    internal WallPlan PlanWallReplacement(WallFamily family, List<PlacedStamp> removed, List<WallReplacement> replacements, bool clipToMap = true)
    {
        if (!clipToMap && replacements.Any(r => r.Cells.Any(c => !Contains(r.X + c.X, r.Y + c.Y))))
            return new(this, PickupRevision, family, removed, replacements, [], [], "The complete native walls do not fit. Reduce room size or use a larger map.");
        // Clip before resolving ownership, collisions or history. The snapped
        // anchor can lie outside the map; its surviving cells and wall node
        // still need their original coordinates to reconnect on later strokes.
        replacements = replacements.Select(p =>
        {
            var cells = p.Cells.Where(s => Contains(p.X+s.X,p.Y+s.Y)).ToList();
            // Filled sections can have no visible cells. Retain their topology
            // with an in-bounds empty cell, as required by saved stamp metadata.
            if (cells.Count == 0)
                cells.Add(new StampCell(Math.Clamp(p.X,0,Width-1)-p.X,
                    Math.Clamp(p.Y,0,Height-1)-p.Y,new Cell()));
            return p with { Cells=cells };
        }).ToList();
        var overlay = new Dictionary<(int X, int Y), Cell>();
        Cell Read(int x, int y) => overlay.GetValueOrDefault((x, y)) ?? Get(x, y);
        WallPlan Failed(string reason) => new(this, PickupRevision, family, removed, replacements, [], [], reason);
        var ids = removed.Select(s => s.Id).ToHashSet();
        var oldByAnchor = removed.GroupBy(s => (s.X,s.Y)).ToDictionary(g => g.Key,g => g.First());
        // Index untouched contributions once per cell. Looking through every
        // stamp's complete footprint inside the cell loop makes strokes quadratic.
        var retained = new Dictionary<(int X,int Y),(int Collision,int AddedCollision,List<Front?> Fronts)>();
        (int Collision,int AddedCollision,List<Front?> Fronts) Retained(int x,int y)
        {
            if (retained.TryGetValue((x,y),out var cached)) return cached;
            int collision=0,addedCollision=0;
            var fronts=new List<Front?>();
            if (stampIndex.TryGetValue(y*Width+x,out var owners))
            foreach (var owner in owners)
            {
                if (ids.Contains(owner.Id)) continue;
                var entry=Footprint(owner)[(x-owner.X,y-owner.Y)];
                int added=(entry.Delta.After.BackImage&~entry.Delta.Before.BackImage)&0x60000000;
                collision |= owner.Wall!=null ? entry.Source.Cell.BackImage&0x60000000 : added;
                if (owner.Wall==null) continue;
                addedCollision |= added;
                fronts.Add(entry.Source.Cell.Foreground);
            }
            return retained[(x,y)]=(collision,addedCollision,fronts);
        }
        static Dictionary<(int X,int Y),(StampCell Source,StampDelta Delta)> Footprint(PlacedStamp stamp) =>
            wallFootprints.GetValue(stamp,s=>s.Cells.Zip(s.Changes).ToDictionary(p=>(p.First.X,p.First.Y),p=>(p.First,p.Second)));
        // Adjacent interiors can meet at a north/south vertex. The inner front
        // face covers the rear mask there, across section ownership boundaries.
        // Resolve the complete replacement batch before recording any deltas.
        var winners = new Dictionary<(int X, int Y), (Front Front,int Priority,int Y,int X,string Id)>();
        foreach (var replacement in replacements)
        foreach (var source in replacement.Cells)
        foreach (var front in source.Cell.ForegroundImages)
        {
            var point = (replacement.X + source.X, replacement.Y + source.Y);
            int priority = replacement.SpritePriorities?.GetValueOrDefault((source.X,source.Y)) ?? 0;
            var localDepth = replacement.SpriteDepths?.GetValueOrDefault((source.X,source.Y)) ?? (0,0,"");
            var depth = (replacement.Y+localDepth.Item1,replacement.X+localDepth.Item2,localDepth.Item3);
            if (family.AllowsFilledSections && winners.TryGetValue(point, out var owner) &&
                (owner.Priority > priority || owner.Priority == priority &&
                 Comparer<(int,int,string)>.Default.Compare(depth,(owner.Y,owner.X,owner.Id)) <= 0)) continue;
            if (winners.TryGetValue(point, out var same) && same.Front == front && same.Priority > priority) continue;
            if (winners.TryGetValue(point, out var previous) && previous.Front != front)
            {
                if (previous.Priority == priority)
                {
                    if (!family.AllowsFilledSections) return Failed($"Wall faces overlap at ({point.Item1}, {point.Item2})");
                    if (Comparer<(int,int,string)>.Default.Compare(depth,(previous.Y,previous.X,previous.Id)) <= 0) continue;
                }
                if (previous.Priority > priority) continue;
            }
            winners[point] = (front,priority,depth.Item1,depth.Item2,depth.Item3);
        }
        replacements = replacements.Select(p => p with { Cells = p.Cells.Select(s =>
        {
            if (s.Cell.ForegroundImages.Count==0 || s.Cell.ForegroundImages.All(f=>winners[(p.X+s.X,p.Y+s.Y)].Front==f)) return s;
            var cell = s.Cell.Copy();
            if (cell.Foreground != winners[(p.X + s.X, p.Y + s.Y)].Front) cell.Foreground = null;
            return s with { Cell = cell };
        }).ToList() }).ToList();
        // Resolve winners over the complete outline, but rebuild ownership only
        // at cells whose contributing artwork changed. Every owner of such a
        // cell participates in order; unchanged cells keep their original deltas.
        // This preserves shared sprite/collision ownership without copying the
        // entire room's cell history for a small change at one edge.
        var changedPoints=new HashSet<(int X,int Y)>();
        var replacing=replacements.Select(p=>(p.X,p.Y)).ToHashSet();
        foreach (var old in removed)
        foreach (var source in old.Cells)
        {
            int x=old.X+source.X,y=old.Y+source.Y;
            var current=Get(x,y);
            if (!replacing.Contains((old.X,old.Y)) ||
                !source.Cell.ForegroundImages.All(current.ForegroundImages.Contains) ||
                (current.BackImage&source.Cell.BackImage&0x60000000)!=(source.Cell.BackImage&0x60000000))
                changedPoints.Add((x,y));
        }
        foreach (var replacement in replacements)
        {
            var old=oldByAnchor.GetValueOrDefault((replacement.X,replacement.Y));
            var previous=old==null ? null : Footprint(old);
            foreach (var source in replacement.Cells)
                if (previous==null || !previous.TryGetValue((source.X,source.Y),out var entry) || !source.Cell.SameContents(entry.Source.Cell))
                    changedPoints.Add((replacement.X+source.X,replacement.Y+source.Y));
            if (old==null) continue;
            var present=replacement.Cells.Select(s=>(s.X,s.Y)).ToHashSet();
            foreach (var source in old.Cells)
                if (!present.Contains((source.X,source.Y))) changedPoints.Add((old.X+source.X,old.Y+source.Y));
        }
        foreach (var stamp in removed.AsEnumerable().Reverse())
        foreach (var delta in stamp.Changes)
        {
            int x=stamp.X+delta.X,y=stamp.Y+delta.Y;
            if (!changedPoints.Contains((x,y))) continue;
            overlay[(x,y)]=RemoveContribution(Read(x,y),delta);
            overlay[(x,y)].BackImage |= Retained(x,y).Collision;
        }
        var stamps = new List<PlacedStamp>();
        var batchCollision = new Dictionary<(int X,int Y),int>();
        var batchFronts = new Dictionary<(int X,int Y),List<Front?>>();
        foreach (var replacement in replacements)
        {
            var old = oldByAnchor.GetValueOrDefault((replacement.X,replacement.Y));
            var deltas = new List<StampDelta>();
            var sourceCells = new List<StampCell>();
            foreach (var source in replacement.Cells)
            {
                int x = replacement.X + source.X, y = replacement.Y + source.Y;
                if (!changedPoints.Contains((x,y)))
                {
                    var entry=Footprint(old!)[(source.X,source.Y)];
                    deltas.Add(entry.Delta);
                    sourceCells.Add(entry.Source);
                    continue;
                }
                sourceCells.Add(source with { Cell=source.Cell.Copy() });
                var before = Read(x, y).Copy();
                var other=Retained(x,y);
                bool existingWallSprite = before.ForegroundImages.Count > 0 &&
                    (batchFronts.TryGetValue((x,y),out var previousFronts) && previousFronts.Contains(before.Foreground) ||
                        other.Fronts.Contains(before.Foreground));
                if (!source.Cell.Empty && before.ForegroundImages.Count > 0 && !existingWallSprite)
                    return Failed($"Wall blocked by an existing object at ({x}, {y})");
                if (source.Cell.ForegroundImages.Count > 0 && before.ForegroundImages.Any(f => !source.Cell.ForegroundImages.Contains(f)))
                    return Failed($"Wall blocked by an existing object at ({x}, {y})");
                var after = MergeStamp(before, source.Cell);
                overlay[(x, y)] = after;
                var owned = source.Cell.ForegroundImages.Where(f => !before.ForegroundImages.Contains(f)).ToList();
                // Every section owns its collision footprint independently. Store
                // the underlying flags rather than flags supplied by another wall,
                // so deleting shared owners in any order cannot leave ghost blocks.
                before.BackImage &= ~(source.Cell.BackImage & (other.AddedCollision | batchCollision.GetValueOrDefault((x,y))));
                deltas.Add(new(source.X, source.Y, before, after.Copy(), owned,
                    owned.Select(f => after.ForegroundImages.IndexOf(f)).ToList()));
            }
            var node=new WallNode(family.Id, replacement.Mask, replacement.Corners, family.DrawInterior, replacement.VariationSeed,
                family.LayoutRevision, replacement.AutomaticConnector, family.PreserveLegacyStep==0 ? 0 : family.Step,
                replacement.DoorCount, replacement.DoorSeed, replacement.SpecialWalls);
            stamps.Add(old!=null && old.Wall==node && deltas.SequenceEqual(old.Changes) ? old :
                new(old?.Id ?? Guid.NewGuid(), family.Name, replacement.X, replacement.Y,sourceCells,deltas,node));
            foreach (var delta in deltas)
            {
                var point = (replacement.X + delta.X, replacement.Y + delta.Y);
                if (!changedPoints.Contains(point)) continue;
                batchCollision[point] = batchCollision.GetValueOrDefault(point) |
                    (delta.After.BackImage & ~delta.Before.BackImage & 0x60000000);
            }
            foreach (var source in replacement.Cells)
            {
                if (source.Cell.ForegroundImages.Count==0) continue;
                var point=(replacement.X+source.X,replacement.Y+source.Y);
                if (!changedPoints.Contains(point)) continue;
                if (!batchFronts.TryGetValue(point,out var fronts)) batchFronts[point]=fronts=[];
                fronts.Add(source.Cell.Foreground);
            }
        }
        if (placedStamps.Count - removed.Count + stamps.Count > 100000) return Failed("Wall instance limit reached");
        // Ownership can change without changing the final tile. Preserve the
        // new stamps, but do not invalidate or record unchanged map cells.
        foreach (var point in overlay.Keys.ToArray())
            if (overlay[point].SameContents(Get(point.X,point.Y))) overlay.Remove(point);
        return new(this, PickupRevision, family, removed, replacements, overlay, stamps, null);
    }

    public bool ApplyWallPlan(WallPlan plan, EditHistory history, Action<int, int> invalidate)
    {
        if (!ReferenceEquals(plan.Document, this) || plan.Revision != PickupRevision || !plan.Allowed || !plan.HasChanges) return false;
        history.RecordStamps(this);
        foreach (var ((x, y), cell) in plan.Cells)
        { history.Record(this, x, y); Set(x, y, cell.Copy()); invalidate(x, y); }
        var removed = plan.Removed.Select(s => s.Id).ToHashSet();
        RestoreStamps(placedStamps.Where(s => !removed.Contains(s.Id)).Concat(plan.Stamps));
        return true;
    }
}
