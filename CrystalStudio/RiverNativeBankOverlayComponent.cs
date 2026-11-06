using System.IO;
using System.Security.Cryptography;

namespace CrystalStudio;

/// <summary>A small saved native bank join with bank fragments in ordinary Front slots.</summary>
public sealed class RiverNativeBankOverlayComponent
{
    public RiverCoastPieces.Piece Piece {get;}
    private RiverNativeBankOverlayComponent(RiverCoastPieces.Piece piece)=>Piece=piece;

    public static RiverNativeBankOverlayComponent Load(RiverCoastPieces catalogue,string path)
    {
        // This hash binds the complete source collision union as well as artwork.
        // Source-fragment checks below also forbid scenery being labelled as bank.
        if(Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)))!="76a0b5158b8d6dc14153182d66f1358bf56df5f374dd56f563f7e1d7212e022d")
            throw new InvalidDataException("Native bank overlay differs from its verified source assembly.");
        var art=RiverArtworkCatalogue.Load(DataPaths.ResolveEditor("catalogue","river-artwork.json"),true);
        var bank=art.Pieces.Single(p=>p.Id=="mir/mir_01391");
        if(bank.Role!="bank"||bank.SourceSha256!="3bd5f1b45430503744be9f90ceb475e31048b27745962cbd48c0695fbb5e00f8"||bank.Commands.Count!=20)
            throw new InvalidDataException("Native overlay source bank changed.");
        var cells=CellCodec.LoadStamp(path,catalogue.Format);
        var middle=new[]{(X:3,Y:4),(X:11,Y:13)}.SelectMany(at=>bank.Commands
            .Select(c=>(X:at.X+c.X,Y:at.Y+c.Y,c.Library,c.Image))).ToHashSet();
        var front=bank.Commands.Select(c=>new RiverArtworkCommand(c.X+9,c.Y+12,c.Library,c.Image)).ToArray();
        if(cells.Count!=111||cells.Select(c=>(c.X,c.Y)).Distinct().Count()!=cells.Count
            ||cells.Any(c=>c.X<0||c.Y<0||(c.Cell.BackImage&0x1fffffff)!=0
                ||(c.Cell.BackImage&0x40000000)!=0&&(c.Cell.BackImage&0x20000000)==0)
            ||!cells.Where(c=>c.Cell.MiddleImage!=0).Select(c=>(c.X,c.Y,(int)c.Cell.MiddleLibrary,(int)c.Cell.MiddleImage)).ToHashSet().SetEquals(middle)
            ||!cells.SelectMany(c=>c.Cell.ForegroundImages.Select(f=>new RiverArtworkCommand(c.X,c.Y,f.Library,f.Image&0x7fff))).ToHashSet().SetEquals(front)
            ||middle.Any(c=>!catalogue.NativeSprites.ContainsKey((c.Library,c.Image)))
            ||front.Any(c=>!catalogue.NativeSprites.ContainsKey((c.Library,c.Image))))
            throw new InvalidDataException("Native overlay must preserve forty Middle and twenty Front bank fragments without ground or scenery.");
        var original=catalogue.Pieces.Single(p=>p.Id=="west-diagonal");
        var start=original.Start with{BankX=3,BankY=4,ShoreX=original.Start.ShoreX+3,ShoreY=original.Start.ShoreY+4};
        var end=original.End with{BankX=11,BankY=13,ShoreX=original.End.ShoreX+5,ShoreY=original.End.ShoreY+4};
        return new(new("west-diagonal-parallel-8-9",cells,start,end){NativeFrontBankCommands=front});
    }
}
