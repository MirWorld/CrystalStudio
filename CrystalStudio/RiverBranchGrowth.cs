namespace CrystalStudio;

/// <summary>Seeded branching generations, retaining only complete native channels.</summary>
internal static class RiverBranchGrowth
{
    internal static RiverPieceRiverBuilder.WindingResult GrowDescendants(RiverCoastPieces catalogue,
        RiverPieceRiverBuilder.WindingResult source,int seed,int width,int height,CancellationToken cancellation)
    {
        var random=new Random(seed^0x134B527);
        var pending=new Queue<(int Id,int Depth)>(Parents(source));
        var result=source;
        while(pending.TryDequeue(out var parent)&&result.BranchCount<10)
        {
            cancellation.ThrowIfCancellationRequested();
            // A first-generation limb has a 60% opportunity; its children
            // have 30%, then 15%, then 7.5%. Space and valid joins still apply.
            double chance=.60*Math.Pow(.5,parent.Depth-1);
            if(parent.Depth>=5||random.NextDouble()>=chance)continue;
            var next=Attach(catalogue,result.Chain,unchecked(seed+parent.Id*130363),width,height,out _,cancellation,parent.Depth,parent.Id);
            if(next==null)continue;
            int newest=next.Chain.Pieces.Max(p=>p.BranchId);
            result=next with{Shape=result.Shape+"-"+next.Shape};
            pending.Enqueue((newest,parent.Depth+1));
        }
        return result;
    }
    internal static RiverPieceRiverBuilder.WindingResult? Attach(RiverCoastPieces catalogue,RiverCoastPieces.Chain chain,
        int seed,int width,int height,out string failure,CancellationToken cancellation,int depth=0,int branchId=0)
    {
        int northCount=chain.Pieces.Count(p=>p.Piece.Id=="diagonal-fork-concave");
        int southCount=chain.Pieces.Count(p=>p.Piece.Id=="tributary-south-join");
        bool bottom=northCount==southCount?new Random(RiverSeed.Derive(seed,22)).Next(2)==0:southCount<northCount;
        var first=bottom?RiverBottomBranchBuilder.Build(catalogue,chain,seed,width,height,out failure,cancellation,parentDepth:depth,parentBranchId:branchId)
            :RiverPieceRiverBuilder.ResearchAttachDiagonalFork(catalogue,chain,seed,width,height,out failure,cancellation,depth,branchId);
        if(first!=null)return first;
        // If the open side cannot fit, avoid filling the other side with an
        // ever larger row of parallel limbs just to reach the requested count.
        if(bottom?northCount>=southCount+1:southCount>=northCount+1)return null;
        return bottom?RiverPieceRiverBuilder.ResearchAttachDiagonalFork(catalogue,chain,seed,width,height,out failure,cancellation,depth,branchId)
            :RiverBottomBranchBuilder.Build(catalogue,chain,seed,width,height,out failure,cancellation,parentDepth:depth,parentBranchId:branchId);
    }
    private static IEnumerable<(int Id,int Depth)> Parents(RiverPieceRiverBuilder.WindingResult source)=>source.Chain.Pieces
        .Where(p=>p.BranchDepth>0).GroupBy(p=>p.BranchId).Select(g=>(g.Key,g.First().BranchDepth));
}
