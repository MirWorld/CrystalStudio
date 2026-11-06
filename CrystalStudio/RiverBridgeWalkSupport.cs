using System.IO;
using System.Security.Cryptography;
using System.Text.Json;

namespace CrystalStudio;

/// <summary>Exact original walking cells for the saved native bridge component.</summary>
public sealed class RiverBridgeWalkSupport
{
    private readonly IReadOnlyList<StampCell> source;
    private readonly (int X,int Y)[] walk;
    private readonly IReadOnlySet<(int X,int Y,int Library,int Image)> deck;
    private readonly int x,y;
    private RiverBridgeWalkSupport(IReadOnlyList<StampCell> source,(int,int)[] walk,int x,int y,
        IReadOnlySet<(int,int,int,int)> deck)
    {this.source=source;this.walk=walk;this.x=x;this.y=y;this.deck=deck;}

    public static RiverBridgeWalkSupport Load(string modulePath,AssetSourceFormat format=AssetSourceFormat.Default,int x=0,int y=0)
    {
        Require(Hash(modulePath)=="79e7810c61e05ca9186ab9a3506e1125683f3181b4d9ceb8a598ce1180d3aee9","Native bridge source component changed.");
        string proofPath=Path.ChangeExtension(modulePath,"json");
        // The portable proof changes only source paths. Walking cells and all
        // artwork evidence are identical to the original pinned proof.
        Require(Hash(proofPath) is "1e96f108660c3aebfac1e21496c3daa1c10fed02d2f897aa27735ff9eab62a1e"
            or "3e257c64eaf2e10692b02dc8f5d4d15941918c28321a2a6066b13f2dc424f8f8","Native bridge walking proof changed.");
        using var json=JsonDocument.Parse(File.ReadAllText(proofPath));
        var cells=CellCodec.LoadStamp(modulePath,format);var byPoint=cells.ToDictionary(c=>(c.X,c.Y),c=>c.Cell);
        var route=json.RootElement.GetProperty("pixelVerifiedCrossing").EnumerateArray().Select(p=>(p.GetProperty("x").GetInt32(),p.GetProperty("y").GetInt32())).ToArray();
        Require(route.Length==29&&route.Distinct().Count()==29,"Invalid original bridge route.");
        foreach(var point in route)
        {var cell=byPoint.GetValueOrDefault(point,new Cell());Require((cell.BackImage&0x60000000)==0&&!cell.ForegroundImages.Any(f=>(f.Image&0x8000)!=0),"Original bridge route is blocked.");}
        string crossingPath=Path.Combine(Path.GetDirectoryName(modulePath)!,"province-bridge-crossing.json");
        Require(Hash(crossingPath) is "7506e709d10a2b70207ae55ebe4b2173b6ad7e9979835110d60e635fad834097"
            or "91f37558a9b1870bb866f646836eb4a843438e968d420e1d27bd6b5f47e1ebd3","Original bridge fragment proof changed.");
        using var crossing=JsonDocument.Parse(File.ReadAllText(crossingPath));
        var offset=json.RootElement.GetProperty("sourceCrossing");int dx=offset.GetProperty("x").GetInt32(),dy=offset.GetProperty("y").GetInt32();
        var deck=crossing.RootElement.GetProperty("bridgeCommands").EnumerateArray().Select(p=>(
            p.GetProperty("x").GetInt32()+dx,p.GetProperty("y").GetInt32()+dy,
            AssetLibraryIds.Resolve(p.GetProperty("library").GetInt32(),MapLayer.Objects,format),p.GetProperty("image").GetInt32())).ToHashSet();
        Require(deck.Count==122&&deck.All(p=>byPoint.TryGetValue((p.Item1,p.Item2),out var cell)
            &&cell.ForegroundImages.Any(f=>f.Library==p.Item3&&(f.Image&0x7fff)==p.Item4)),"Bridge flow evidence must name exactly the original 122 Front fragments.");
        return new(cells,route,x,y,deck);
    }
    internal RiverBridgeWalkSupport ForValidatedComponent(IReadOnlyList<StampCell> cells)=>new(cells,walk,x,y,deck);
    internal RiverBridgeWalkSupport Translate(int dx,int dy)=>new(source,walk,x+dx,y+dy,deck);
    internal bool IsDeck(int px,int py,int library,int image)=>deck.Contains((px-x,py-y,library,image));
    internal void Validate(IReadOnlyDictionary<(int,int),Cell> cells)
    {
        foreach(var saved in source)
        {
            var expected=saved.Cell;if(expected.MiddleImage==0&&expected.ForegroundImages.Count==0)continue;
            Require(cells.TryGetValue((x+saved.X,y+saved.Y),out var actual),"Bridge support has no matching native component.");
            Require(expected.MiddleImage==0||actual!.MiddleLibrary==expected.MiddleLibrary&&actual.MiddleImage==expected.MiddleImage,"Bridge support changes its native banks.");
            Require(expected.ForegroundImages.Count==0||actual!.ForegroundImages.SequenceEqual(expected.ForegroundImages),"Bridge support changes its native crossing artwork.");
        }
        foreach(var point in walk)Require(!cells.GetValueOrDefault((x+point.X,y+point.Y),new Cell()).ForegroundImages.Any(f=>(f.Image&0x8000)!=0),"Assembled objects block the native bridge route.");
    }
    internal void Apply(Dictionary<(int,int),Cell> cells)
    {
        foreach(var point in walk)
        {var position=(x+point.X,y+point.Y);if(!cells.TryGetValue(position,out var cell))cells[position]=cell=new();cell.BackImage&=~0x60000000;}
    }
    private static string Hash(string path)=>Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));
    private static void Require(bool value,string message){if(!value)throw new InvalidDataException(message);}
}
