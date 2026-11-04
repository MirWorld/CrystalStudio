using System.IO;
using System.Text.Json;

namespace CrystalStudio;

public sealed record TerrainCoastPlacement(RiverCoastLayout Layout, int X, int Y)
{
    public bool DiagonalAccess { get; init; }
}

/// <summary>Places native saved coast cells. All seam covers belong to the reusable pieces.</summary>
public static partial class TerrainCoastObjects
{
    internal const string StampPrefix = "Generated coast .X · ";
    internal const string NorthwestCoverVariantId = "north-west-required-cover-00203";
    public static bool Fits(RiverCoastLayout layout, TerrainArea area,int mapWidth=0,int mapHeight=0) => TryOrigins(layout,area,mapWidth,mapHeight,out _,out _,out _,out _);

    private static bool TryOrigins(RiverCoastLayout layout,TerrainArea area,int mapWidth,int mapHeight,out int firstX,out int firstY,out int columns,out int rows,int margin=8)
    {
        // Space for the reviewed terrain transition around inland banks.
        int minX=area.X+margin,maxX=area.X+area.Width-layout.Width-margin;
        int minY=area.Y+margin,maxY=area.Y+area.Height-layout.Height-margin;
        bool boundary=true;
        IReadOnlyList<string> edges=layout.BoundaryEdges.Count>0?layout.BoundaryEdges:layout.EntryEdge==""?[]:new[]{layout.EntryEdge};
        foreach(string edge in edges)
        {
            if(edge=="north") { boundary&=area.Y==0&&layout.Height+margin<=area.Height;minY=maxY=0; }
            else if(edge=="west") { boundary&=area.X==0&&layout.Width+margin<=area.Width;minX=maxX=0; }
            else if(edge=="east") { boundary&=mapWidth>0&&area.X+area.Width==mapWidth&&layout.Width+margin<=area.Width;minX=maxX=mapWidth-layout.Width; }
            else if(edge=="south") { boundary&=mapHeight>0&&area.Y+area.Height==mapHeight&&layout.Height+margin<=area.Height;minY=maxY=mapHeight-layout.Height; }
        }
        firstX=(minX+1)&~1;firstY=(minY+1)&~1;maxX&=~1;maxY&=~1;
        columns=firstX<=maxX?(maxX-firstX)/2+1:0;rows=firstY<=maxY?(maxY-firstY)/2+1:0;
        return boundary&&columns>0&&rows>0;
    }

    public static IReadOnlyList<RiverCoastLayout> Load(AssetStore assets, CancellationToken cancellation = default)
    {
        // Whole river presets are retired. Reusable pieces are verified here,
        // but an open bank chain is not a complete playable river.
        _ = RiverCoastPieces.Load(assets,cancellation);
        return [];
    }

    public static RiverCoastPieces LoadWindingPieces(AssetStore assets,CancellationToken cancellation=default,bool bankOnly=false)
    {
        string root=DataPaths.ResolveEditor("catalogue","river-coast-layouts");
        var banks=RiverCoastPieces.Load(assets,cancellation,Path.Combine(root,"winding"));
        banks=banks.WithCap(RiverDiagonalCapComponent.Load(banks,Path.Combine(root,"caps","diagonal-gentle-cap.X")));
        banks=banks.WithForks(RiverForkComponents.Load(banks,Path.Combine(root,"forks"),completeCover:bankOnly));
        var artwork=RiverArtworkCatalogue.Load(DataPaths.ResolveEditor("catalogue","river-artwork.json"),true)
            .PrepareSavedLayouts(assets,cancellation);
        banks=banks.WithSavedCoverVariant("north-west-flexible-join",NorthwestCoverVariantId,
            Path.Combine(root,"variants",NorthwestCoverVariantId+".X"),artwork,assets,cancellation);
        if(bankOnly)banks=banks.WithReturn(RiverNativeReturnComponent.Load(banks,Path.Combine(root,"bridges","native-1384-1393-m2-m9.X")));
        return bankOnly?banks.WithNativeBankOverlay(RiverNativeBankOverlayComponent.Load(banks,
            Path.Combine(root,"variants","west-diagonal-parallel-8-9-native-overlay.X"))):banks;
    }

    public static RiverCoastPieces LoadBridgePieces(AssetStore assets,CancellationToken cancellation=default,bool legacyResearch=false)
    {
        var banks=LoadWindingPieces(assets,cancellation);
        string root=DataPaths.ResolveEditor("catalogue","river-coast-layouts");
        if(!legacyResearch)
        {
            banks=banks.WithReturn(RiverNativeReturnComponent.Load(banks,Path.Combine(root,"bridges","native-1384-1393-m2-m9.X")));
            banks=banks.WithSouthDiagonalReturn(RiverSouthDiagonalComponent.Load(banks,Path.Combine(root,"bridges","native-south-diagonal-return.X")));
        }
        return banks.WithBridge(RiverBridgeComponent.Load(assets,banks,Path.Combine(root,"bridges",legacyResearch?"bridge-crossing.X":"bridge-plain-bank.X"),
            Path.Combine(root,"modules","province-cliff-bridge-crossing.X"),cancellation));
    }

    public static bool FitsBridge(RiverCoastPieces pieces,TerrainArea area,bool branches)
        =>UsesGentleBridge(pieces)?!branches&&RiverWindingPlanCache.Build(pieces,0,area.Width-8,area.Height-8,out _,bridge:true)!=null:
            HasGradualTurns(branches?RiverPieceRiverBuilder.ResearchBridgedBranching(pieces,0,area.Width-8,area.Height-8,out _):
            RiverPieceRiverBuilder.ResearchBridged(pieces,0,area.Width-8,area.Height-8,out _));

    private static bool UsesGentleBridge(RiverCoastPieces pieces)=>pieces.Pieces.Any(p=>p.Id=="bridge-west-shore"
        &&p.BridgeComponent!=null&&((p.End.BankX,p.End.BankY) is (5,16) or (15,66)));

    public static bool FitsWinding(RiverCoastPieces pieces,TerrainArea area)
        =>RiverWindingPlanCache.Build(pieces,0,area.Width-8,area.Height-8,out _)!=null;

    public static bool FitsBranching(RiverCoastPieces pieces,TerrainArea area)
        =>RiverWindingPlanCache.Build(pieces,0,area.Width-8,area.Height-8,out _,branches:true)!=null;

    private static bool HasGradualTurns(RiverPieceRiverBuilder.WindingResult? built)
        =>built?.PreparedGround is {} ground&&RiverShapeQuality.Analyse(ground).Elbows.Count==0;

    /// <summary>Offline regression fixtures only; never called by the generator window.</summary>
    public static IReadOnlyList<RiverCoastLayout> LoadReferenceCourses(AssetStore assets, string directory, CancellationToken cancellation = default)
    {
        var artwork = RiverArtworkCatalogue.Load(Path.Combine(directory, "river-artwork.json"), true).PrepareSavedLayouts(assets, cancellation);
        string chains = Path.Combine(directory,"river-coast-layouts","chains");
        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(chains,"river-layouts.json")));
        var root = manifest.RootElement;
        if (root.GetProperty("schemaVersion").GetInt32()!=1||root.GetProperty("placementAlignmentCells").GetInt32()!=2)
            throw new InvalidDataException("Invalid saved river layout index.");
        var entries = root.GetProperty("layouts").EnumerateArray().ToArray();
        if (entries.Length is <1 or >100) throw new InvalidDataException("Invalid saved river layout count.");
        var layouts = new List<RiverCoastLayout>();
        foreach (var entry in entries)
        {
            string file=entry.GetProperty("file").GetString()??"";
            if (file!=Path.GetFileName(file)||!file.EndsWith(".X",StringComparison.Ordinal)||file[..^2].Length is <1 or >80
                ||!file[..^2].All(c=>char.IsAsciiLetterOrDigit(c)||c=='-')) throw new InvalidDataException("Invalid saved river object path.");
            string path=Path.Combine(chains,file);
            var layout=RiverCoastLayoutCatalogue.LoadComplete(path,artwork,assets,true,cancellation);
            if (layouts.Any(l=>l.Id==layout.Id)||layout.Width!=entry.GetProperty("width").GetInt32()||layout.Height!=entry.GetProperty("height").GetInt32()
                ||layout.BranchCount!=entry.GetProperty("branches").GetInt32()
                ||layout.EntryEdge!=(entry.TryGetProperty("entryEdge",out var edge)?edge.GetString()??"":"")
                ||entry.TryGetProperty("boundaryEdges",out var boundaries)&&!layout.BoundaryEdges.SequenceEqual(boundaries.EnumerateArray().Select(e=>e.GetString()??""))
                ||entry.TryGetProperty("waterMode",out var mode)&&mode.GetString()!=ModeName(layout.WaterMode)
                ||Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path)))!=entry.GetProperty("sha256").GetString())
                throw new InvalidDataException("Saved river layout differs from its index.");
            layouts.Add(layout);
        }
        return layouts;
    }

    internal static IEnumerable<PlacedStamp> Replaced(MapDocument document, TerrainArea area)
    {
        foreach (var stamp in document.PlacedStamps.Where(s => s.Name.StartsWith(StampPrefix, StringComparison.Ordinal)))
        {
            bool Inside(StampCell c) => stamp.X + c.X >= area.X && stamp.Y + c.Y >= area.Y
                && stamp.X + c.X < area.X + area.Width && stamp.Y + c.Y < area.Y + area.Height;
            if (!stamp.Cells.Any(Inside)) continue;
            if (!stamp.Cells.All(Inside)) throw new ArgumentException("The selection cuts a saved coast object. Include the whole layout.");
            yield return stamp;
        }
    }

    internal static MapDocument WorkingCopy(MapDocument document, TerrainArea area)
    {
        var copy = new MapDocument(document.Width, document.Height);
        foreach (var (key, cell) in document.Cells) copy.Set(key % document.Width, key / document.Width, cell.Copy());
        copy.RestoreStamps(document.PlacedStamps);
        var history = new EditHistory();
        foreach (var stamp in Replaced(copy, area).ToArray()) copy.DeleteStamp(stamp.Id, history, (_, _) => { });
        copy.RemoveGeneratedBanks(area, history, (_, _) => { });
        return copy;
    }

    public static bool FitsShore(RiverShoreComponents components,TerrainArea area)
    {
        if((long)area.Width*area.Height>RiverPieceGroundAssembler.MaximumCoastCells)return false;
        return RiverPieceCoastBuilder.ResearchCurved(components.Banks,components.EvenEdge,0,area.Width,area.Height,
            out _,exactSize:true,endEdge:(area.Height&1)==0?components.EvenEdge:components.OddEdge)!=null
            ||components.HorizontalEdge is {} edge && RiverHorizontalCoastBuilder.Research(components.Banks,edge,
                RiverPieceRiverBuilder.CoastBends,0,area.Width,area.Height,out _)!=null;
    }

    internal static TerrainCoastPlacement PlanShorePieces(MapDocument document, TerrainArea area, TerrainOptions options,
        RiverShoreComponents components, CancellationToken cancellation)
        =>CachedShore(document,area,options,components,false,
            (empty,settings)=>BuildShorePieces(empty,area,settings,components,cancellation),cancellation);

    private static TerrainCoastPlacement BuildShorePieces(MapDocument document, TerrainArea area, TerrainOptions options,
        RiverShoreComponents components, CancellationToken cancellation)
    {
        if (options.WaterMode != TerrainWaterMode.Beach || area.X != 0 || area.Y != 0
            || area.Width != document.Width || area.Height != document.Height)
            throw new ArgumentException("This native coastline needs the whole map and its actual boundaries.");
        if (options.CompactBeach && ProvinceBeach.Cove(document, components.Banks, options.Seed, cancellation) is { } cove)
            return cove;
        var finish = (area.Height & 1) == 0 ? components.EvenEdge : components.OddEdge;
        string failure="";bool horizontal=false;
        RiverPieceCoastBuilder.Result? built=null;
        if(components.HorizontalEdge is {} edge && new Random(options.Seed^0x33713F).Next(2)==0)
        {
            built=RiverHorizontalCoastBuilder.Research(components.Banks,edge,RiverPieceRiverBuilder.CoastBends,
                options.Seed,area.Width,area.Height,out failure,cancellation);
            horizontal=built!=null;
        }
        built??=RiverPieceCoastBuilder.ResearchCurved(components.Banks, components.EvenEdge, options.Seed,
            area.Width, area.Height, out failure, cancellation, exactSize: true, endEdge: finish);
        if(built==null&&components.HorizontalEdge is {} alternate)
        {
            built=RiverHorizontalCoastBuilder.Research(components.Banks,alternate,RiverPieceRiverBuilder.CoastBends,
                options.Seed,area.Width,area.Height,out failure,cancellation);
            horizontal=built!=null;
        }
        if(built==null)throw new ArgumentException(failure);
        var ground = built.Ground;
        var cells = new List<StampCell>();
        foreach (var saved in ground.Cells)
        {
            var cell = saved.Cell.Copy();
            if (!ground.RequiredGroundAnchors.Contains((saved.X, saved.Y))) cell.BackImage &= 0x60000000;
            if (cell.BackImage == 0 && cell.MiddleImage == 0 && cell.ForegroundImages.Count == 0 && cell.Door == 0) continue;
            cells.Add(new(saved.X, saved.Y, cell));
        }
        // Ordinary in-memory ownership of a freshly assembled map, never a whole-course .X asset.
        var layout = new RiverCoastLayout(horizontal?"procedural-native-shore-south":"procedural-native-shore", "Procedural native coast", cells)
            { Width = area.Width, Height = area.Height, WaterMode = TerrainWaterMode.Beach, BoundaryEdges = horizontal?["west","east"]:["north", "south"] };
        if (!CanPlace(document, layout, 0, 0))
            throw new ArgumentException("Existing objects or collisions leave no clear space for the coast banks.");
        return new(layout, 0, 0) { DiagonalAccess = true };
    }

    internal static TerrainCoastPlacement PlanPieces(MapDocument document,TerrainArea area,TerrainOptions options,
        RiverCoastPieces pieces,CancellationToken cancellation)
    {
        if(options.WaterMode is not (TerrainWaterMode.River or TerrainWaterMode.Waterways))
            throw new ArgumentException("Reusable river-mouth bank assemblies are still being reviewed.");
        // This is an ephemeral cell assembly for normal map ownership, never a saved whole-river .X.
        // The assembler's bounds already include four cells of native artwork support.
        const int transitionMargin=4;
        if(options.BridgeCrossing&&options.WaterMode==TerrainWaterMode.Waterways&&UsesGentleBridge(pieces))
            throw new ArgumentException("Gradual native bridge tributaries are still being reviewed.");
        string failure;var built=(options.BridgeCrossing
            ?options.WaterMode==TerrainWaterMode.Waterways
                ?RiverPieceRiverBuilder.ResearchBridgedBranching(pieces,options.Seed,area.Width-2*transitionMargin,area.Height-2*transitionMargin,out failure,cancellation)
                :UsesGentleBridge(pieces)?RiverWindingPlanCache.Build(pieces,options.Seed,area.Width-2*transitionMargin,area.Height-2*transitionMargin,out failure,cancellation,bridge:true):
                    RiverPieceRiverBuilder.ResearchBridged(pieces,options.Seed,area.Width-2*transitionMargin,area.Height-2*transitionMargin,out failure,cancellation)
            :options.WaterMode==TerrainWaterMode.Waterways
            ?RiverWindingPlanCache.Build(pieces,options.Seed,area.Width-2*transitionMargin,area.Height-2*transitionMargin,out failure,cancellation,branches:true)
            :RiverWindingPlanCache.Build(pieces,options.Seed,area.Width-2*transitionMargin,area.Height-2*transitionMargin,out failure,cancellation))
            ??throw new ArgumentException(failure);
        var ground=built.PreparedGround??RiverPieceGroundAssembler.Assemble(pieces,built.Chain,options.Seed,cancellation);
        // Every selectable river mode must pass the same native bank and channel
        // check. Bridges and tributaries must not bypass the winding quality gate.
        // Ordinary winding plans are already checked before entering their cache.
        if((options.BridgeCrossing&&!UsesGentleBridge(pieces))
            &&RiverShapeQuality.Analyse(ground,cancellation).Elbows.Count>0)
            throw new ArgumentException("No reviewed river layout without large square bends is available for these settings. Bridge and tributary pieces still need gradual turns.");
        var cells=new List<StampCell>();
        foreach(var saved in ground.Cells)
        {
            var cell=saved.Cell.Copy();
            // Only native banks and water need their dirt/water underlay. The surrounding
            // terrain is generated through the catalogue's reviewed grass/dirt transitions.
            if(!ground.RequiredGroundAnchors.Contains((saved.X,saved.Y)))cell.BackImage&=0x60000000;
            if(cell.BackImage==0&&cell.MiddleImage==0&&cell.ForegroundImages.Count==0&&cell.Door==0)continue;
            cells.Add(new(saved.X-ground.X,saved.Y-ground.Y,cell));
        }
        var layout=new RiverCoastLayout("procedural-native-"+built.Shape,options.WaterMode==TerrainWaterMode.Waterways?"Procedural native tributary":"Procedural native river",cells)
            {Width=ground.Width,Height=ground.Height,BranchCount=built.BranchCount,WaterMode=options.WaterMode,HasNativeBridge=options.BridgeCrossing};
        if(!TryOrigins(layout,area,document.Width,document.Height,out int firstX,out int firstY,out int columns,out int rows,transitionMargin))
            throw new ArgumentException("The reusable river banks and their land transitions do not fit this area.");
        var random=new Random(options.Seed^0x5A31C029);int count=checked(columns*rows),start=random.Next(count);
        int stride=area.Width+1;
        var occupied=OccupiedPrefix(document,area,cancellation);
        for(int attempt=0;attempt<count;attempt++)
        {
            cancellation.ThrowIfCancellationRequested();int key=(start+attempt)%count;
            int x=firstX+key%columns*2,y=firstY+key/columns*2;
            int left=x-area.X,top=y-area.Y,right=left+layout.Width,bottom=top+layout.Height;
            if(occupied[bottom*stride+right]-occupied[top*stride+right]-occupied[bottom*stride+left]+occupied[top*stride+left]!=0)continue;
            if(CanPlace(document,layout,x,y))return new(layout,x,y){DiagonalAccess=true};
        }
        throw new ArgumentException("Existing objects or collisions leave no clear space for the river banks.");
    }

    // Legacy complete-course fixtures support offline comparisons only.
    internal static TerrainCoastPlacement Plan(MapDocument document, TerrainArea area, TerrainOptions options,
        IReadOnlyList<RiverCoastLayout> layouts, CancellationToken cancellation)
    {
        var random = new Random(options.Seed ^ 0x5A31C029);
        int stride = area.Width + 1;
        var occupied=OccupiedPrefix(document,area,cancellation);
        var candidates = layouts.Where(l => Fits(l,area,document.Width,document.Height)&&l.WaterMode==options.WaterMode).OrderBy(_ => random.Next()).ToArray();
        if (candidates.Length == 0) throw new ArgumentException("No complete saved river layout fits this area with its terrain transitions. Enlarge the area.");
        // Saved native artwork cannot be rotated or stretched. Different courses are separate .X assets.
        foreach (var layout in candidates)
        {
            int width = layout.Width, height = layout.Height;
            if(!TryOrigins(layout,area,document.Width,document.Height,out int firstX,out int firstY,out int columns,out int rows))continue;
            int count = checked(columns * rows), start = random.Next(count);
            for (int attempt = 0; attempt < count; attempt++)
            {
                cancellation.ThrowIfCancellationRequested();
                int key = (start + attempt) % count;
                int x = firstX + key % columns * 2, y = firstY + key / columns * 2;
                int left=x-area.X,top=y-area.Y,right=left+width,bottom=top+height;
                if (occupied[bottom*stride+right]-occupied[top*stride+right]-occupied[bottom*stride+left]+occupied[top*stride+left]!=0) continue;
                if (!CanPlace(document, layout, x, y)) continue;
                return new(layout, x, y);
            }
        }
        throw new ArgumentException("Existing objects or collisions leave no space for a complete saved river. Choose a clear area.");
    }

    private static int[] OccupiedPrefix(MapDocument document,TerrainArea area,CancellationToken cancellation)
    {
        int stride=area.Width+1;
        var occupied=new int[checked(stride*(area.Height+1))];
        foreach(var (key,cell) in document.Cells)
        {
            int x=key%document.Width-area.X,y=key/document.Width-area.Y;
            if(x>=0&&y>=0&&x<area.Width&&y<area.Height&&Protected(cell))occupied[(y+1)*stride+x+1]=1;
        }
        for(int y=1;y<=area.Height;y++)
        {
            cancellation.ThrowIfCancellationRequested();int row=0;
            for(int x=1;x<=area.Width;x++){row+=occupied[y*stride+x];occupied[y*stride+x]=row+occupied[(y-1)*stride+x];}
        }
        return occupied;
    }

    private static string ModeName(TerrainWaterMode mode)=>mode switch
    {
        TerrainWaterMode.River=>"river",TerrainWaterMode.Waterways=>"waterways",TerrainWaterMode.RiverToSea=>"river-mouth",TerrainWaterMode.Beach=>"beach",_=>"none"
    };

    internal static bool CanPlace(MapDocument document, RiverCoastLayout layout, int x, int y)
    {
        if ((x & 1) != 0 || (y & 1) != 0 || !document.CheckStampPlacement(layout.Cells, x, y).Allowed) return false;
        // Include gaps between saved anchors so an existing object cannot protrude into the banks.
        for (int yy=y;yy<y+layout.Height;yy++) for (int xx=x;xx<x+layout.Width;xx++)
            if (!document.Contains(xx,yy)||Protected(document.Get(xx,yy))) return false;
        foreach (var s in layout.Cells)
        {
            // A ground sprite covers four map cells, including odd cells with no saved anchor.
            int extent = (s.Cell.BackImage & 0x1fffffff) == 0 ? 1 : 2;
            for (int dy = 0; dy < extent; dy++) for (int dx = 0; dx < extent; dx++)
            {
                if (!document.Contains(x+s.X+dx,y+s.Y+dy))
                {
                    // Native ground tiles may cover a partial final row/column of an odd-sized map.
                    // All saved anchors and every authored bank remain inside the actual boundary.
                    if (layout.WaterMode is TerrainWaterMode.Beach or TerrainWaterMode.RiverToSea && document.Contains(x+s.X,y+s.Y)) continue;
                    return false;
                }
                var existing = document.Get(x+s.X+dx,y+s.Y+dy);
                if (existing.MiddleImage != 0 || existing.ForegroundImages.Count != 0 || (existing.BackImage & 0x60000000) != 0 || existing.Door != 0) return false;
            }
        }
        return true;
    }

    private static bool Protected(Cell cell) => cell.MiddleImage!=0||cell.ForegroundImages.Count!=0||(cell.BackImage&0x60000000)!=0||cell.Door!=0;

    internal static void VerifyAccess(MapDocument document, TerrainCoastPlacement placement, CancellationToken cancellation)
    {
        // Check the actual saved collision bits against existing obstacles, without adding repairs.
        cancellation.ThrowIfCancellationRequested();
        int width = document.Width, height = document.Height,length=checked(width*height),originalBlocks=0,scanned=0;
        var originalBlocked=new bool[length];
        foreach(var (key,cell) in document.Cells)
        {
            if((scanned++&1023)==0)cancellation.ThrowIfCancellationRequested();
            if(Blocked(cell)){originalBlocked[key]=true;originalBlocks++;}
        }
        var addedBlocked=(bool[])originalBlocked.Clone();
        foreach(var cell in placement.Layout.Cells)
        {
            if((scanned++&1023)==0)cancellation.ThrowIfCancellationRequested();
            int x=placement.X+cell.X,y=placement.Y+cell.Y;
            if(document.Contains(x,y)&&Blocked(cell.Cell))addedBlocked[y*width+x]=true;
        }
        var before = new int[length];
        var queue = new Queue<int>(); var expected = new List<int>();
        if(originalBlocks==0)
        {
            // An unobstructed source is one land region; only the added map needs a flood fill.
            int free=0;for(int key=0;key<length;key++)
            {
                if((key&1023)==0)cancellation.ThrowIfCancellationRequested();
                if(!addedBlocked[key])free++;
            }
            expected.Add(free);
        }
        else
        {
            Array.Fill(before,-1);
            for (int first = 0; first < before.Length; first++)
            {
                if ((first & 1023) == 0) cancellation.ThrowIfCancellationRequested();
                if (before[first] != -1 || originalBlocked[first]) continue;
                int label = expected.Count, survivors = 0,visited=0; before[first] = label; queue.Enqueue(first);
                while (queue.TryDequeue(out int key))
                {
                    if ((visited++ & 1023) == 0) cancellation.ThrowIfCancellationRequested();
                    if (!addedBlocked[key]) survivors++;
                    int x=key%width;
                    if(x>0)Original(key-1,label);if(x+1<width)Original(key+1,label);
                    if(key>=width)Original(key-width,label);if(key+width<length)Original(key+width,label);
                    if(placement.DiagonalAccess||placement.Layout.HasNativeBridge)
                    {
                        // The existing game pathfinder permits all eight directions.
                        if(key>=width){if(x>0)Original(key-width-1,label);if(x+1<width)Original(key-width+1,label);}
                        if(key+width<length){if(x>0)Original(key+width-1,label);if(x+1<width)Original(key+width+1,label);}
                    }
                }
                expected.Add(survivors);
            }
        }
        var seen = new bool[before.Length]; var components = new int[expected.Count];
        for (int first = 0; first < before.Length; first++)
        {
            if ((first & 1023) == 0) cancellation.ThrowIfCancellationRequested();
            if (seen[first] || before[first] == -1 || addedBlocked[first]) continue;
            int label = before[first];
            if (++components[label] > 1) throw new ArgumentException("This saved river would cut off land around existing obstacles. Choose a clear area.");
            int reached = 0; seen[first] = true; queue.Enqueue(first);
            while (queue.TryDequeue(out int key))
            {
                if ((reached & 1023) == 0) cancellation.ThrowIfCancellationRequested();
                reached++;
                int x=key%width;
                if(x>0)Added(key-1,label);if(x+1<width)Added(key+1,label);
                if(key>=width)Added(key-width,label);if(key+width<length)Added(key+width,label);
                if(placement.DiagonalAccess||placement.Layout.HasNativeBridge)
                {
                    if(key>=width){if(x>0)Added(key-width-1,label);if(x+1<width)Added(key-width+1,label);}
                    if(key+width<length){if(x>0)Added(key+width-1,label);if(x+1<width)Added(key+width+1,label);}
                }
            }
            if (reached != expected[label]) throw new ArgumentException("This saved river would disconnect walkable land.");
        }
        static bool Blocked(Cell cell)=>(cell.BackImage&0x60000000)!=0||cell.ForegroundImages.Any(f=>(f.Image&0x8000)!=0);
        void Original(int next,int label)
        {
            if(before[next]!=-1||originalBlocked[next])return;
            before[next]=label;queue.Enqueue(next);
        }
        void Added(int next,int label)
        {
            if(seen[next]||before[next]!=label||addedBlocked[next])return;
            seen[next]=true;queue.Enqueue(next);
        }
    }
}
