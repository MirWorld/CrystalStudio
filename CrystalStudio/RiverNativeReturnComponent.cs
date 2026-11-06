using System.IO;
using System.Security.Cryptography;

namespace CrystalStudio;

/// <summary>One unchanged two-bank return join witnessed in eight real-map locations.</summary>
public sealed class RiverNativeReturnComponent
{
    public RiverCoastPieces.Piece Piece {get;}
    private RiverNativeReturnComponent(RiverCoastPieces.Piece piece)=>Piece=piece;
    public static RiverNativeReturnComponent Load(RiverCoastPieces catalogue,string path)
    {
        if(Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)))!="abf104118c60dae6b02441007751192b5d7d9b78e24580d7c5a63c9eff5b921c")
            throw new InvalidDataException("Native diagonal return piece changed.");
        var art=RiverArtworkCatalogue.Load(DataPaths.ResolveEditor("catalogue","river-coast-layouts","winding","river-artwork.json"),true);
        var expected=new Dictionary<(int,int),(int,int)>();
        foreach(var part in new[]{(Id:"mir/mir_01384",X:0,Y:0),(Id:"mir/mir_01393",X:-2,Y:-9)})
        foreach(var command in art.Pieces.Single(p=>p.Id==part.Id).Commands)
        {
            var key=(part.X+command.X,part.Y+command.Y);var value=(command.Library,command.Image);
            if(expected.TryGetValue(key,out var old)&&old!=value)throw new InvalidDataException("Native return banks conflict.");
            expected[key]=value;
        }
        var cells=CellCodec.LoadStamp(path,catalogue.Format);
        if(cells.Count!=25||cells.Count!=expected.Count||cells.Any(c=>!expected.TryGetValue((c.X,c.Y),out var p)
            ||(c.Cell.MiddleLibrary,c.Cell.MiddleImage)!=p||(c.Cell.BackImage&0x1fffffff)!=0||c.Cell.ForegroundImages.Count!=0))
            throw new InvalidDataException("Native return must preserve both complete bank fragments.");
        RiverCoastPieces.Port Port(string id,int x,int y,int endpoint)
        {
            var source=catalogue.Pieces.SelectMany(p=>new[]{p.Start,p.End}).First(p=>p.BankId==id&&p.FreeEndpoint==endpoint);
            return new(id,x,y,endpoint,x+source.ShoreX-source.BankX,y+source.ShoreY-source.BankY);
        }
        return new(new("native-1384-1393-m2-m9",cells,Port("mir/mir_01384",0,0,1),Port("mir/mir_01393",-2,-9,0)));
    }
}
