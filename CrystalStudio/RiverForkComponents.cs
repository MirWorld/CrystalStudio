using System.IO;
using System.Security.Cryptography;

namespace CrystalStudio;

/// <summary>Two small saved fork joins with complete native banks and authored necessary covers.</summary>
public sealed class RiverForkComponents
{
    public IReadOnlyList<RiverCoastPieces.Piece> Pieces {get;}
    private RiverForkComponents(IReadOnlyList<RiverCoastPieces.Piece> pieces)=>Pieces=pieces;
    public static RiverForkComponents Load(RiverCoastPieces catalogue,string directory,bool completeCover=false)
    {
        var artwork=RiverArtworkCatalogue.Load(DataPaths.ResolveEditor("catalogue","river-artwork.json"),true);
        var concave=LoadPiece("diagonal-fork-concave",completeCover?"concave-complete-cover.X":"concave.X",
            completeCover?"9ae8c8af15a3c8cd47a52666c6e57112de0d6a3a91908491d5f23f1a08ef7ffc":"c1f37b4396c3bc612d36f52310865896ca6ec8086519174ce53b0d97b2841fe1",
            [("mir/mir_01377",0,0),("mir/mir_01391",1,-4)],completeCover?[("mir/mir_00217",4,-1),("mir/mir_00218",6,0),("mir/mir_00215",5,-2)]:[("mir/mir_00746",6,0)],
            Port("mir/mir_01377",0,0,1),Port("mir/mir_01391",1,-4,0));
        var returning=LoadPiece("diagonal-fork-return","return.X",
            "d8ffdb424a3c52778b5f42def818efbbc308c3a50561aa86a1edc16e7b8d8e73",
            [("mir/mir_01384",3,5),("mir/mir_01377",12,4)],[("mir/mir_00747",6,7)],
            Port("mir/mir_01384",3,5,0),Port("mir/mir_01377",12,4,0));
        return new([concave,returning]);

        RiverCoastPieces.Piece LoadPiece(string id,string file,string hash,
            (string Id,int X,int Y)[] banks,(string Id,int X,int Y)[] covers,
            RiverCoastPieces.Port start,RiverCoastPieces.Port end)
        {
            string path=Path.Combine(directory,file);
            Require(Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)))==hash,"Saved diagonal fork join changed.");
            var cells=CellCodec.LoadStamp(path);
            Require(cells.Count is >0 and <=256&&cells.Select(c=>(c.X,c.Y)).Distinct().Count()==cells.Count,"Invalid fork join footprint.");
            var expectedBanks=Commands(banks,"bank");var expectedCovers=Commands(covers,"cover");
            var storedBanks=cells.Where(c=>c.Cell.MiddleImage!=0).ToDictionary(c=>(c.X,c.Y));
            var storedCovers=cells.Where(c=>c.Cell.ForegroundImages.Count!=0).ToDictionary(c=>(c.X,c.Y));
            Require(storedBanks.Count==expectedBanks.Count&&expectedBanks.All(p=>storedBanks.TryGetValue(p.Key,out var c)
                &&(c.Cell.MiddleLibrary,c.Cell.MiddleImage)==p.Value),"Fork join must preserve both complete native banks.");
            Require(storedCovers.Count==expectedCovers.Count&&expectedCovers.All(p=>storedCovers.TryGetValue(p.Key,out var c)
                &&c.Cell.ForegroundImages.Count==1&&c.Cell.ForegroundImages[0]==new Front(checked((short)p.Value.Library),checked((short)p.Value.Image))),
                "Fork join must retain its complete required native covers.");
            Require(cells.All(c=>c.Cell.BackLibrary==0&&(c.Cell.BackImage&~0x60000000)==0
                &&c.Cell.Door==0&&c.Cell.DoorOffset==0&&c.Cell.MiddleFrames==0&&c.Cell.MiddleTick==0
                &&c.Cell.TileAnimation==0&&c.Cell.TileOffset==0&&c.Cell.TileFrames==0&&c.Cell.Light==0),
                "Fork join must use the existing static map layers and native collision flags.");
            return new(id,cells,start,end);
        }
        Dictionary<(int,int),(int Library,int Image)> Commands((string Id,int X,int Y)[] parts,string role)
        {
            var result=new Dictionary<(int,int),(int,int)>();
            foreach(var part in parts)
            {
                var source=artwork.Pieces.Single(p=>p.Id==part.Id);
                Require(role=="cover"?source.Role is "rock" or "tree":source.Role==role,"Invalid native fork source category.");
                foreach(var command in source.Commands)
                {
                    Require(catalogue.NativeSprites.ContainsKey((command.Library,command.Image)),"Missing verified native fork artwork.");
                    var key=(part.X+command.X,part.Y+command.Y);var value=(command.Library,command.Image);
                    Require(!result.TryGetValue(key,out var old)||old==value,"Fork bank fragments conflict.");result[key]=value;
                }
            }
            return result;
        }
        RiverCoastPieces.Port Port(string id,int x,int y,int endpoint)
        {
            var source=catalogue.Pieces.SelectMany(p=>new[]{p.Start,p.End}).First(p=>p.BankId==id&&p.FreeEndpoint==endpoint);
            return new(id,x,y,endpoint,x+source.ShoreX-source.BankX,y+source.ShoreY-source.BankY);
        }
    }
    private static void Require(bool value,string message){if(!value)throw new InvalidDataException(message);}
}
