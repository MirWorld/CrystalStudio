using System.IO;
using System.Security.Cryptography;
using System.Text.Json;

namespace CrystalStudio;

/// <summary>One verified native crossing with two shore paths and four bank ports.</summary>
public sealed class RiverBridgeComponent
{
    public RiverCoastPieces.Piece EastShore {get;}
    public RiverCoastPieces.Piece WestShore {get;}
    public IReadOnlyDictionary<(int,int),RiverArtwork.Pixels> NativeSprites {get;}
    // Four isolated source tree-edge pixels are naturally hidden by the bank.
    // Evidence for the unchanged native drawing order, never a rendering mask.
    public IReadOnlyList<(int X,int Y)> SavedDrawOrderOverlaps {get;}
    private readonly RiverBridgeWalkSupport walking;
    private RiverBridgeComponent(IReadOnlyList<StampCell> cells,RiverCoastPieces catalogue,
        IReadOnlyDictionary<(int,int),RiverArtwork.Pixels> sprites,RiverBridgeWalkSupport walking,bool shortUpstream,bool approach=false,bool mountainFree=false)
    {
        this.walking=walking;NativeSprites=sprites;
        SavedDrawOrderOverlaps=mountainFree?[(1296,2561),(1296,2565),(1296,2567),(1296,2569)]:[];
        EastShore=new("bridge-east-shore",cells,Port("mir/mir_01385",41,shortUpstream?35:11,0),Port("mir/mir_01384",52,80,1)){BridgeComponent=this};
        WestShore=new("bridge-west-shore",cells,Port("mir/mir_01391",mountainFree?33:27,mountainFree?93:84,1),mountainFree?Port("mir/mir_01391",15,66,0):approach?Port("mir/mir_01391",5,16,0):shortUpstream?Port("mir/mir_01391",11,25,0):Port("mir/mir_01392",9,7,0)){BridgeComponent=this};
        RiverCoastPieces.Port Port(string id,int x,int y,int endpoint)
        {
            var reference=catalogue.Pieces.SelectMany(p=>new[]{p.Start,p.End}).FirstOrDefault(p=>p.BankId==id&&p.FreeEndpoint==endpoint)
                ??throw new InvalidDataException("Native bridge port has no verified bank profile.");
            return new(id,x,y,endpoint,x+reference.ShoreX-reference.BankX,y+reference.ShoreY-reference.BankY);
        }
    }
    public static RiverBridgeComponent Load(AssetStore assets,RiverCoastPieces catalogue,string componentPath,string originalModulePath,CancellationToken cancellation=default)
    {
        var walking=RiverBridgeWalkSupport.Load(originalModulePath);
        string componentHash=Hash(componentPath);
        bool mountainFree=componentHash=="7179bfb22fa1005425a9050f5ea61d5291cce9f26d9b285b264a2918dc10bd63";
        bool approach=mountainFree||componentHash=="0290a2d1093c4718336be919b695ccca44e6f1cff5ab5b0038ea5a3ff95896fd";
        bool restoredCliffSupport=componentHash=="4df6668033e2264b5fb99552d91c9911e1a752023dad0f697beeb4b4c307f880";
        bool shortUpstream=approach||restoredCliffSupport||componentHash=="08b23de9740bdd52a362294642d94ed4f677f608a5e48bd63af37ea93f906c00";
        Require(shortUpstream||componentHash=="62a56934ccd313ca742bb18f209a34bbe4a1aeef3fa74f377b1e53f28ce6693e","Native bridge component changed.");
        var original=CellCodec.LoadStamp(originalModulePath);var cells=CellCodec.LoadStamp(componentPath);var actual=cells.ToDictionary(c=>(c.X,c.Y),c=>c.Cell);
        if(approach)ValidateApproach(assets,catalogue,originalModulePath,actual,original,cancellation,mountainFree,walking);
        else Require(actual.Count==original.Count,"Native bridge component changes its source footprint.");
        foreach(var saved in original.Where(_=>!approach))
        {
            cancellation.ThrowIfCancellationRequested();Require(actual.TryGetValue((saved.X,saved.Y),out var cell),"Missing original bridge component cell.");
            var source=saved.Cell;int image=source.BackImage&0x1fffffff;
            bool clearedSupport=shortUpstream&&saved.Y<26;
            int ground=!clearedSupport&&image is >=1801 and <=1805?image:0;
            bool retainedCliff=restoredCliffSupport&&((saved.X,saved.Y) is (10,22) or (11,22) or (10,23) or (11,23)
                or (11,24) or (12,24) or (13,24) or (12,25) or (13,25));
            int flags=clearedSupport&&!retainedCliff?0:source.BackImage&0x60000000;
            Require((cell!.MiddleLibrary==source.MiddleLibrary&&cell.MiddleImage==source.MiddleImage||cell.MiddleLibrary==0&&cell.MiddleImage==0)
                &&(cell.Foreground == source.Foreground)
                &&cell.BackImage==(flags|ground)&&cell.BackLibrary==(ground==0?0:source.BackLibrary),
                "Bridge component changes native banks, covers or collision support.");
        }
        using var json=JsonDocument.Parse(File.ReadAllText(Path.ChangeExtension(originalModulePath,"json")));
        var sprites=catalogue.NativeSprites.ToDictionary(p=>p.Key,p=>p.Value);
        foreach(var proof in json.RootElement.GetProperty("sprites").EnumerateArray())
        {
            cancellation.ThrowIfCancellationRequested();int library=proof.GetProperty("library").GetInt32(),image=proof.GetProperty("image").GetInt32();
            var sprite=assets.Libraries.TryGetValue(library,out var file)?file.Decode(image-1):null;
            Require(sprite!=null&&sprite.Width==proof.GetProperty("width").GetInt32()&&sprite.Height==proof.GetProperty("height").GetInt32(),"Missing native bridge artwork.");
            byte[] pixels=new byte[checked(sprite!.Width*sprite.Height*4)];sprite.Bitmap.CopyPixels(pixels,sprite.Width*4,0);
            Require(Convert.ToHexStringLower(SHA256.HashData(pixels))==proof.GetProperty("pixelSha256").GetString(),"Native bridge sprite pixels changed.");
            sprites[(library,image)]=new(sprite.Width,sprite.Height,pixels);
        }
        return new(cells,catalogue,sprites,walking.ForValidatedComponent(cells),shortUpstream,approach,mountainFree);
    }
    private static void ValidateApproach(AssetStore assets,RiverCoastPieces catalogue,string sourcePath,
        IReadOnlyDictionary<(int,int),Cell> actual,IReadOnlyList<StampCell> original,CancellationToken cancellation,bool mountainFree,RiverBridgeWalkSupport walking)
    {
        string baselinePath=DataPaths.ResolveEditor("catalogue","river-coast-layouts","bridges","bridge-crossing.X");
        Require(Hash(baselinePath)=="08b23de9740bdd52a362294642d94ed4f677f608a5e48bd63af37ea93f906c00","Bridge approach reference changed.");
        var baseline=Load(assets,catalogue,baselinePath,sourcePath,cancellation);
        var expected=baseline.WestShore.Cells.ToDictionary(c=>(c.X,c.Y),c=>c.Cell.Copy());
        var source=original.ToDictionary(c=>(c.X,c.Y),c=>c.Cell);
        (int,int)[] cliff=[(10,22),(11,22),(10,23),(11,23),(11,24),(12,24),(13,24),(12,25),(13,25)];
        foreach(var p in cliff)expected[p].BackImage=source[p].BackImage&0x60000000;
        foreach(var saved in catalogue.Pieces.Single(p=>p.Id=="west-diagonal").Cells)
        {
            var key=(saved.X+5,saved.Y+16);var donor=saved.Cell;
            Require(donor.ForegroundImages.Count==0&&(donor.BackImage&0x1fffffff)==0,"Bridge approach bank must contain unchanged native bank cells only.");
            if(!expected.TryGetValue(key,out var cell))expected[key]=cell=donor.Copy();
            if(donor.MiddleImage!=0)
            {
                Require(cell.MiddleImage==0||cell.MiddleLibrary==donor.MiddleLibrary&&cell.MiddleImage==donor.MiddleImage,"Bridge approach banks disagree.");
                cell.MiddleLibrary=donor.MiddleLibrary;cell.MiddleImage=donor.MiddleImage;
            }
            cell.BackImage|=donor.BackImage&0x60000000;
        }
        Require(!expected.ContainsKey((11,27)),"Cliff support must occupy its previously empty collision cell.");
        expected[(11,27)]=new Cell{BackImage=0x20000000};
        if(mountainFree)
        {
            foreach(var (point,cell) in expected)
            {
                if (cell.Foreground is { } f && !walking.IsDeck(point.Item1,point.Item2,f.Library,f.Image&0x7fff)&&((int)f.Library,f.Image&0x7fff) is not ((2,964) or (2,3059) or (2,3068))) cell.Foreground = null;
                if(point.Item1<35){cell.MiddleLibrary=0;cell.MiddleImage=0;}
                int image=cell.BackImage&0x1fffffff;
                int ground=(point.Item1>=35||cell.ForegroundImages.Count>0)&&image is >=1801 and <=1805?image:0;
                cell.BackImage=(cell.BackImage&0x60000000)|ground;
                if(ground==0)cell.BackLibrary=0;
                if(cell.MiddleImage==0&&cell.ForegroundImages.Count==0)cell.BackImage&=0x1fffffff;
            }
            (string Id,bool Reverse)[] route=[("west-diagonal",true),("west-diagonal",true),("west-diagonal",true)];
            var current=new RiverCoastPieces.Port("mir/mir_01391",33,93,0);
            foreach(var step in route)
            {
                var piece=catalogue.Pieces.Single(p=>p.Id==step.Id);
                var start=step.Reverse?piece.End:piece.Start;var end=step.Reverse?piece.Start:piece.End;
                Require(current.BankId==start.BankId&&current.FreeEndpoint!=start.FreeEndpoint,"Mountain-free boundary has incompatible native ports.");
                int x=current.BankX-start.BankX,y=current.BankY-start.BankY;
                foreach(var saved in piece.Cells)
                {
                    var key=(saved.X+x,saved.Y+y);var donor=saved.Cell;
                    if(!expected.TryGetValue(key,out var cell)){expected[key]=donor.Copy();continue;}
                    if(donor.MiddleImage!=0)
                    {
                        Require(cell.MiddleImage==0||cell.MiddleLibrary==donor.MiddleLibrary&&cell.MiddleImage==donor.MiddleImage,"Mountain-free boundary banks overlap incompatible artwork.");
                        cell.MiddleLibrary=donor.MiddleLibrary;cell.MiddleImage=donor.MiddleImage;
                    }
                    if(donor.ForegroundImages.Count>0)
                    {
                        Require(cell.ForegroundImages.Count==0||(cell.Foreground == donor.Foreground),"Mountain-free boundary covers overlap the bridge.");
                        cell.Foreground=donor.Foreground;
                    }
                    cell.BackImage|=donor.BackImage&0x60000000;
                }
                current=end with{BankX=x+end.BankX,BankY=y+end.BankY};
            }
            Require(current.BankId=="mir/mir_01391"&&current.BankX==15&&current.BankY==66&&current.FreeEndpoint==0,"Mountain-free bank path changed its endpoint.");
            var cover=RiverArtworkCatalogue.Load(DataPaths.ResolveEditor("catalogue","river-artwork.json"),true)
                .Pieces.Single(p=>p.Id=="mir/mir_00217");
            Require(cover.SourceSha256=="6eac7434073349675f5b415000dbe50a9dd20add2907049c2cf32bdd0d56fe99"
                &&cover.Role=="tree"&&cover.Commands.Count==8,"Bridge abutment cover source changed.");
            foreach(var command in cover.Commands)
            {
                var key=(command.X+23,command.Y+77);
                if(!expected.TryGetValue(key,out var cell))expected[key]=cell=new Cell{MiddleLibrary=1};
                Require(cell.ForegroundImages.Count==0,"Bridge cover overlaps source deck artwork.");
                cell.Foreground = new Front(checked((short)command.Library),checked((short)command.Image));
            }
            // Preserve the two collision cells from the pinned original tree.
            foreach(var key in new[]{(23,79),(23,80)})
            {
                if(!expected.TryGetValue(key,out var cell))expected[key]=cell=new Cell{MiddleLibrary=1};
                cell.BackImage|=0x60000000;
            }
            foreach(var key in expected.Where(p=>p.Value.MiddleImage==0&&p.Value.ForegroundImages.Count==0&&p.Value.BackImage==0).Select(p=>p.Key).ToArray())expected.Remove(key);
        }
        Require(actual.Count==expected.Count&&expected.All(p=>actual.TryGetValue(p.Key,out var c)&&Same(c,p.Value)),"Bridge approach changes verified native cells or source support.");
    }
    private static bool Same(Cell a,Cell b)=>a.BackLibrary==b.BackLibrary&&a.BackImage==b.BackImage
        &&a.MiddleLibrary==b.MiddleLibrary&&a.MiddleImage==b.MiddleImage&&(a.Foreground == b.Foreground)
        &&a.Door==b.Door&&a.DoorOffset==b.DoorOffset&&a.MiddleFrames==b.MiddleFrames&&a.MiddleTick==b.MiddleTick
        &&a.TileAnimation==b.TileAnimation&&a.TileOffset==b.TileOffset&&a.TileFrames==b.TileFrames&&a.Light==b.Light;
    internal RiverBridgeWalkSupport WalkingAt(int x,int y)=>walking.Translate(x,y);
    private static string Hash(string path)=>Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));
    private static void Require(bool value,string message){if(!value)throw new InvalidDataException(message);}
}
