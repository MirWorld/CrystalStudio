using System.IO;
using System.Security.Cryptography;

namespace CrystalStudio;

/// <summary>Two unchanged north-bank objects at a horizontal map edge.</summary>
public sealed class RiverHorizontalBoundaryComponent
{
    public RiverCoastPieces.Piece Piece { get; }
    public int Width => 24;
    private RiverHorizontalBoundaryComponent(IReadOnlyList<StampCell> cells)
    {
        Piece=new("north-bank-horizontal-edge",cells,
            new("mir/mir_01377",5,0,1,0,1.125),
            new("mir/mir_01377",17,0,0,23.916666666666664,.75));
    }
    public static RiverHorizontalBoundaryComponent Load(RiverCoastPieces catalogue,string path)
    {
        if(Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)))!="5ee75341075aef01e1a46d0aaeb59e36e99dd7b1011b230b7a31140dfd0b9529")
            throw new InvalidDataException("Horizontal edge component differs from its original bank artwork.");
        var cells=CellCodec.LoadStamp(path,catalogue.Format);
        var original=catalogue.Pieces.Single(p=>p.Id=="north-straight").Cells;
        var stored=cells.ToDictionary(c=>(c.X,c.Y),c=>c.Cell);
        if(cells.Count!=original.Count||original.Any(c=>!stored.TryGetValue((c.X+5,c.Y),out var saved)
            ||saved.MiddleLibrary!=c.Cell.MiddleLibrary||saved.MiddleImage!=c.Cell.MiddleImage
            ||saved.BackLibrary!=c.Cell.BackLibrary||saved.BackImage!=c.Cell.BackImage||(saved.Foreground != c.Cell.Foreground))
            ||cells.Any(c=>c.X<0||c.X>=24||c.Y<0||c.Y>=5||c.Cell.ForegroundImages.Count!=0))
            throw new InvalidDataException("Horizontal edge must retain both original banks and their collision flags.");
        return new(cells);
    }
}
