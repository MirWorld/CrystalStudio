namespace CrystalStudio;

internal static class RiverBranchAssembly
{
    internal static RiverPieceRiverBuilder.WindingResult? Build(RiverCoastPieces catalogue,RiverCoastPieces.Chain source,
        int seed,int branches,string shape,out string failure,CancellationToken cancellation,
        Action<RiverPieceGroundAssembler.Result,RiverShapeQuality.Report>? inspect=null)
    {
        failure="The completed tributary does not form a gradual connected river network.";
        cancellation.ThrowIfCancellationRequested();
        var chain=source;
        try
        {
            var ground=RiverPieceGroundAssembler.Assemble(catalogue,chain,seed,cancellation,verifyCovers:true,verifyChannel:true,sealNativePockets:true);
            // Every complete native limb reaches more than 48 physical cells
            // from its mouth. Smaller skeleton spurs within the bank sprites
            // describe shoreline bays, not additional branches.
            var quality=RiverShapeQuality.Analyse(ground,cancellation,minimumBranchLength:32);inspect?.Invoke(ground,quality);
            if(quality.Ends!=branches+2||quality.Junctions!=branches||quality.Elbows.Count!=0)
            {failure=$"Tributary has {quality.Ends} ends, {quality.Junctions} junctions and {quality.Elbows.Count} abrupt turns.";return null;}
            failure="";return new(chain,shape){PreparedGround=ground};
        }
        catch(System.IO.InvalidDataException error){failure=error.Message;}
        return null;
    }
}
