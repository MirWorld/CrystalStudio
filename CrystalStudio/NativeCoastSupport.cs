using System.IO;
using System.Security.Cryptography;
using System.Text.Json;

namespace CrystalStudio;

/// <summary>Checks fixed native crossing dependencies. No fitting or placement runs here.</summary>
internal sealed class NativeCoastSupport
{
    public Dictionary<(int,int),(int Library,int Image)> Ground { get; } = [];
    public Dictionary<(int,int),RiverArtworkCommand> Middle { get; } = [];
    public Dictionary<(int,int),RiverArtworkCommand> Front { get; } = [];
    private readonly Dictionary<(int,int),int> collision = [];
    private readonly HashSet<(int,int)> exactCollision = [];
    private readonly HashSet<(int,int)> walk = [];

    public static NativeCoastSupport Read(JsonElement proof,int width,int height,AssetStore assets,CancellationToken cancellation)
    {
        var result=new NativeCoastSupport();
        if(proof.TryGetProperty("nativeBankGround",out var bankGround))
        {
            Require(proof.GetProperty("bankPlacements").GetArrayLength()==2&&proof.GetProperty("coverPlacements").GetArrayLength()==0,
                "Native ground join must retain exactly its two original banks without scenery.");
            var source=bankGround.GetProperty("sourceMap");var anchor=bankGround.GetProperty("sourceBankAnchor");var offset=bankGround.GetProperty("sourceStampOffset");
            string hash=Text(source,"sha256");
            Require(hash.Length==64&&hash.All(char.IsAsciiHexDigit)&&((Number(anchor,"x")+Number(offset,"x"))&1)==0
                &&((Number(anchor,"y")+Number(offset,"y"))&1)==0,"Native ground join source/alignment proof is invalid.");
            var references=new HashSet<(int,int)>();
            foreach(var spriteProof in bankGround.GetProperty("groundSprites").EnumerateArray())
            {
                cancellation.ThrowIfCancellationRequested();int library=Number(spriteProof,"library"),image=Number(spriteProof,"image");
                var sprite=assets.Libraries.TryGetValue(library,out var lib)?lib.Decode(image-1):null;
                Require(library==0&&(image is >=1 and <=5 or >=1801 and <=1805 or >=2301 and <=2400)
                    &&sprite!=null&&sprite.Width==96&&sprite.Height==64,"Unverified native bank ground family or dimensions.");
                Require(references.Add((library,image)),
                    "Duplicate native bank ground reference.");
            }
            foreach(var cell in bankGround.GetProperty("supportCells").EnumerateArray())
            {
                int x=Number(cell,"x"),y=Number(cell,"y"),back=Number(cell,"backImage"),library=Number(cell,"backLibrary");
                Require(x>=0&&y>=0&&x<width&&y<height&&library==0&&result.collision.TryAdd((x,y),back&0x60000000),
                    "Native bank ground support is repeated or outside its saved join.");
                result.exactCollision.Add((x,y));
                int image=back&0x1fffffff;
                if(image!=0)
                {
                    Require(((x|y)&1)==0&&references.Contains((library,image)),"Native bank ground has an unproved or misaligned tile.");
                    result.Ground.Add((x,y),(library,image));
                }
            }
            Require(result.Ground.Count>0&&result.collision.Count==result.Ground.Count*4,
                "Native ground join must retain all four collision cells beneath each ground tile.");
            foreach(var (x,y) in result.Ground.Keys)
                Require(result.collision.ContainsKey((x+1,y))&&result.collision.ContainsKey((x,y+1))&&result.collision.ContainsKey((x+1,y+1)),
                    "Native ground join drops part of its original collision support.");
        }
        if(proof.TryGetProperty("nativeCrossings",out var crossings))
        {
            Require(crossings.GetArrayLength()<=4,"Too many native crossings.");
            if(crossings.GetArrayLength()>0)Require(Number(proof,"collisionConnectivityNeighbours")==8,
                "Native bridge access must match the game's eight movement directions.");
            foreach(var crossing in crossings.EnumerateArray())
            {
                cancellation.ThrowIfCancellationRequested();
                string file=Text(crossing,"file").Replace('\\','/');
                Require(file=="catalogue/river-coast-layouts/modules/province-cliff-bridge-crossing.X"
                    &&Text(crossing,"sha256")=="79e7810c61e05ca9186ab9a3506e1125683f3181b4d9ceb8a598ce1180d3aee9","Unverified native crossing dependency.");
                string path=DataPaths.ResolveEditor(file.Replace('/',Path.DirectorySeparatorChar));
                Require(Hash(path)==Text(crossing,"sha256"),"Native crossing object changed.");
                using var document=JsonDocument.Parse(File.ReadAllText(Path.ChangeExtension(path,"json")));var source=document.RootElement;
                Require(Text(source,"sha256")==Text(crossing,"sha256")&&Number(source.GetProperty("bridgeSource"),"matchedFragments")==122
                    &&Text(source.GetProperty("sourceMap"),"sha256")=="ef70bd5f5425f7f9dc79cb1ea6957f771c7293d55a554b3ba6239aba372fa370",
                    "Native crossing source proof changed.");
                int x=Number(crossing,"x"),y=Number(crossing,"y"),w=Number(source,"width"),h=Number(source,"height");
                Require((x&1)==0&&(y&1)==0&&x>=0&&y>=0&&w==60&&h==92&&x+w<=width&&y+h<=height,"Native crossing is out of bounds.");
                var cells=CellCodec.LoadStamp(path).ToDictionary(c=>(c.X,c.Y),c=>c.Cell);
                var references=new HashSet<(int,int)>();
                foreach(var spriteProof in source.GetProperty("sprites").EnumerateArray())
                {
                    cancellation.ThrowIfCancellationRequested();int library=Number(spriteProof,"library"),image=Number(spriteProof,"image");
                    var sprite=assets.Libraries.TryGetValue(library,out var lib)?lib.Decode(image-1):null;
                    Require(sprite!=null&&sprite.Width==Number(spriteProof,"width")&&sprite.Height==Number(spriteProof,"height"),"Missing native crossing sprite.");
                    Require(references.Add((library,image)),"Duplicate native crossing reference.");
                }
                foreach(var (point,cell) in cells)
                {
                    var target=(point.Item1+x,point.Item2+y);
                    if(cell.MiddleImage!=0)
                    {
                        Require(references.Contains((cell.MiddleLibrary,cell.MiddleImage)),"Unproved native Middle artwork.");
                        result.Middle.Add(target,new(target.Item1,target.Item2,cell.MiddleLibrary,cell.MiddleImage));
                    }
                    foreach(var front in cell.ForegroundImages)
                    {
                        Require(references.Contains((front.Library,front.Image))&&front.Frames==0&&front.Tick==0,"Unproved native Front artwork.");
                        result.Front.Add(target,new(target.Item1,target.Item2,front.Library,front.Image));
                    }
                }
                var support=new HashSet<(int,int)>();
                foreach(var point in crossing.GetProperty("collisionSupport").EnumerateArray())
                {
                    int sx=Number(point,"x"),sy=Number(point,"y");Require(sx>=0&&sy>=0&&sx<w&&sy<h&&support.Add((sx,sy)),"Invalid native collision support.");
                    result.collision.Add((x+sx,y+sy),cells.GetValueOrDefault((sx,sy),new Cell()).BackImage&0x60000000);
                }
                Require(support.Count==w*h,"Native collision support must retain the whole source rectangle.");
                foreach(var point in crossing.GetProperty("groundSupport").EnumerateArray())
                {
                    int sx=Number(point,"x"),sy=Number(point,"y");
                    var cell=cells.GetValueOrDefault((sx,sy));
                    Require((sx&1)==0&&(sy&1)==0&&cell!=null&&(cell.BackImage&0x1fffffff)!=0,"Invalid native ground support.");
                    result.Ground.Add((x+sx,y+sy),(cell!.BackLibrary,cell.BackImage&0x1fffffff));
                }
                foreach(var point in source.GetProperty("pixelVerifiedCrossing").EnumerateArray())result.walk.Add((x+Number(point,"x"),y+Number(point,"y")));
                Require(Number(crossing,"changedVisiblePixelsBeforeCovers")==0&&Number(crossing,"changedOpaquePixelsBeforeCovers")==0,
                    "Native crossing pixels changed during authoring.");
            }
        }
        if(result.Front.Count>0&&proof.TryGetProperty("savedLayouts",out var savedLayouts))foreach(var saved in savedLayouts.EnumerateArray())
        {
            string file=Text(saved,"file").Replace('\\','/');
            Require(file=="catalogue/river-coast-layouts/chains/northwest-corner.X"
                &&Text(saved,"sha256")=="745d0de61a3c2bbf417890a77a63b09d91a4e766b56fd8daafdad7e666a791cd","Unverified crossing closing module.");
            string path=DataPaths.ResolveEditor(file.Replace('/',Path.DirectorySeparatorChar));Require(Hash(path)==Text(saved,"sha256"),"Crossing closing module changed.");
            int x=Number(saved,"x"),y=Number(saved,"y");Require((x&1)==0&&(y&1)==0&&x>=0&&y>=0,"Invalid closing-module alignment.");
            // Offline composition gives this verified whole closing module the
            // last ground layer. This checks the saved result; it places nothing.
            foreach(var cell in CellCodec.LoadStamp(path))if((cell.Cell.BackImage&0x1fffffff)!=0)
            {
                Require(x+cell.X<width&&y+cell.Y<height,"Closing module ground is out of bounds.");
                result.Ground[(x+cell.X,y+cell.Y)]=(cell.Cell.BackLibrary,cell.Cell.BackImage&0x1fffffff);
            }
        }
        if(proof.TryGetProperty("nativeCliffGrass",out var grass)&&grass.ValueKind!=JsonValueKind.Null)
        {
            Require(result.Front.Count>0&&grass.GetProperty("nativeCrossingGroundPreserved").GetBoolean()&&!grass.GetProperty("runtimePainting").GetBoolean(),"Invalid saved cliff grass proof.");
            var terrain=TerrainCatalogue.Load();var dirt=terrain.Sets.Single(s=>s.Id=="0:0:0");var edge=terrain.Sets.Single(s=>s.Id=="0:0:950");var centre=terrain.Sets.Single(s=>s.Id=="0:0:1400");
            Require(terrain.CanBlend(dirt,edge)&&terrain.MatchesAssets(edge,assets)&&terrain.MatchesAssets(centre,assets),"Saved cliff grass needs reviewed original terrain.");
            foreach(var tile in grass.GetProperty("tiles").EnumerateArray())
            {
                int x=Number(tile,"x"),y=Number(tile,"y"),mask=Number(tile,"mask"),image=Number(tile,"image");var set=mask==15?centre:edge;
                Require(x>=0&&y>=0&&x<width&&y<height&&(x&1)==0&&(y&1)==0&&set.Shapes.TryGetValue(mask,out var variants)&&variants.Any(v=>v.Index+1==image),"Invalid saved cliff grass tile.");
                result.Ground.Add((x,y),(0,image));
            }
        }
        return result;
    }

    public void Validate(IReadOnlyList<StampCell> cells)
    {
        var actual=cells.ToDictionary(c=>(c.X,c.Y),c=>c.Cell);
        Require(Ground.All(p=>actual.TryGetValue(p.Key,out var cell)&&cell.BackLibrary==p.Value.Library&&(cell.BackImage&0x1fffffff)==p.Value.Image),"Saved coast changes native ground or grass support.");
        Require(collision.All(p=>(actual.GetValueOrDefault(p.Key,new Cell()).BackImage&p.Value)==p.Value),"Saved coast drops original native collision flags.");
        Require(exactCollision.All(p=>(actual.GetValueOrDefault(p,new Cell()).BackImage&0x60000000)==collision[p]),
            "Saved bank join changes its exact original ground collision support.");
        Require(walk.All(p=>
        {
            // Empty source cells may be absent from an ordinary sparse .X file.
            var cell=actual.GetValueOrDefault(p,new Cell());
            return (cell.BackImage&0x60000000)==0&&!cell.ForegroundImages.Any(f=>(f.Image&0x8000)!=0);
        }),"Saved coast blocks the native bridge walking route.");
    }

    private static int Number(JsonElement element,string name)=>element.GetProperty(name).GetInt32();
    private static string Text(JsonElement element,string name)=>element.GetProperty(name).GetString()??"";
    private static string Hash(string path)=>Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));
    private static void Require(bool valid,string message){if(!valid)throw new InvalidDataException(message);}
}
