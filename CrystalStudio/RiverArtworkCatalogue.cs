using System.IO;
using System.Security.Cryptography;
using System.Text.Json;

namespace CrystalStudio;

public sealed record RiverArtworkCommand(int X,int Y,int Library,int Image);
public sealed record RiverArtworkUse(string FileName,int Count);
public sealed record RiverArtworkPiece(string Id,string Name,string Role,string SourceSha256,
    List<RiverArtworkCommand> Commands,List<RiverArtworkUse> Maps);
public sealed record RiverArtworkProof(int Library,int Image,int Width,int Height,string PixelSha256);
public sealed record RiverArtworkJoin(string FromId,string ToId,int Dx,int Dy,int Count);

/// <summary>References existing native artwork. No generated library pixels.</summary>
public sealed record RiverArtworkCatalogue(int SchemaVersion,string Status,string Name,string ReviewBasis,
    int EdgeBufferCells,string LandId,string WaterId,List<RiverArtworkPiece> Pieces,List<RiverArtworkProof> Sprites)
{
    public List<RiverArtworkJoin> Joins {get;init;}=[];
    public static RiverArtworkCatalogue Load(string path,bool allowResearch=false)
    {
        var value=JsonSerializer.Deserialize<RiverArtworkCatalogue>(File.ReadAllText(path),new JsonSerializerOptions{PropertyNameCaseInsensitive=true})
            ??throw new InvalidDataException("Empty river artwork catalogue.");
        if(value.SchemaVersion!=1||value.Status!="reviewed"&&!(allowResearch&&value.Status=="research")
            ||value.EdgeBufferCells!=0||string.IsNullOrWhiteSpace(value.ReviewBasis)
            ||value.Pieces.Count==0||value.Pieces.Select(p=>p.Id).Distinct().Count()!=value.Pieces.Count)
            throw new InvalidDataException("Invalid or unreviewed river artwork catalogue.");
        var refs=value.Sprites.Select(p=>(p.Library,p.Image)).ToHashSet();
        if(refs.Count!=value.Sprites.Count||value.Sprites.Any(p=>p.Width is <1 or >2048||p.Height is <1 or >2048||p.PixelSha256.Length!=64||p.Image is <1 or >short.MaxValue))
            throw new InvalidDataException("Invalid river sprite proofs.");
        foreach(var piece in value.Pieces)
            if(piece.Role is not ("bank" or "rock" or "tree" or "shrub")||piece.SourceSha256.Length!=64||piece.Commands.Count is <1 or >1000
                ||piece.Commands.Select(p=>(p.X,p.Y)).Distinct().Count()!=piece.Commands.Count
                ||piece.Commands.Any(p=>Math.Abs((long)p.X)>100||Math.Abs((long)p.Y)>100||!refs.Contains((p.Library,p.Image))))
                throw new InvalidDataException("Invalid river artwork piece: "+piece.Id);
        var banks=value.Pieces.Where(p=>p.Role=="bank").Select(p=>p.Id).ToHashSet();
        if(value.Joins.Any(j=>!banks.Contains(j.FromId)||!banks.Contains(j.ToId)||j.Count<=0
            ||Math.Abs((long)j.Dx)>100||Math.Abs((long)j.Dy)>100))throw new InvalidDataException("Invalid native river bank join.");
        return value;
    }

    public RiverArtwork Prepare(AssetStore assets,CancellationToken token=default)
        => new(this,VerifySprites(assets,token),token);

    /// <summary>Verify saved-layout dependencies without deriving procedural piece masks or joins.</summary>
    public RiverArtwork PrepareSavedLayouts(AssetStore assets,CancellationToken token=default)
        => new(this,VerifySprites(assets,token),token,preparePieces:false);

    private Dictionary<(int,int),RiverArtwork.Pixels> VerifySprites(AssetStore assets,CancellationToken token)
    {
        var sprites=new Dictionary<(int,int),RiverArtwork.Pixels>();
        foreach(var proof in Sprites)
        {
            token.ThrowIfCancellationRequested();
            var sprite=assets.Libraries.TryGetValue(proof.Library,out var library)?library.Decode(proof.Image-1):null;
            if(sprite==null||sprite.Width!=proof.Width||sprite.Height!=proof.Height)
                throw new InvalidDataException($"Missing native river artwork {proof.Library}:{proof.Image}.");
            byte[] bytes=new byte[checked(sprite.Width*sprite.Height*4)];sprite.Bitmap.CopyPixels(bytes,sprite.Width*4,0);
            sprites.Add((proof.Library,proof.Image),new(sprite.Width,sprite.Height,bytes));
        }
        return sprites;
    }
}

public sealed class RiverArtwork
{
    public sealed record Pixels(int Width,int Height,byte[] Bgra);
    public sealed record Sample(int X,int Y,int Depth,bool Water);
    public sealed record Piece(RiverArtworkPiece Source,int Left,int Top,int Width,int Height,
        byte[] Bgra,Sample[] Opaque,Sample[] Footprint,Sample[] Border,int RootX,int RootY);
    public RiverArtworkCatalogue Catalogue {get;}
    public IReadOnlyList<Piece> Pieces {get;}
    public IReadOnlyDictionary<(int,int),Pixels> Sprites {get;}

    internal RiverArtwork(RiverArtworkCatalogue catalogue,Dictionary<(int,int),Pixels> sprites,CancellationToken token,bool preparePieces=true)
    {
        Catalogue=catalogue;Sprites=sprites;
        if (!preparePieces) { Pieces=[]; return; }
        var sources=catalogue.Pieces.ToList();
        foreach(var join in catalogue.Joins)
        {
            var a=catalogue.Pieces.Single(p=>p.Id==join.FromId);var b=catalogue.Pieces.Single(p=>p.Id==join.ToId);
            var commands=a.Commands.Concat(b.Commands.Select(c=>c with {X=c.X+join.Dx,Y=c.Y+join.Dy})).ToList();
            if(commands.GroupBy(c=>(c.X,c.Y)).Any(g=>g.Select(c=>(c.Library,c.Image)).Distinct().Count()!=1))
                throw new InvalidDataException("Native river join contains conflicting anchors.");
            string id=$"join:{a.Id}:{b.Id}:{join.Dx}:{join.Dy}";
            string proof=Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(id+":"+a.SourceSha256+":"+b.SourceSha256)));
            sources.Add(new(id,"Joined Province banks","bank",proof,commands.DistinctBy(c=>(c.X,c.Y)).ToList(),[]));
        }
        Pieces=sources.Select(Prepare).ToArray();
        Piece Prepare(RiverArtworkPiece source)
        {
            token.ThrowIfCancellationRequested();
            var commands=source.Commands.OrderBy(c=>c.Y).ThenBy(c=>c.X).ToArray();
            bool Floor(RiverArtworkCommand c)=>IsFloor(sprites[(c.Library,c.Image)]);
            int Top(RiverArtworkCommand c)=>c.Y*32+(Floor(c)?0:32-sprites[(c.Library,c.Image)].Height);
            int left=commands.Min(c=>c.X*48),top=commands.Min(Top);
            left=(int)Math.Floor(left/4.0)*4;top=(int)Math.Floor(top/4.0)*4;
            int right=commands.Max(c=>c.X*48+sprites[(c.Library,c.Image)].Width),bottom=commands.Max(c=>Top(c)+sprites[(c.Library,c.Image)].Height);
            int width=(right-left+3)/4,height=(bottom-top+3)/4;
            int fullWidth=width*4,fullHeight=height*4;
            byte[] pixels=new byte[checked(fullWidth*fullHeight*4)];
            var depth=new int[width*height];Array.Fill(depth,int.MinValue);
            // The native piece retains its original per-cell draw order. This
            // buffer only derives placement masks; saved artwork stays native.
            foreach(int commandIndex in Enumerable.Range(0,commands.Length).OrderBy(i=>Floor(commands[i])?0:1).ThenBy(i=>i))
            {
                var c=commands[commandIndex];
                var sprite=sprites[(c.Library,c.Image)];int ox=c.X*48-left,oy=Top(c)-top;
                for(int y=0;y<sprite.Height;y++)for(int x=0;x<sprite.Width;x++)
                {
                    int src=(y*sprite.Width+x)*4,a=sprite.Bgra[src+3];if(a==0)continue;
                    int dx=x+ox,dy=y+oy,dst=(dy*fullWidth+dx)*4;
                    int old=pixels[dst+3],alpha=a*255+old*(255-a);
                    for(int k=0;k<3;k++)pixels[dst+k]=(byte)((sprite.Bgra[src+k]*a*255+pixels[dst+k]*old*(255-a)+alpha/2)/alpha);
                    pixels[dst+3]=(byte)((alpha+127)/255);
                    // Depth encodes only the source anchor here. The planner
                    // adds its placement offset using the actual map width.
                    depth[dy/4*width+dx/4]=commandIndex;
                }
            }
            var reduced=new byte[width*height*4];var opaque=new List<Sample>();var footprint=new List<Sample>();
            for(int y=0;y<height;y++)for(int x=0;x<width;x++)
            {
                int alpha=0,b=0,g=0,r=0;
                for(int dy=0;dy<4;dy++)for(int dx=0;dx<4;dx++)
                {
                    int p=((y*4+dy)*fullWidth+x*4+dx)*4,a=pixels[p+3];
                    alpha+=a;b+=pixels[p]*a;g+=pixels[p+1]*a;r+=pixels[p+2]*a;
                }
                int key=y*width+x,pixel=key*4;
                if(alpha>0){reduced[pixel]=(byte)((b+alpha/2)/alpha);reduced[pixel+1]=(byte)((g+alpha/2)/alpha);reduced[pixel+2]=(byte)((r+alpha/2)/alpha);}
                reduced[pixel+3]=(byte)((alpha+8)/16);
                bool water=reduced[pixel]>reduced[pixel+2]+35&&reduced[pixel]>reduced[pixel+1]+15;
                var sample=new Sample(x,y,depth[key],water);
                if(reduced[pixel+3]>250)opaque.Add(sample);
                if(reduced[pixel+3]>0)footprint.Add(sample);
            }
            var border=opaque.Where(p=>p.X==0||p.Y==0||p.X==width-1||p.Y==height-1
                ||reduced[((p.Y-1)*width+p.X)*4+3]<=250||reduced[((p.Y+1)*width+p.X)*4+3]<=250
                ||reduced[(p.Y*width+p.X-1)*4+3]<=250||reduced[(p.Y*width+p.X+1)*4+3]<=250).ToArray();
            int rootY=opaque.Max(p=>p.Y);var roots=opaque.Where(p=>p.Y>=rootY-2).Select(p=>p.X).Order().ToArray();
            int rootX=(roots[(roots.Length-1)/2]+roots[roots.Length/2])/2;
            return new(source with {Commands=commands.ToList()},left/4,top/4,width,height,reduced,opaque.ToArray(),footprint.ToArray(),border,rootX,rootY);
        }
    }

    internal static bool IsFloor(Pixels sprite)=>sprite.Width==48&&sprite.Height==32||sprite.Width==96&&sprite.Height==64;
}
