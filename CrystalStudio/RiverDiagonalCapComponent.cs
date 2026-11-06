using System.IO;
using System.Security.Cryptography;

namespace CrystalStudio;

/// <summary>A small saved cap, copied from seven unchanged native bank pieces.</summary>
public sealed class RiverDiagonalCapComponent
{
    public RiverCoastPieces.Piece Piece {get;}
    private RiverDiagonalCapComponent(IReadOnlyList<StampCell> cells)
    {
        Piece=new("diagonal-gentle-cap",cells,new("mir/mir_01384",16,5,0,13.5,0),
            new("mir/mir_01391",3,34,0,2.75,30));
    }
    public static RiverDiagonalCapComponent Load(RiverCoastPieces catalogue,string path)
    {
        if(Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)))!="8d9be540de1e7b9605400947b3084561d9bf4bfc7dd8b118719b5e1d15754f9c")
            throw new InvalidDataException("Diagonal cap differs from its verified native bank artwork.");
        var byId=catalogue.Pieces.ToDictionary(p=>p.Id);
        (string Id,int X,int Y,bool Reverse)[] parts=[("native-1384-1385-4-7",16,5,false),("east-narrow-entry",20,12,false),
            ("native-1385-1393-1-m9",20,31,true),("native-1385-1386-m1-9",20,31,false),("south-cap-exit",19,40,false),
            ("south-west-turn-entry",13,40,false),("west-diagonal-entry",8,41,false)];
        var original=catalogue.ResearchAssemble(parts.Select(p=>new RiverCoastPieces.Placement(byId[p.Id],p.X,p.Y,p.Reverse)).ToArray(),out string failure)
            ??throw new InvalidDataException(failure);
        var cells=CellCodec.LoadStamp(path,catalogue.Format);var stored=cells.ToDictionary(c=>(c.X,c.Y),c=>c.Cell);
        if(cells.Count!=original.Cells.Count||cells.Any(c=>c.X<0||c.X>=24||c.Y<0||c.Y>=43)
            ||original.Cells.Any(c=>!stored.TryGetValue((c.X,c.Y),out var saved)||!Same(saved,c.Cell)))
            throw new InvalidDataException("Saved diagonal cap must preserve every native cell and collision flag.");
        return new(cells);
    }
    private static bool Same(Cell a,Cell b)=>a.BackLibrary==b.BackLibrary&&a.BackImage==b.BackImage
        &&a.MiddleLibrary==b.MiddleLibrary&&a.MiddleImage==b.MiddleImage&&(a.Foreground == b.Foreground)
        &&a.Door==b.Door&&a.DoorOffset==b.DoorOffset&&a.MiddleFrames==b.MiddleFrames&&a.MiddleTick==b.MiddleTick
        &&a.TileAnimation==b.TileAnimation&&a.TileOffset==b.TileOffset&&a.TileFrames==b.TileFrames&&a.Light==b.Light;
}
