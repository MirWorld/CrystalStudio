using System.IO;
using System.Security.Cryptography;
using System.Text.Json;

namespace CrystalStudio;

public sealed record RiverCoastLayout(string Id,string Name,IReadOnlyList<StampCell> Cells)
{
    public int Width { get; init; }
    public int Height { get; init; }
    public int BranchCount { get; init; }
    public string EntryEdge { get; init; } = "";
    public bool HasNativeBridge { get; init; }
    public TerrainWaterMode WaterMode { get; init; } = TerrainWaterMode.River;
    public IReadOnlyList<string> BoundaryEdges { get; init; } = [];
}

/// <summary>Finished .X coast layouts. Placement uses the complete saved stamp.</summary>
public sealed class RiverCoastLayoutCatalogue
{
    public IReadOnlyList<RiverCoastLayout> Layouts {get;}
    private RiverCoastLayoutCatalogue(IReadOnlyList<RiverCoastLayout> layouts)=>Layouts=layouts;

    /// <summary>Load a complete authored coast chain, including its saved ground and covers.</summary>
    public static RiverCoastLayout LoadComplete(string path,RiverArtwork artwork,AssetStore assets,bool allowResearch=false,CancellationToken cancellation=default)
    {
        cancellation.ThrowIfCancellationRequested();
        if(new FileInfo(path).Length>1024*1024)throw new InvalidDataException("Complete coast object is too large.");
        string metadata=Path.ChangeExtension(path,"json");
        if(new FileInfo(metadata).Length>8*1024*1024)throw new InvalidDataException("Coast object proof is too large.");
        using var json=JsonDocument.Parse(File.ReadAllText(metadata));var proof=json.RootElement;string status=Text(proof,"status");
        Require(status=="reviewed"||allowResearch&&status.StartsWith("research",StringComparison.Ordinal),"Unreviewed complete coast layout.");
        Require(Number(proof,"remainingExposedGroundSamples")==0&&Number(proof,"disconnectedWalkableCells")==0,
            "Saved coast still has an exposed seam or disconnected walkable land.");
        Require(Hash(path)==Text(proof,"sha256"),"Complete saved coast object checksum changed.");
        var cells=CellCodec.LoadStamp(path);int width=Number(proof,"width"),height=Number(proof,"height");
        Require(width is >0 and <=1024&&height is >0 and <=1024&&cells.Count is >0 and <=30000
            &&cells.Select(c=>(c.X,c.Y)).Distinct().Count()==cells.Count,"Invalid complete coast dimensions or cells.");
        string edge=proof.TryGetProperty("entryEdge",out var entry)?entry.GetString()??"":"";
        Require(edge is "" or "north" or "west" or "east" or "south","Invalid saved river entry edge.");
        string[] edges=proof.TryGetProperty("boundaryEdges",out var boundaries)?boundaries.EnumerateArray().Select(e=>e.GetString()??"").ToArray():edge==""?[]:[edge];
        Require(edges.Length<=2&&edges.Distinct().Count()==edges.Length&&edges.All(e=>e is "north" or "west" or "east" or "south")
            &&!(edges.Contains("north")&&edges.Contains("south"))&&!(edges.Contains("west")&&edges.Contains("east"))
            &&(edge==""||edges.SequenceEqual([edge])),"Invalid saved coast boundaries.");
        if(edges.Length>0)ValidateBoundarySource(path,proof,cells,width,height,artwork,assets,allowResearch,cancellation);
        else Require(!proof.TryGetProperty("boundarySource",out _),"Boundary source needs a declared entry edge.");
        var original=artwork.Catalogue.Pieces.ToDictionary(p=>p.Id);
        var banks=new Dictionary<(int,int),RiverArtworkCommand>();var covers=new Dictionary<(int,int),List<RiverArtworkCommand>>();
        foreach(string property in new[]{"bankPlacements","coverPlacements"})
        {
            var placements=proof.GetProperty(property).EnumerateArray().ToArray();Require(placements.Length<=200,"Too many coast components.");
            foreach(var placement in placements)
            {
                cancellation.ThrowIfCancellationRequested();bool cover=property=="coverPlacements";
                Require(original.TryGetValue(Text(placement,"id"),out var source)&&source.SourceSha256==Text(placement,"sourceSha256")
                    &&(cover?source.Role!="bank":source.Role=="bank"),"Complete coast source artwork differs from its proof.");
                if(cover)Require(Number(placement,"coveredSamples")>0&&Text(placement,"purpose").StartsWith("Cover exposed ground seam ",StringComparison.Ordinal),"Optional coast scenery is forbidden.");
                int x=Number(placement,"x"),y=Number(placement,"y");Require(Math.Abs((long)x)<=1024&&Math.Abs((long)y)<=1024,"Coast source placement is out of range.");
                foreach(var command in source!.Commands)
                {
                    var c=command with {X=command.X+x,Y=command.Y+y};
                    if(!cover&&(edges.Contains("north")&&c.Y<0||edges.Contains("west")&&c.X<0||edges.Contains("east")&&c.X>=width||edges.Contains("south")&&c.Y>=height))continue;
                    if(cover)
                    {
                        if(!covers.TryGetValue((c.X,c.Y),out var commands))covers[(c.X,c.Y)]=commands=[];
                        if(!commands.Contains(c))commands.Add(c);
                    }
                    else{Require(!banks.TryGetValue((c.X,c.Y),out var old)||old==c,"Conflicting complete coast fragments.");banks[(c.X,c.Y)]=c;}
                }
            }
        }
        var native=NativeCoastSupport.Read(proof,width,height,assets,cancellation);
        foreach(var (point,command) in native.Middle)
        {
            Require(!banks.TryGetValue(point,out var old)||old==command,"Saved bank conflicts with the native crossing.");banks[point]=command;
        }
        foreach(var (point,command) in native.Front)
        {
            Require(!covers.ContainsKey(point),"Saved cover replaces native crossing artwork.");covers.Add(point,[command]);
        }
        native.Validate(cells);
        var ground=new HashSet<(int,int)>();
        foreach(var spriteProof in proof.GetProperty("groundSprites").EnumerateArray())
        {
            cancellation.ThrowIfCancellationRequested();int library=Number(spriteProof,"library"),image=Number(spriteProof,"image");
            Require(library==0&&(image is >=1 and <=5 or >=1801 and <=1805||native.Ground.Values.Contains((library,image))),"Complete coast uses an unreviewed ground family.");
            var sprite=assets.Libraries.TryGetValue(library,out var lib)?lib.Decode(image-1):null;
            Require(sprite!=null&&sprite.Width==96&&sprite.Height==64,"Missing original complete-coast ground artwork.");
            Require(ground.Add((library,image)),"Duplicate complete-coast ground reference.");
        }
        bool generatorGround=proof.TryGetProperty("groundOwnership",out var ownership)&&ownership.GetString()=="generator";
        if(generatorGround)Require(proof.GetProperty("savedUnit").GetString()=="bank/join piece; never a complete river"
            &&proof.GetProperty("bankPlacements").GetArrayLength()==2&&proof.GetProperty("coverPlacements").GetArrayLength() is >0 and <=8
            &&proof.GetProperty("coverPlacements").EnumerateArray().All(p=>Number(p,"uniqueRequiredSamples")>0)
            &&width<=128&&height<=128&&ground.Count==0&&cells.All(c=>(c.Cell.BackImage&0x1fffffff)==0),
            "Only a small covered bank join may leave ground to the terrain generator.");
        Require(banks.Count>0&&(ground.Count>0||generatorGround),"Saved coast needs banks and either native ground or explicit small-piece ground ownership.");
        var storedBanks=cells.Where(c=>c.Cell.MiddleImage!=0).ToDictionary(c=>(c.X,c.Y));var storedCovers=cells.Where(c=>c.Cell.ForegroundImages.Count!=0).ToDictionary(c=>(c.X,c.Y));
        Require(storedBanks.Count==banks.Count&&banks.All(p=>storedBanks.TryGetValue(p.Key,out var c)&&c.Cell.MiddleLibrary==p.Value.Library&&c.Cell.MiddleImage==p.Value.Image),"Saved complete coast changes its native bank fragments.");
        Require(storedCovers.Count==covers.Count&&covers.All(p=>storedCovers.TryGetValue(p.Key,out var c)
            &&c.Cell.ForegroundImages.SequenceEqual(p.Value.Select(f=>new Front(checked((short)f.Library),checked((short)f.Image))))),
            "Saved coast changes its original ordered cover fragments.");
        Require(cells.All(c=>c.X>=0&&c.Y>=0&&c.X<width&&c.Y<height&&c.Cell.BackLibrary==0
            &&((c.Cell.BackImage&0x1fffffff)==0||c.X%2==0&&c.Y%2==0&&ground.Contains((c.Cell.BackLibrary,c.Cell.BackImage&0x1fffffff)))
            &&((c.Cell.BackImage&0x1fffffff) is 0 or >=1 and <=5 or >=1801 and <=1805||native.Ground.GetValueOrDefault((c.X,c.Y))==(c.Cell.BackLibrary,c.Cell.BackImage&0x1fffffff))
            &&c.Cell.Door==0&&c.Cell.DoorOffset==0&&c.Cell.MiddleFrames==0&&c.Cell.MiddleTick==0
            &&c.Cell.TileFrames==0&&c.Cell.TileAnimation==0&&c.Cell.TileOffset==0&&c.Cell.Light==0),"Complete coast contains unsupported cells or metadata.");
        string name=Text(proof,"name");Require(name.Length is >0 and <=512,"Invalid complete coast name.");
        int branches=proof.TryGetProperty("branchCount",out var count)?count.GetInt32():0;
        Require(branches is >=0 and <=3,"Invalid saved coast branch count.");
        TerrainWaterMode mode=proof.TryGetProperty("waterMode",out var waterMode)?waterMode.GetString() switch
        {
            "river-mouth"=>TerrainWaterMode.RiverToSea,"beach"=>TerrainWaterMode.Beach,_=>throw new InvalidDataException("Invalid saved coast water mode.")
        }:branches>0?TerrainWaterMode.Waterways:TerrainWaterMode.River;
        Require(edges.Length<2||mode is TerrainWaterMode.RiverToSea or TerrainWaterMode.Beach,"Two-edge coast needs its own water mode.");
        if(mode==TerrainWaterMode.RiverToSea)
        {
            var witness=proof.GetProperty("nativeMouthWitness");
            Require(branches==0&&edges.Length==2&&witness.GetProperty("inlandDistanceFromSeaEdges").GetInt32()>=20
                &&witness.GetProperty("seaWaterRadius").GetDouble()>=10
                &&witness.GetProperty("seaWaterRadius").GetDouble()>=witness.GetProperty("inlandWaterRadius").GetDouble()*2
                &&proof.GetProperty("waterTopology").GetProperty("mainWaterFraction").GetDouble()>.995,"Saved river mouth needs verified inland-to-sea water evidence.");
        }
        if (branches>0)
        {
            var topology=proof.GetProperty("waterTopology");
            Require(topology.GetProperty("mainWaterFraction").GetDouble()>.995&&topology.GetProperty("sustainedEnds").GetArrayLength()>=branches+2
                &&topology.GetProperty("junctionSamples").GetArrayLength()>0,"Saved tributary needs original-pixel water topology evidence.");
        }
        foreach(string boundary in edges)Require(cells.Count(c=>(c.Cell.BackImage&0x1fffffff) is >=1801 and <=1805&&(boundary switch{"north"=>c.Y==0,"west"=>c.X==0,"east"=>c.X==width-2,"south"=>c.Y==height-2,_=>false}))>=4,
            "Saved inlet has no substantial water at its declared entry edge.");
        return new(Path.GetFileNameWithoutExtension(path),name,cells) { Width=width,Height=height,BranchCount=branches,EntryEdge=edge,HasNativeBridge=native.Front.Count>0,BoundaryEdges=edges,WaterMode=mode };
    }

    private static void ValidateBoundarySource(string path,JsonElement proof,IReadOnlyList<StampCell> cells,int width,int height,
        RiverArtwork artwork,AssetStore assets,bool allowResearch,CancellationToken cancellation)
    {
        var boundary=proof.GetProperty("boundarySource");string file=Text(boundary,"file");
        Require(file.Length is >2 and <=82&&file==Path.GetFileName(file)&&file.EndsWith(".X",StringComparison.Ordinal)
            &&file[..^2].All(c=>char.IsAsciiLetterOrDigit(c)||c=='-')&&file!=Path.GetFileName(path),"Invalid inlet source path.");
        string sourcePath=Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path))!,file);
        Require(Hash(sourcePath)==Text(boundary,"sha256"),"Saved inlet source changed.");
        using(var sourceProof=JsonDocument.Parse(File.ReadAllText(Path.ChangeExtension(sourcePath,"json"))))
            Require(!sourceProof.RootElement.TryGetProperty("entryEdge",out _)&&!sourceProof.RootElement.TryGetProperty("boundarySource",out _),
                "Saved inlet must reference a complete inland source, without recursive cuts.");
        var source=LoadComplete(sourcePath,artwork,assets,allowResearch,cancellation);
        int cropX=Number(boundary,"cropX"),cropY=Number(boundary,"cropY"),cropRight=boundary.TryGetProperty("cropRight",out var rightCrop)?rightCrop.GetInt32():0,
            cropBottom=boundary.TryGetProperty("cropBottom",out var bottomCrop)?bottomCrop.GetInt32():0;
        Require(cropX>=0&&cropY>=0&&cropRight>=0&&cropBottom>=0&&((cropX|cropY|cropRight|cropBottom)&1)==0&&cropX+cropY+cropRight+cropBottom>0
            &&source.Width==Number(boundary,"width")&&source.Height==Number(boundary,"height")
            &&width==source.Width-cropX-cropRight&&height==source.Height-cropY-cropBottom,"Invalid inlet boundary dimensions.");
        bool Retained(StampCell cell)=>cell.X>=cropX&&cell.Y>=cropY&&cell.X<cropX+width&&cell.Y<cropY+height;
        var retained=source.Cells.Where(Retained).ToDictionary(c=>(c.X-cropX,c.Y-cropY),c=>c.Cell);
        Require(retained.Count==cells.Count&&cells.All(c=>retained.TryGetValue((c.X,c.Y),out var original)&&Same(c.Cell,original)),
            "Saved inlet must retain the exact original source cells, flags and whole covers.");
        foreach(var cell in source.Cells.Where(c=>!Retained(c)))
        {
            cancellation.ThrowIfCancellationRequested();
            if(cell.Cell.MiddleImage!=0)Outside(cell.X,cell.Y,cell.Cell.MiddleLibrary,cell.Cell.MiddleImage);
            foreach(var front in cell.Cell.ForegroundImages)Outside(cell.X,cell.Y,front.Library,front.Image&0x7fff);
        }
        var pixelProof=proof.GetProperty("boundaryPixelProof");
        Require(Number(pixelProof,"changedPixels")==0&&Number(pixelProof,"removedFragmentsDrawingInside")==0,
            "Saved inlet boundary pixels were not preserved.");
        void Outside(int x,int y,int library,int image)
        {
            var sprite=artwork.Sprites[(library,image)];bool floor=(sprite.Width,sprite.Height) is (48,32) or (96,64);
            int right=x*48+sprite.Width,bottom=floor?y*32+sprite.Height:(y+1)*32;
            int left=x*48,top=floor?y*32:(y+1)*32-sprite.Height;
            Require(right<=cropX*48||bottom<=cropY*32||left>=(cropX+width)*48||top>=(cropY+height)*32,"Removed inlet fragment would draw inside the map boundary.");
        }
    }

    private static bool Same(Cell a,Cell b)=>a.BackLibrary==b.BackLibrary&&a.BackImage==b.BackImage&&a.MiddleLibrary==b.MiddleLibrary&&a.MiddleImage==b.MiddleImage
        &&(a.Foreground == b.Foreground)&&a.Door==b.Door&&a.DoorOffset==b.DoorOffset&&a.MiddleFrames==b.MiddleFrames&&a.MiddleTick==b.MiddleTick
        &&a.TileFrames==b.TileFrames&&a.TileAnimation==b.TileAnimation&&a.TileOffset==b.TileOffset&&a.Light==b.Light;

    public static RiverCoastLayoutCatalogue Load(string path,RiverArtwork artwork,bool allowResearch=false,CancellationToken cancellation=default)
    {
        cancellation.ThrowIfCancellationRequested();
        if(new FileInfo(path).Length>8*1024*1024)throw new InvalidDataException("Coast catalogue is too large.");
        using var json=JsonDocument.Parse(File.ReadAllText(path));var root=json.RootElement;
        string status=Text(root,"status");
        Require(Number(root,"schemaVersion")==1&&(status=="reviewed"||allowResearch&&status.StartsWith("research",StringComparison.Ordinal)),"Unreviewed coast layouts.");
        var proofs=artwork.Catalogue.Sprites.ToDictionary(p=>(p.Library,p.Image));
        foreach(var proof in root.GetProperty("sprites").EnumerateArray())
            Require(proofs.TryGetValue((Number(proof,"library"),Number(proof,"image")),out var native)
                &&native.Width==Number(proof,"width")&&native.Height==Number(proof,"height")&&native.PixelSha256==Text(proof,"pixelSha256"),"Coast sprite proof differs from the original artwork.");
        var originals=artwork.Catalogue.Pieces.ToDictionary(p=>p.Id);var layouts=new List<RiverCoastLayout>();
        string directory=Path.GetDirectoryName(Path.GetFullPath(path))!;
        foreach(var layout in root.GetProperty("layouts").EnumerateArray())
        {
            cancellation.ThrowIfCancellationRequested();string id=Text(layout,"id"),name=Text(layout,"name"),file=Text(layout,"file");
            Require(layouts.Count<100&&id.Length is >0 and <80&&id.All(c=>char.IsAsciiLetterOrDigit(c)||c=='-')
                &&name.Length<=512&&!layouts.Any(l=>l.Id==id),"Invalid coast layout id or name.");
            Require(file==Path.GetFileName(file)&&Path.GetExtension(file).Equals(".x",StringComparison.OrdinalIgnoreCase),"Invalid coast object path.");
            string objectPath=Path.Combine(directory,file);
            Require(new FileInfo(objectPath).Length<=1024*1024&&Hash(objectPath)==Text(layout,"sha256"),"Saved coast object checksum changed.");
            var cells=CellCodec.LoadStamp(objectPath);
            Require(cells.Count is >0 and <=1000&&cells.Count==Number(layout,"cellCount")&&cells.Select(c=>(c.X,c.Y)).Distinct().Count()==cells.Count,"Invalid coast object cells.");
            var bankPlacements=layout.GetProperty("bankPlacements").EnumerateArray().ToArray();
            Require(bankPlacements.Length==2,"Coast join needs two original bank sections.");
            var first=Native(bankPlacements[0],"bank");var second=Native(bankPlacements[1],"bank");
            int dx=Number(bankPlacements[1],"x")-Number(bankPlacements[0],"x"),dy=Number(bankPlacements[1],"y")-Number(bankPlacements[0],"y");
            Require(artwork.Catalogue.Joins.Any(j=>j.FromId==first.Id&&j.ToId==second.Id&&j.Dx==dx&&j.Dy==dy
                ||j.FromId==second.Id&&j.ToId==first.Id&&j.Dx==-dx&&j.Dy==-dy),"Coast join has no native-map evidence.");
            var banks=new Dictionary<(int,int),RiverArtworkCommand>();var covers=new Dictionary<(int,int),RiverArtworkCommand>();
            foreach(var placement in bankPlacements)Add(banks,Native(placement,"bank"),placement);
            var coverPlacements=layout.GetProperty("coverPlacements").EnumerateArray().ToArray();
            Require(coverPlacements.Length<=10,"Too many coast seam covers.");
            if(coverPlacements.Length!=0)
            {
                var seam=layout.GetProperty("seamPixels");
                Require(Number(seam,"width") is >0 and <=200&&Number(seam,"height") is >0 and <=200
                    &&Number(layout,"reviewedSeamPixels")==Number(seam,"width")*Number(seam,"height")
                    &&Number(layout,"reviewedSeamUncoveredPixels")==0,"Coast cover needs a reviewed, fully covered seam.");
            }
            foreach(var placement in coverPlacements)
            {
                Require(Text(placement,"purpose").StartsWith("Cover reviewed native ",StringComparison.Ordinal),"Optional coast decorations are forbidden.");
                var source=Native(placement);Require(source.Role!="bank","Seam cover must be a complete native scenery object.");
                Add(covers,source,placement);
            }
            var storedBanks=cells.Where(c=>c.Cell.MiddleImage!=0).ToDictionary(c=>(c.X,c.Y));
            var storedCovers=cells.Where(c=>c.Cell.ForegroundImages.Count!=0).ToDictionary(c=>(c.X,c.Y));
            Require(storedBanks.Count==banks.Count&&banks.All(p=>storedBanks.TryGetValue(p.Key,out var c)
                &&c.Cell.MiddleLibrary==p.Value.Library&&c.Cell.MiddleImage==p.Value.Image),"Saved coast changes native bank fragments.");
            Require(storedCovers.Count==covers.Count&&covers.All(p=>storedCovers.TryGetValue(p.Key,out var c)
                &&c.Cell.ForegroundImages.Count==1&&c.Cell.ForegroundImages[0]==new Front(checked((short)p.Value.Library),checked((short)p.Value.Image))),"Saved coast cover is incomplete or contains extra scenery.");
            Require(cells.All(c=>Math.Abs((long)c.X)<=100&&Math.Abs((long)c.Y)<=100&&c.Cell.BackLibrary==0
                &&(c.Cell.BackImage&~0x60000000)==0&&c.Cell.Door==0&&c.Cell.DoorOffset==0
                &&c.Cell.MiddleFrames==0&&c.Cell.MiddleTick==0&&c.Cell.TileFrames==0&&c.Cell.TileAnimation==0&&c.Cell.TileOffset==0&&c.Cell.Light==0),"Coast layout must use ordinary static object layers and collision flags.");
            layouts.Add(new(id,name,cells));
        }
        Require(layouts.Count!=0,"Missing coast layouts.");return new(layouts);

        RiverArtworkPiece Native(JsonElement placement,string? role=null)
        {
            Require(Math.Abs((long)Number(placement,"x"))<=100&&Math.Abs((long)Number(placement,"y"))<=100,"Coast placement is outside its object bounds.");
            Require(originals.TryGetValue(Text(placement,"id"),out var source)&&source.SourceSha256==Text(placement,"sourceSha256")
                &&(role==null||source.Role==role),"Coast source object is missing or changed.");return source!;
        }
        static void Add(Dictionary<(int,int),RiverArtworkCommand> destination,RiverArtworkPiece source,JsonElement placement)
        {
            foreach(var command in source.Commands)
            {
                var c=command with {X=command.X+Number(placement,"x"),Y=command.Y+Number(placement,"y")};
                Require(!destination.TryGetValue((c.X,c.Y),out var old)||old==c,"Conflicting coast object fragments.");destination[(c.X,c.Y)]=c;
            }
        }
    }
    private static int Number(JsonElement value,string name)=>value.GetProperty(name).GetInt32();
    private static string Text(JsonElement value,string name)=>value.GetProperty(name).GetString()??throw new InvalidDataException("Missing coast layout text.");
    private static string Hash(string path)=>Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));
    private static void Require(bool value,string message){if(!value)throw new InvalidDataException(message);}
}
