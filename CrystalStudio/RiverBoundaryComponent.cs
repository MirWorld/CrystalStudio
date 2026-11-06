using System.IO;
using System.Security.Cryptography;

namespace CrystalStudio;

/// <summary>One verified pair of original banks at a map edge, never a saved coast course.</summary>
public sealed class RiverBoundaryComponent
{
    public RiverCoastPieces.Piece Piece {get;}
    public int Width {get;}
    public int Height {get;}
    private RiverBoundaryComponent(IReadOnlyList<StampCell> cells,bool odd)
    {
        Width=odd?24:18;Height=odd?27:18;
        Piece=new(odd?"south-diagonal-bank-edge-odd":"south-diagonal-bank-edge",cells,new("mir/mir_01391",3,4,0,2.75,0),
            odd?new("mir/mir_01391",15,22,1,20.5,26.875):new("mir/mir_01391",9,13,1,14.5,17.875));
    }
    public static RiverBoundaryComponent Load(RiverCoastPieces catalogue,string path)
    {
        string digest=Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));
        bool odd=digest=="77c96770534ddb3db84cb0b451c1fbbce9d36c2c51506ab42760d9e93a11b9c5";
        if(!odd&&digest!="8590c52571198b3620e48fa6e6e6612bac80bbc471d69dea4cd46a131994dabf")
            throw new InvalidDataException("Native map-edge component differs from its verified artwork and game collision proof.");
        var cells=CellCodec.LoadStamp(path,catalogue.Format);var original=catalogue.Pieces.Single(p=>p.Id=="west-diagonal").Cells.Where(c=>c.Cell.MiddleImage!=0).ToArray();
        var native=new Dictionary<(int,int),Cell>();
        foreach(var offset in odd?new[]{(3,4),(9,13)}:new[]{(3,4)})
            foreach(var c in original)native[(c.X+offset.Item1,c.Y+offset.Item2)]=c.Cell;
        var stored=cells.Where(c=>c.Cell.MiddleImage!=0).ToDictionary(c=>(c.X,c.Y),c=>c.Cell);
        if(native.Count!=(odd?60:40)||stored.Count!=native.Count||native.Any(p=>!stored.TryGetValue(p.Key,out var c)
            ||c.MiddleLibrary!=p.Value.MiddleLibrary||c.MiddleImage!=p.Value.MiddleImage)
            ||cells.Any(c=>c.X<0||c.Y<0||c.X>=(odd?24:18)||c.Y>=(odd?27:18)||c.Cell.ForegroundImages.Count!=0))
            throw new InvalidDataException("Map-edge piece must retain its complete original banks and no additional scenery.");
        var result=new RiverBoundaryComponent(cells,odd);
        if(result.Piece.GroundPhaseMask!=1)throw new InvalidDataException("Native map-edge ground phase changed.");
        return result;
    }
}
