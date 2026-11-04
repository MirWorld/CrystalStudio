using System.Runtime.CompilerServices;

namespace CrystalStudio;

/// <summary>Reuse verified river geometry while the preview's other settings change.</summary>
internal static class RiverWindingPlanCache
{
    private readonly record struct Key(int Seed,int Width,int Height,bool Bridge,bool Branches);
    private sealed class RecentPlans
    {
        public readonly object Gate=new();
        public readonly LinkedList<(Key Key,RiverPieceRiverBuilder.WindingResult Plan)> Entries=[];
    }
    // A closed generator window can release its catalogue and its recent plans.
    private static readonly ConditionalWeakTable<RiverCoastPieces,RecentPlans> Catalogues=new();
    internal static RiverPieceRiverBuilder.WindingResult? Build(RiverCoastPieces catalogue,int seed,int width,int height,
        out string failure,CancellationToken cancellation=default,bool bridge=false,bool branches=false)
    {
        cancellation.ThrowIfCancellationRequested();var key=new Key(seed,width,height,bridge,branches);
        if(bridge&&branches)
        {failure="Gradual native bridge tributaries are still being reviewed.";return null;}
        var recent=Catalogues.GetValue(catalogue,_=>new());
        lock(recent.Gate)
        {
            for(var node=recent.Entries.First;node!=null;node=node.Next)
                if(node.Value.Key==key)
                {
                    var plan=node.Value.Plan;recent.Entries.Remove(node);recent.Entries.AddFirst((key,plan));
                    failure="";return plan;
                }
        }
        // Ordinary rivers can have tributaries too. The waterways option always
        // requests them; a winding river falls back to an unbranched course if
        // there is not enough room for a complete native junction.
        int limbs=bridge||branches?0:RiverPieceRiverBuilder.DesiredBranchCount(seed,width,height,false);
        var (mainWidth,mainHeight)=limbs>0?RiverSeed.MainArea(seed,width,height):(width,height);
        var built=branches?RiverPieceRiverBuilder.ResearchBranching(catalogue,seed,width,height,out failure,cancellation):
            bridge?RiverPieceRiverBuilder.ResearchGentleBridgeSelection(catalogue,seed,width,height,out failure,cancellation):
            RiverPieceRiverBuilder.ResearchWinding(catalogue,seed,mainWidth,mainHeight,out failure,cancellation)
                ??RiverPieceRiverBuilder.ResearchWinding(catalogue,seed,width,height,out failure,cancellation);
        if(built==null&&!bridge)
        {
            // A seed may request proportions that do not fit. Retain a complete
            // gentle channel instead of failing because of the random choice.
            foreach(int reserve in Enumerable.Range(0,8))
            {
                cancellation.ThrowIfCancellationRequested();
                built=RiverPieceRiverBuilder.ResearchWinding(catalogue,RiverSeed.Derive(seed,100+reserve),width,height,out failure,cancellation);
                if(built!=null)break;
            }
        }
        if(built!=null&&!bridge&&!branches)
        {
            for(int limb=0;limb<limbs;limb++)
            {
                var next=RiverBranchGrowth.Attach(catalogue,built.Chain,unchecked(seed+limb*104729),
                    width,height,out _,cancellation);
                if(next==null)break;
                built=next with{Shape=built.Shape+"-"+next.Shape};
            }
        }
        if(built==null)return null;cancellation.ThrowIfCancellationRequested();
        if(!bridge&&built.BranchCount>0)built=RiverBranchGrowth.GrowDescendants(catalogue,built,seed,width,height,cancellation);
        // A research catalogue without the production cap can still produce an
        // older outline. It must pass the same check before preview reuse.
        if(!catalogue.Pieces.Any(p=>p.Id=="diagonal-gentle-cap"))
        {
            var ground=built.PreparedGround??RiverPieceGroundAssembler.Assemble(catalogue,built.Chain,seed,cancellation);
            if(!RiverShapeQuality.Analyse(ground,cancellation).SimpleGentleChannel)
            {failure="No reviewed river layout without large square bends is available for this catalogue.";return null;}
            built=built with{PreparedGround=ground};
        }
        lock(recent.Gate)
        {
            // Concurrent preview requests may finish the same seed. Keep one.
            for(var node=recent.Entries.First;node!=null;node=node.Next)
                if(node.Value.Key==key){recent.Entries.Remove(node);break;}
            recent.Entries.AddFirst((key,built));
            while(recent.Entries.Count>4)recent.Entries.RemoveLast();
        }
        return built;
    }
}
