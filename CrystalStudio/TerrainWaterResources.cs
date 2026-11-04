using System.IO;
using System.Runtime.CompilerServices;

namespace CrystalStudio;

/// <summary>Reuse verified native pieces for the lifetime of the loaded game libraries.</summary>
internal sealed record TerrainWaterResources(RiverCoastPieces? Banks,RiverCoastPieces? Bridges,RiverShoreComponents? Shore,string? Error,IslandCoastObjects? Island,string? IslandError)
{
    private static readonly ConditionalWeakTable<AssetStore,Task<TerrainWaterResources>> Loaded=new();
    internal static Task<TerrainWaterResources> Load(AssetStore assets,CancellationToken cancellation)
        =>Loaded.GetValue(assets,a=>Task.Run(()=>Read(a))).WaitAsync(cancellation);
    private static TerrainWaterResources Read(AssetStore assets)
    {
        RiverCoastPieces? banks=null,bridges=null;RiverShoreComponents? shore=null;string? error=null;
        try{banks=TerrainCoastObjects.LoadWindingPieces(assets,bankOnly:true);}catch(IOException ex){error=ex.Message;}
        try{bridges=TerrainCoastObjects.LoadBridgePieces(assets);}catch(IOException){}
        try{shore=RiverShoreComponents.Load(assets);}catch(IOException){}
        IslandCoastObjects? island=null;string? islandError=null;
        try{island=IslandCoastObjects.Load(assets);}catch(IOException ex){islandError=ex.Message;}
        return new(banks,bridges,shore,error,island,islandError);
    }
}
