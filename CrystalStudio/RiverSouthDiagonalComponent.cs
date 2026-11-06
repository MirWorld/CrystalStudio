using System.IO;
using System.Security.Cryptography;

namespace CrystalStudio;

/// <summary>A small saved horizontal-to-diagonal join made from three complete native banks.</summary>
public sealed class RiverSouthDiagonalComponent
{
    public RiverCoastPieces.Piece Piece {get;}
    private RiverSouthDiagonalComponent(RiverCoastPieces.Piece piece)=>Piece=piece;
    public static RiverSouthDiagonalComponent Load(RiverCoastPieces catalogue,string path)
    {
        if(Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)))!="fc762aa46e67c770dd957a5ed0554e5b4cff25d4b46c1de9611248ec60cf21fd")
            throw new InvalidDataException("Saved south diagonal join changed.");
        var art=RiverArtworkCatalogue.Load(DataPaths.ResolveEditor("catalogue","river-coast-layouts","winding","river-artwork.json"),true);
        var expected=new Dictionary<(int,int),(int,int)>();
        foreach(var part in new[]{(Id:"mir/mir_01389",X:0,Y:0),(Id:"mir/mir_01389",X:-8,Y:0),(Id:"mir/mir_01391",X:-15,Y:-3)})
        foreach(var command in art.Pieces.Single(p=>p.Id==part.Id).Commands)
        {
            var key=(part.X+command.X,part.Y+command.Y);var value=(command.Library,command.Image);
            if(expected.TryGetValue(key,out var old)&&old!=value)throw new InvalidDataException("South diagonal source banks conflict.");
            expected[key]=value;
        }
        var cells=CellCodec.LoadStamp(path,catalogue.Format);
        var banks=cells.Where(c=>c.Cell.MiddleImage!=0).ToDictionary(c=>(c.X,c.Y),c=>(c.Cell.MiddleLibrary,c.Cell.MiddleImage));
        if(cells.Count!=121||banks.Count!=expected.Count||expected.Any(p=>!banks.TryGetValue(p.Key,out var value)||value!=p.Value)
            ||cells.Any(c=>c.Cell.BackLibrary!=0||(c.Cell.BackImage&~0x60000000)!=0||c.Cell.ForegroundImages.Count!=0
                ||c.Cell.Door!=0||c.Cell.DoorOffset!=0||c.Cell.MiddleFrames!=0||c.Cell.MiddleTick!=0
                ||c.Cell.TileAnimation!=0||c.Cell.TileOffset!=0||c.Cell.TileFrames!=0||c.Cell.Light!=0))
            throw new InvalidDataException("South diagonal join must retain complete source banks and static native collision flags.");
        RiverCoastPieces.Port Port(string id,int x,int y,int endpoint)
        {
            var source=catalogue.Pieces.SelectMany(p=>new[]{p.Start,p.End}).First(p=>p.BankId==id&&p.FreeEndpoint==endpoint);
            return new(id,x,y,endpoint,x+source.ShoreX-source.BankX,y+source.ShoreY-source.BankY);
        }
        return new(new("native-south-diagonal-return",cells,Port("mir/mir_01389",0,0,1),Port("mir/mir_01391",-15,-3,0)));
    }
}
