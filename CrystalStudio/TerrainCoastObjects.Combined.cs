using System.IO;

namespace CrystalStudio;

public static partial class TerrainCoastObjects
{
    internal static TerrainCoastPlacement PlanRiverAndBeach(MapDocument document,TerrainArea area,TerrainOptions options,
        RiverCoastPieces riverBanks,RiverShoreComponents shore,CancellationToken cancellation)
        =>CachedShore(document,area,options,shore,true,
            (empty,settings)=>BuildRiverAndBeach(empty,area,settings,riverBanks,shore,cancellation),cancellation);

    private static TerrainCoastPlacement CachedShore(MapDocument document,TerrainArea area,TerrainOptions options,
        RiverShoreComponents shore,bool combined,Func<MapDocument,TerrainOptions,TerrainCoastPlacement> build,CancellationToken cancellation)
    {
        if(area.X!=0||area.Y!=0||area.Width!=document.Width||area.Height!=document.Height)
            throw new ArgumentException("A beach needs the whole map and its actual boundaries.");
        var key=new NativeWaterPlanCache.Key(options.Seed,area.Width,area.Height,combined,options.BridgeCrossing,Compact:options.CompactBeach);
        var empty=new MapDocument(area.Width,area.Height);
        var result=NativeWaterPlanCache.Get(shore,key,()=>build(empty,options),cancellation);
        if(result.Placement==null)
        {
            // The reserve is built in a fixed order, never from the most recently
            // successful user seed. Preview history cannot change a seed's result.
            result=NativeWaterPlanCache.Get(shore,key with{Seed=0,Fallback=true},()=>
            {
                string failure=result.Failure;
                foreach(int seed in Enumerable.Range(0,16))
                {
                    cancellation.ThrowIfCancellationRequested();
                    try{return build(empty,options with{Seed=seed});}
                    catch(ArgumentException error){failure=error.Message;}
                }
                throw new ArgumentException(failure);
            },cancellation);
        }
        var placement=result.Placement??throw new ArgumentException(result.Failure);
        if(!CanPlace(document,placement.Layout,0,0))throw new ArgumentException("Existing objects or collisions leave no clear space for the coast banks.");
        return placement;
    }

    private static TerrainCoastPlacement BuildRiverAndBeach(MapDocument document,TerrainArea area,TerrainOptions options,
        RiverCoastPieces riverBanks,RiverShoreComponents shore,CancellationToken cancellation)
    {
        bool preferMouth=!options.CompactBeach&&!options.BridgeCrossing&&new Random(options.Seed^0x71AC39).Next(2)==0;
        string failure="The complete coast and river need more clear space.";
        int seed=options.Seed;
        cancellation.ThrowIfCancellationRequested();
        var settings=options with{WaterMode=TerrainWaterMode.Beach,BridgeCrossing=false};
        TerrainCoastPlacement? separated=null,joined=null;
        if(preferMouth)joined=TryMouth(shore,area,seed,out failure,cancellation);
        if(joined!=null&&CanPlace(document,joined.Layout,0,0))return joined;
        try
        {
            var beach=BuildShorePieces(document,area,settings,shore,cancellation);
            separated=TrySeparate(document,area,options,riverBanks,beach,cancellation);
        }
        catch(ArgumentException error){failure=error.Message;}
        if(separated!=null)return separated;
        if(!preferMouth&&!options.BridgeCrossing)joined=TryMouth(shore,area,seed,out failure,cancellation);
        if(joined!=null&&CanPlace(document,joined.Layout,0,0))return joined;
        throw new ArgumentException(failure);
    }

    private static TerrainCoastPlacement? TryMouth(RiverShoreComponents shore,TerrainArea area,int seed,
        out string failure,CancellationToken cancellation)
    {
        failure="No complete native river mouth fits this coastline.";
        if(shore.HorizontalEdge is not {} edge)return null;
        int width=area.Width,height=area.Height;
        var coast=RiverHorizontalCoastBuilder.Research(shore.Banks,edge,RiverPieceRiverBuilder.CoastBends,
            seed,width,height,out failure,cancellation,minimumLandHeight:Math.Min(240,height/3),minimumStraightPieces:4);
        if(coast==null)return null;
        var random=new Random(seed^0x35618);var original=coast.Chain.Pieces;
        var sites=Enumerable.Range(1,Math.Max(0,original.Count-4)).Where(i=>original.Skip(i).Take(4)
            .All(p=>p.Piece.Id=="north-straight"&&!p.Reverse)).OrderBy(_=>random.Next()).ToArray();
        var courses=RiverLimbBends.Courses(shore.Banks,seed,width,height,0,cancellation);
        var pieces=shore.Banks.Pieces.ToDictionary(p=>p.Id);
        var links=shore.Banks.Links.ToDictionary(l=>(l.FromId,l.FromReverse,l.ToId,l.ToReverse));
        int trials=0;
        foreach(var course in courses)
        foreach(int site in sites)
        {
            cancellation.ThrowIfCancellationRequested();
            var steps=original.Select(p=>(Id:p.Piece.Id,p.Reverse)).ToList();
            steps.RemoveRange(site,4);steps.InsertRange(site,course.Steps);
            var placed=new List<RiverCoastPieces.Placement>{original[0]};bool valid=true;
            foreach(var step in steps.Skip(1))
            {
                var previous=placed[^1];
                if(!links.TryGetValue((previous.Piece.Id,previous.Reverse,step.Id,step.Reverse),out var link)) {valid=false;break;}
                placed.Add(new(pieces[step.Id],previous.X+link.Dx,previous.Y+link.Dy,step.Reverse));
            }
            if(!valid||placed[^1].X!=original[^1].X||placed[^1].Y!=original[^1].Y)continue;
            var chain=shore.Banks.ResearchAssemble(placed,out failure);
            if(chain==null||chain.Closed||chain.Cells.Any(c=>c.X<0||c.Y<12||c.X>=width||c.Y>=height))continue;
            if(++trials>3)return null;
            var first=placed[0];var last=placed[^1];
            (double X,double Y)[] boundary=[(width,last.Y+edge.Piece.End.ShoreY),(width,height),(0,height),(0,first.Y+edge.Piece.Start.ShoreY)];
            try
            {
                var ground=RiverPieceGroundAssembler.AssembleCoast(shore.Banks,chain,seed,new(0,0,width,height),boundary,
                    cancellation,verifyChannel:true,sealNativePockets:true);
                var layout=CoastLayout(ground,"procedural-native-estuary","River opening into the sea",TerrainWaterMode.RiverToSea,["west","east"]);
                return new(layout,0,0){DiagonalAccess=true};
            }
            catch(InvalidDataException error){failure=error.Message;}
        }
        return null;
    }

    private static TerrainCoastPlacement? TrySeparate(MapDocument document,TerrainArea area,TerrainOptions options,
        RiverCoastPieces riverBanks,TerrainCoastPlacement beach,CancellationToken cancellation)
    {
        int width=area.Width,height=area.Height;
        // Reserve a dry strip around all saved coastal ground and artwork. The
        // largest remaining rectangle can host a complete river without touching it.
        // Accumulate rectangles once instead of repainting the same 26×26
        // neighbourhood for every saved water cell (hundreds of millions of writes).
        int stride=width+1;var coverage=new int[stride*(height+1)];
        foreach(var cell in beach.Layout.Cells)
        {
            int l=Math.Max(0,cell.X-12),r=Math.Min(width,cell.X+14);
            int t=Math.Max(0,cell.Y-12),b=Math.Min(height,cell.Y+14);
            coverage[t*stride+l]++;coverage[t*stride+r]--;
            coverage[b*stride+l]--;coverage[b*stride+r]++;
        }
        var rectangles=new List<TerrainArea>();var heights=new int[width];
        for(int y=0;y<height;y++)
        {
            cancellation.ThrowIfCancellationRequested();
            int count=0;
            for(int x=0;x<width;x++)
            {
                int key=y*stride+x;count+=coverage[key];
                coverage[key]=count+(y>0?coverage[key-stride]:0);
                heights[x]=coverage[key]>0?0:heights[x]+1;
            }
            var stack=new Stack<int>();
            for(int x=0;x<=width;x++)
            {
                int current=x==width?0:heights[x];
                while(stack.Count>0&&heights[stack.Peek()]>current)
                {
                    int h=heights[stack.Pop()],left=stack.Count==0?0:stack.Peek()+1,w=x-left;
                    if(w>=180&&h>=120)rectangles.Add(new(left,y-h+1,w,h));
                }
                stack.Push(x);
            }
        }
        foreach(var space in rectangles.OrderByDescending(r=>(long)r.Width*r.Height).Distinct().Take(3))
        {
            try
            {
                var river=PlanPieces(document,space,options with{WaterMode=TerrainWaterMode.River},riverBanks,cancellation);
                var cells=beach.Layout.Cells.Concat(river.Layout.Cells.Select(c=>new StampCell(c.X+river.X,c.Y+river.Y,c.Cell))).ToArray();
                if(cells.Select(c=>(c.X,c.Y)).Distinct().Count()!=cells.Length)continue;
                var layout=beach.Layout with{Id="procedural-native-river-and-coast",Name="Inland river and separate beach",Cells=cells,
                    WaterMode=TerrainWaterMode.RiverToSea,BranchCount=river.Layout.BranchCount,HasNativeBridge=river.Layout.HasNativeBridge};
                var combined=new TerrainCoastPlacement(layout,0,0){DiagonalAccess=true};
                VerifyAccess(document,combined,cancellation);return combined;
            }
            catch(ArgumentException){ }
        }
        return null;
    }

    private static RiverCoastLayout CoastLayout(RiverPieceGroundAssembler.Result ground,string id,string name,
        TerrainWaterMode mode,IReadOnlyList<string> edges)
    {
        var cells=new List<StampCell>();
        foreach(var saved in ground.Cells)
        {
            var cell=saved.Cell.Copy();
            if(!ground.RequiredGroundAnchors.Contains((saved.X,saved.Y)))cell.BackImage&=0x60000000;
            if(cell.BackImage==0&&cell.MiddleImage==0&&cell.ForegroundImages.Count==0&&cell.Door==0)continue;
            cells.Add(new(saved.X,saved.Y,cell));
        }
        return new(id,name,cells){Width=ground.Width,Height=ground.Height,WaterMode=mode,BoundaryEdges=edges};
    }
}
