using System.IO;
using System.Security.Cryptography;
using System.Text.Json;

namespace CrystalStudio;

/// <summary>Reusable saved bank pieces. This research compiler does not select complete river presets or fit scenery.</summary>
public sealed class RiverCoastPieces
{
    public sealed record Port(string BankId,int BankX,int BankY,int FreeEndpoint,double ShoreX=0,double ShoreY=0);
    public sealed record Piece(string Id,IReadOnlyList<StampCell> Cells,Port Start,Port End)
    {
        public RiverBridgeComponent? BridgeComponent {get;init;}
        // Source-validated planning classification only; never serialized or rendered.
        public IReadOnlyList<RiverArtworkCommand> NativeFrontBankCommands {get;internal init;}=[];
        public int GroundPhaseMask {get;}=GroundPhases(Cells);
        public bool GroundAligned(int x,int y)=>(GroundPhaseMask&(1<<((x&1)|((y&1)<<1))))!=0;
        private static int GroundPhases(IReadOnlyList<StampCell> cells)
        {
            int phases=15;
            foreach(var c in cells.Where(c=>(c.Cell.BackImage&0x1fffffff)!=0))phases&=1<<((c.X&1)|((c.Y&1)<<1));
            return phases;
        }
    }
    public sealed record Link(string FromId,bool FromReverse,string ToId,bool ToReverse,int Dx,int Dy);
    public sealed record Placement(Piece Piece,int X,int Y,bool Reverse)
    {
        public int BranchDepth {get;init;}
        public int BranchId {get;init;}
    }
    public sealed record Chain(IReadOnlyList<Placement> Pieces,IReadOnlyList<StampCell> Cells)
    {
        public IReadOnlyList<RiverBridgeWalkSupport> BridgeWalkSupport {get;init;}=[];
        public bool Closed {get;init;}
        public int SearchNodes {get;init;}
    }
    public IReadOnlyList<Piece> Pieces {get;}
    public IReadOnlyList<Link> Links {get;}
    public IReadOnlyDictionary<(int,int),RiverArtwork.Pixels> NativeSprites {get;}
    internal AssetSourceFormat Format => NativeSprites.Keys.Any(k => k.Item1 >= 2000) ? AssetSourceFormat.Sanxiu : AssetSourceFormat.Default;
    private readonly Dictionary<(string From,bool ReverseFrom,string To,bool ReverseTo),Link[]> connections;
    private RiverCoastPieces(IReadOnlyList<Piece> pieces,IReadOnlyList<Link> links,IReadOnlyDictionary<(int,int),RiverArtwork.Pixels> sprites)
    {
        Pieces=pieces;Links=links;NativeSprites=sprites;
        connections=links.GroupBy(l=>(l.FromId,l.FromReverse,l.ToId,l.ToReverse)).ToDictionary(g=>g.Key,g=>g.ToArray());
    }
    internal Link? FindConnection(string from,bool reverseFrom,string to,bool reverseTo)
        =>connections.TryGetValue((from,reverseFrom,to,reverseTo),out var found)?found.SingleOrDefault():null;

    public RiverCoastPieces WithBoundary(RiverBoundaryComponent edge)=>WithBoundaryPiece(edge.Piece);
    public RiverCoastPieces WithBoundary(RiverHorizontalBoundaryComponent edge)=>WithBoundaryPiece(edge.Piece);
    public RiverCoastPieces WithCap(RiverDiagonalCapComponent cap)=>WithBoundaryPiece(cap.Piece);
    public RiverCoastPieces WithReturn(RiverNativeReturnComponent join)=>WithBoundaryPiece(join.Piece);
    public RiverCoastPieces WithSouthDiagonalReturn(RiverSouthDiagonalComponent join)=>WithBoundaryPiece(join.Piece);
    public RiverCoastPieces WithNativeBankOverlay(RiverNativeBankOverlayComponent join)=>WithBoundaryPiece(join.Piece);
    public RiverCoastPieces WithForks(RiverForkComponents joins)
    {
        var result=this;foreach(var piece in joins.Pieces)result=result.WithBoundaryPiece(piece);return result;
    }
    /// <summary>Load a complete saved mandatory-cover alternative at unchanged bank ports.</summary>
    public RiverCoastPieces WithSavedCoverVariant(string originalId,string variantId,string path,
        RiverArtwork artwork,AssetStore assets,CancellationToken cancellation=default)
    {
        var original=Pieces.Single(p=>p.Id==originalId);
        var saved=RiverCoastLayoutCatalogue.LoadComplete(path,artwork,assets,allowResearch:true,cancellation:cancellation);
        var cells=saved.Cells;
        if(cells.Count is 0 or >256||cells.Any(c=>(c.Cell.BackImage&0x1fffffff)!=0
            ||c.Cell.MiddleImage!=0&&!NativeSprites.ContainsKey((c.Cell.MiddleLibrary,c.Cell.MiddleImage))
            ||c.Cell.ForegroundImages.Any(f=>!NativeSprites.ContainsKey((f.Library,f.Image&0x7fff)))))
            throw new InvalidDataException("Saved cover alternative must use a small native bank piece without ground tiles.");
        static IEnumerable<(int,int,int,int)> Banks(IReadOnlyList<StampCell> source)=>source
            .Where(c=>c.Cell.MiddleImage!=0).OrderBy(c=>c.Y).ThenBy(c=>c.X)
            .Select(c=>(c.X,c.Y,(int)c.Cell.MiddleLibrary,(int)c.Cell.MiddleImage));
        if(!Banks(original.Cells).SequenceEqual(Banks(cells)))
            throw new InvalidDataException("Saved cover alternative changes the native banks.");
        return WithBoundaryPiece(new Piece(variantId,cells,original.Start,original.End));
    }
    /// <summary>Offline candidate authoring only. Production loaders must still
    /// validate saved pieces, source fragments, covers and provenance.</summary>
    public RiverCoastPieces ResearchWithPiece(Piece piece)
    {
        if(piece.Cells.Count==0||piece.Cells.Count>256||piece.Cells.Any(c=>(c.Cell.BackImage&0x1fffffff)!=0
            ||c.Cell.MiddleImage>0&&!NativeSprites.ContainsKey((c.Cell.MiddleLibrary,c.Cell.MiddleImage))
            ||c.Cell.ForegroundImages.Any(f=>!NativeSprites.ContainsKey((f.Library,f.Image&0x7fff)))))
            throw new InvalidDataException("Research join must be a small existing-library bank piece.");
        foreach(var port in new[]{piece.Start,piece.End})
            if(!Pieces.SelectMany(p=>new[]{p.Start,p.End}).Any(p=>p.BankId==port.BankId&&p.FreeEndpoint==port.FreeEndpoint))
                throw new InvalidDataException("Research join has an unknown native bank port.");
        return WithBoundaryPiece(piece);
    }
    /// <summary>Offline validation of a small authored insert at unchanged bank ports.</summary>
    public RiverCoastPieces ResearchReplacePiece(string id,IReadOnlyList<StampCell> cells)
    {
        var original=Pieces.Single(p=>p.Id==id);
        if(cells.Count is 0 or >256||cells.Select(c=>(c.X,c.Y)).Distinct().Count()!=cells.Count
            ||cells.Any(c=>(c.Cell.BackImage&0x1fffffff)!=0
                ||c.Cell.MiddleImage!=0&&!NativeSprites.ContainsKey((c.Cell.MiddleLibrary,c.Cell.MiddleImage))
                ||c.Cell.ForegroundImages.Any(f=>!NativeSprites.ContainsKey((f.Library,f.Image&0x7fff)))))
            throw new InvalidDataException("Research replacement must be a small existing-library join without ground tiles.");
        var replacement=new Piece(id,cells,original.Start,original.End);
        return new(Pieces.Select(p=>p==original?replacement:p).ToArray(),Links,NativeSprites);
    }
    private RiverCoastPieces WithBoundaryPiece(Piece edge)
    {
        if(Pieces.Any(p=>p.Id==edge.Id))throw new InvalidDataException("Boundary component is already loaded.");
        var pieces=Pieces.Append(edge).ToArray();var links=Links.ToList();
        foreach(var from in pieces)foreach(bool reverseFrom in new[]{false,true})
        foreach(var to in pieces)foreach(bool reverseTo in new[]{false,true})
        {
            if(from!=edge&&to!=edge)continue;
            var end=reverseFrom?from.Start:from.End;var start=reverseTo?to.End:to.Start;
            if(end.BankId==start.BankId&&end.FreeEndpoint!=start.FreeEndpoint)
            {
                var link=new Link(from.Id,reverseFrom,to.Id,reverseTo,end.BankX-start.BankX,end.BankY-start.BankY);
                if(!links.Contains(link))links.Add(link);
            }
        }
        return new(pieces,links,NativeSprites);
    }

    public RiverCoastPieces WithBridge(RiverBridgeComponent bridge)
    {
        if(Pieces.Any(p=>p.BridgeComponent!=null))throw new InvalidDataException("Bridge component is already loaded.");
        var pieces=Pieces.Concat(new[]{bridge.EastShore,bridge.WestShore}).ToArray();var links=Links.ToList();
        foreach(var from in pieces)foreach(bool reverseFrom in new[]{false,true})
        foreach(var to in pieces)foreach(bool reverseTo in new[]{false,true})
        {
            if(from.BridgeComponent==null&&to.BridgeComponent==null)continue;
            var end=reverseFrom?from.Start:from.End;var start=reverseTo?to.End:to.Start;
            if(end.BankId==start.BankId&&end.FreeEndpoint!=start.FreeEndpoint)
            {var link=new Link(from.Id,reverseFrom,to.Id,reverseTo,end.BankX-start.BankX,end.BankY-start.BankY);if(!links.Contains(link))links.Add(link);}
        }
        return new(pieces,links,bridge.NativeSprites);
    }

    public static RiverCoastPieces Load(AssetStore assets,CancellationToken cancellation=default,string? sourceDirectory=null,string? artworkCataloguePath=null)
    {
        string directory=sourceDirectory??DataPaths.ResolveEditor("catalogue","river-coast-layouts");
        string nativeCatalogue=Path.Combine(directory,"catalogue.json");
        var artwork=RiverArtworkCatalogue.Load(artworkCataloguePath??DataPaths.ResolveEditor("catalogue","river-artwork.json"),true).PrepareSavedLayouts(assets,cancellation);
        var saved=RiverCoastLayoutCatalogue.Load(nativeCatalogue,artwork,true,cancellation).Layouts.ToDictionary(p=>p.Id);
        using var ports=JsonDocument.Parse(File.ReadAllText(Path.Combine(directory,"piece-ports.json")));
        var root=ports.RootElement;
        Require(root.GetProperty("schemaVersion").GetInt32()==1&&root.GetProperty("savedUnit").GetString()=="bank/join piece; never a complete river"
            &&root.GetProperty("sourceCatalogueSha256").GetString()==Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(nativeCatalogue))),
            "Reusable coast-piece index does not match its native artwork catalogue.");
        using var sources=JsonDocument.Parse(File.ReadAllText(nativeCatalogue));
        var sourcePieces=sources.RootElement.GetProperty("layouts").EnumerateArray().ToDictionary(p=>p.GetProperty("id").GetString()!);
        var coveredIds=new HashSet<string>();var groundedIds=new HashSet<string>();
        if(root.TryGetProperty("sourceModuleCatalogueFile",out var moduleFile))
        {
            string file=moduleFile.GetString()??"";
            Require(file==Path.GetFileName(file)&&file.EndsWith(".json",StringComparison.Ordinal),"Invalid covered-join index path.");
            string index=Path.Combine(directory,file);
            Require(root.GetProperty("sourceModuleCatalogueSha256").GetString()==Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(index))),
                "Covered-join index changed after port verification.");
            using var modules=JsonDocument.Parse(File.ReadAllText(index));var moduleRoot=modules.RootElement;
            Require(moduleRoot.GetProperty("schemaVersion").GetInt32()==1&&moduleRoot.GetProperty("savedUnit").GetString()=="bank/join piece; never a complete river",
                "Only reusable covered bank pieces may enter the graph.");
            foreach(var entry in moduleRoot.GetProperty("modules").EnumerateArray())
            {
                cancellation.ThrowIfCancellationRequested();string id=entry.GetProperty("id").GetString()??"",objectFile=entry.GetProperty("file").GetString()??"";
                Require(id.Length is >0 and <=80&&id.All(c=>char.IsAsciiLetterOrDigit(c)||c=='-')&&objectFile==id+".X"
                    &&entry.GetProperty("proof").GetString()==id+".json"&&!saved.ContainsKey(id),"Invalid or repeated covered join.");
                string path=Path.Combine(directory,objectFile);using var proof=JsonDocument.Parse(File.ReadAllText(Path.ChangeExtension(path,"json")));
                var value=proof.RootElement;
                Require(value.GetProperty("bankPlacements").GetArrayLength()==2&&value.GetProperty("coverPlacements").GetArrayLength()==entry.GetProperty("requiredCovers").GetInt32()
                    &&(entry.GetProperty("requiredCovers").GetInt32()>0||value.TryGetProperty("nativeBankGround",out _))
                    &&value.GetProperty("sha256").GetString()==entry.GetProperty("sha256").GetString(),
                    "Covered piece differs from its original banks and required covers.");
                var piece=RiverCoastLayoutCatalogue.LoadComplete(path,artwork,assets,true,cancellation);
                Require(piece.Width==entry.GetProperty("width").GetInt32()&&piece.Height==entry.GetProperty("height").GetInt32(),"Covered join dimensions changed.");
                saved.Add(id,piece);sourcePieces.Add(id,value.Clone());coveredIds.Add(id);
                if(value.TryGetProperty("nativeBankGround",out _))groundedIds.Add(id);
            }
        }
        var pieces=new List<Piece>();
        foreach(var item in root.GetProperty("pieces").EnumerateArray())
        {
            cancellation.ThrowIfCancellationRequested();string id=item.GetProperty("id").GetString()!;
            Require(saved.TryGetValue(id,out var original)&&!pieces.Any(p=>p.Id==id),"Missing or repeated reusable coast piece.");
            var source=sourcePieces[id];
            if(item.TryGetProperty("kind",out var kind))Require(kind.GetString()==(groundedIds.Contains(id)?"native-ground-module":coveredIds.Contains(id)?"covered-module":"native-join"),"Port index changes the saved piece kind.");
            Require(item.GetProperty("file").GetString()==source.GetProperty("file").GetString()
                &&item.GetProperty("sha256").GetString()==source.GetProperty("sha256").GetString(),"Reusable piece changes its saved .X file.");
            var banks=source.GetProperty("bankPlacements").EnumerateArray().ToArray();
            var start=ReadPort(item.GetProperty("start"),banks[0]);var end=ReadPort(item.GetProperty("end"),banks[1]);
            var piece=new Piece(id,original!.Cells,start,end);
            Require(piece.GroundPhaseMask!=0&&(!item.TryGetProperty("groundPhaseMask",out var phases)||phases.GetInt32()==piece.GroundPhaseMask),
                "Reusable piece index changes its original ground placement phase.");
            pieces.Add(piece);
        }
        Require(pieces.Count==saved.Count,"Reusable piece index must include every reviewed bank piece.");
        var byId=pieces.ToDictionary(p=>p.Id);var links=new List<Link>();
        foreach(var item in root.GetProperty("connections").EnumerateArray())
        {
            var link=new Link(item.GetProperty("fromId").GetString()!,item.GetProperty("fromReverse").GetBoolean(),
                item.GetProperty("toId").GetString()!,item.GetProperty("toReverse").GetBoolean(),item.GetProperty("dx").GetInt32(),item.GetProperty("dy").GetInt32());
            Require(byId.ContainsKey(link.FromId)&&byId.ContainsKey(link.ToId)&&!links.Contains(link),"Invalid reusable bank connection.");
            var a=byId[link.FromId];var b=byId[link.ToId];var end=link.FromReverse?a.Start:a.End;var start=link.ToReverse?b.End:b.Start;
            Require(end.BankId==start.BankId&&end.FreeEndpoint!=start.FreeEndpoint&&link.Dx==end.BankX-start.BankX&&link.Dy==end.BankY-start.BankY,
                "Reusable pieces must overlap an identical original bank at opposite open ends.");
            links.Add(link);
        }
        Require(links.Count>0,"Missing reusable bank connections.");
        var shorePoints=new Dictionary<(string,int),(double,double)>();
        foreach(var port in pieces.SelectMany(p=>new[]{p.Start,p.End}))
        {
            var key=(port.BankId,port.FreeEndpoint);var relative=(port.ShoreX-port.BankX,port.ShoreY-port.BankY);
            Require(!shorePoints.TryGetValue(key,out var old)||Math.Abs(old.Item1-relative.Item1)<1e-8&&Math.Abs(old.Item2-relative.Item2)<1e-8,
                "Reusable ports disagree about the same native shore endpoint.");
            shorePoints[key]=relative;
        }
        return new(pieces,links,artwork.Sprites);
        static Port ReadPort(JsonElement p,JsonElement bank)
        {
            var value=new Port(p.GetProperty("bankId").GetString()!,p.GetProperty("bankX").GetInt32(),p.GetProperty("bankY").GetInt32(),p.GetProperty("freeEndpoint").GetInt32(),
                p.GetProperty("shoreX").GetDouble(),p.GetProperty("shoreY").GetDouble());
            Require(value.BankId==bank.GetProperty("id").GetString()&&value.BankX==bank.GetProperty("x").GetInt32()
                &&value.BankY==bank.GetProperty("y").GetInt32()&&value.FreeEndpoint is 0 or 1
                &&double.IsFinite(value.ShoreX)&&double.IsFinite(value.ShoreY)&&Math.Abs(value.ShoreX)<=1024&&Math.Abs(value.ShoreY)<=1024,
                "Reusable coast port changes its original bank anchor or has invalid shore coordinates.");return value;
        }
    }

    /// <summary>Prototype open-bank chaining. A complete river still needs paired banks, ends and verified fork/corner modules.</summary>
    public Chain ResearchChain(int seed,int maximumPieces,CancellationToken cancellation=default)
    {
        cancellation.ThrowIfCancellationRequested();
        if(maximumPieces is <1 or >1000)throw new ArgumentOutOfRangeException(nameof(maximumPieces));
        var random=new Random(seed);var byId=Pieces.ToDictionary(p=>p.Id);var placed=new List<Placement>();var cells=new Dictionary<(int,int),Cell>();
        var first=Pieces[random.Next(Pieces.Count)];var current=new Placement(first,0,0,random.Next(2)==0);
        Add(current,cells);placed.Add(current);
        for(int step=1;step<maximumPieces;step++)
        {
            cancellation.ThrowIfCancellationRequested();bool found=false;
            foreach(var link in Links.Where(l=>l.FromId==current.Piece.Id&&l.FromReverse==current.Reverse).OrderBy(_=>random.Next()))
            {
                var next=new Placement(byId[link.ToId],current.X+link.Dx,current.Y+link.Dy,link.ToReverse);
                if(placed.Any(p=>p.Piece.Id==next.Piece.Id&&p.X==next.X&&p.Y==next.Y))continue;
                var trial=cells.ToDictionary(p=>p.Key,p=>p.Value.Copy());
                if(!Add(next,trial))continue;
                current=next;cells=trial;placed.Add(next);found=true;break;
            }
            if(!found)break;
        }
        return new(placed,cells.OrderBy(p=>p.Key.Item2).ThenBy(p=>p.Key.Item1).Select(p=>new StampCell(p.Key.Item1,p.Key.Item2,p.Value)).ToArray());
    }

    /// <summary>Research closed-bank outline search; ground/water and complete river checks are still required.</summary>
    public Chain? ResearchAssemble(IReadOnlyList<Placement> placements,out string failure)
    {
        failure="";var cells=new Dictionary<(int,int),Cell>();
        if(placements.Count==0){failure="No reusable pieces were requested.";return null;}
        for(int i=0;i<placements.Count;i++)
        {
            var next=placements[i];
            if(!Pieces.Contains(next.Piece)){failure=$"Piece {i} is not from this verified catalogue.";return null;}
            if(i>0)
            {
                var previous=placements[i-1];
                if(!connections.TryGetValue((previous.Piece.Id,previous.Reverse,next.Piece.Id,next.Reverse),out var links)||
                    !links.Any(l=>l.Dx==next.X-previous.X&&l.Dy==next.Y-previous.Y))
                {failure=$"Piece {i} has no compatible original bank connection.";return null;}
            }
            if(!Add(next,cells,out string conflict))
            {
                failure=$"Piece {i} ({next.Piece.Id}): {conflict}";
                foreach(var saved in next.Piece.Cells)
                {
                    var key=(saved.X+next.X,saved.Y+next.Y);
                    if(!cells.TryGetValue(key,out var old)||old.MiddleImage==0||saved.Cell.MiddleImage==0
                        ||old.MiddleLibrary==saved.Cell.MiddleLibrary&&old.MiddleImage==saved.Cell.MiddleImage)continue;
                    for(int prior=i-1;prior>=0;prior--)
                    {
                        var p=placements[prior];var owner=p.Piece.Cells.FirstOrDefault(c=>c.X+p.X==key.Item1&&c.Y+p.Y==key.Item2
                            &&c.Cell.MiddleLibrary==old.MiddleLibrary&&c.Cell.MiddleImage==old.MiddleImage);
                        if(owner==null)continue;
                        failure+=$" Existing anchor comes from piece {prior} ({p.Piece.Id}) at ({p.X},{p.Y}), local ({owner.X},{owner.Y}); new origin ({next.X},{next.Y}), local ({saved.X},{saved.Y}).";
                        break;
                    }
                    break;
                }
                return null;
            }
        }
        foreach(var group in placements.Where(p=>p.Piece.BridgeComponent!=null).GroupBy(p=>(p.Piece.BridgeComponent!,p.X,p.Y)))
            if(!group.Any(p=>p.Piece==group.Key.Item1.EastShore)||!group.Any(p=>p.Piece==group.Key.Item1.WestShore))
            {failure="A native bridge requires both bank paths at the same position.";return null;}
        var first=placements[0];var last=placements[^1];var start=first.Reverse?first.Piece.End:first.Piece.Start;var end=last.Reverse?last.Piece.Start:last.Piece.End;
        return new(placements.ToArray(),cells.OrderBy(p=>p.Key.Item2).ThenBy(p=>p.Key.Item1).Select(p=>new StampCell(p.Key.Item1,p.Key.Item2,p.Value)).ToArray())
        {BridgeWalkSupport=placements.Where(p=>p.Piece.BridgeComponent!=null).GroupBy(p=>(p.Piece.BridgeComponent!,p.X,p.Y))
            .Select(g=>g.Key.Item1.WalkingAt(g.Key.X,g.Key.Y)).ToArray(),Closed=start.BankId==end.BankId&&start.FreeEndpoint!=end.FreeEndpoint&&first.X+start.BankX==last.X+end.BankX&&first.Y+start.BankY==last.Y+end.BankY};
    }

    /// <summary>Research closed-bank outline search; ground/water and complete river checks are still required.</summary>
    public Chain? ResearchLoop(int seed,int minimumPieces,int maximumPieces,int maximumWidth,int maximumHeight,int nodeBudget=10000,CancellationToken cancellation=default)
    {
        if(minimumPieces<3||maximumPieces<minimumPieces||maximumPieces>200||maximumWidth<32||maximumHeight<32||nodeBudget<1)
            throw new ArgumentOutOfRangeException(nameof(minimumPieces));
        cancellation.ThrowIfCancellationRequested();var random=new Random(seed);var byId=Pieces.ToDictionary(p=>p.Id);
        var outgoing=Links.GroupBy(l=>(l.FromId,l.FromReverse)).ToDictionary(g=>g.Key,g=>g.ToArray());
        int nodes=0;
        foreach(var first in Pieces.SelectMany(p=>new[]{new Placement(p,0,0,false),new Placement(p,0,0,true)}).OrderBy(_=>random.Next()))
        {
            if(nodes>=nodeBudget)break;
            int firstNodes=nodes;
            var source=Start(first);var placed=new List<Placement>{first};var cells=new Dictionary<(int,int),Cell>();
            if(!Add(first,cells))continue;
            var seenBanks=new HashSet<(string,int,int)>{(source.BankId,source.BankX,source.BankY)};
            var firstEnd=End(first);seenBanks.Add((firstEnd.BankId,firstEnd.BankX,firstEnd.BankY));
            var closing=ClosingDistances(source,Math.Min(40,maximumPieces),160000);
            var answer=Search(first,cells);if(answer!=null)return answer;
            Chain? Search(Placement current,Dictionary<(int,int),Cell> existing)
            {
                if(++nodes>nodeBudget||nodes-firstNodes>Math.Max(250,nodeBudget/12))return null;cancellation.ThrowIfCancellationRequested();
                if(placed.Count>=maximumPieces||!outgoing.TryGetValue((current.Piece.Id,current.Reverse),out var choices))return null;
                var ordered=choices.Select(link=>
                {
                    var next=new Placement(byId[link.ToId],current.X+link.Dx,current.Y+link.Dy,link.ToReverse);var end=End(next);
                    double distance=Math.Abs(next.X+end.BankX-source.BankX)+Math.Abs(next.Y+end.BankY-source.BankY);
                    // Grow initially, then bias toward closing the original open bank.
                    int steps=closing.GetValueOrDefault((end.BankId,end.FreeEndpoint,next.X+end.BankX,next.Y+end.BankY),100);
                    return (Next:next,Score:random.NextDouble()*16+(placed.Count>=minimumPieces?steps*5+distance*.05:0));
                }).OrderBy(p=>p.Score);
                foreach(var (next,_) in ordered)
                {
                    if(nodes>=nodeBudget||nodes-firstNodes>Math.Max(250,nodeBudget/12))return null;var end=End(next);var point=(end.BankId,next.X+end.BankX,next.Y+end.BankY);
                    bool close=point==(source.BankId,source.BankX,source.BankY)&&end.FreeEndpoint!=source.FreeEndpoint;
                    if(close&&placed.Count+1<minimumPieces||!close&&seenBanks.Contains(point))continue;
                    if(closing.TryGetValue((end.BankId,end.FreeEndpoint,point.Item2,point.Item3),out int remaining)&&placed.Count+1+remaining>maximumPieces)continue;
                    var merged=existing.ToDictionary(p=>p.Key,p=>p.Value.Copy());if(!Add(next,merged))continue;
                    if(merged.Keys.Max(p=>p.Item1)-merged.Keys.Min(p=>p.Item1)+1>maximumWidth
                        ||merged.Keys.Max(p=>p.Item2)-merged.Keys.Min(p=>p.Item2)+1>maximumHeight)continue;
                    placed.Add(next);
                    if(close)return new(placed.ToArray(),merged.OrderBy(p=>p.Key.Item2).ThenBy(p=>p.Key.Item1).Select(p=>new StampCell(p.Key.Item1,p.Key.Item2,p.Value)).ToArray()){Closed=true,SearchNodes=nodes};
                    seenBanks.Add(point);var answer=Search(next,merged);if(answer!=null)return answer;
                    seenBanks.Remove(point);placed.RemoveAt(placed.Count-1);
                }
                return null;
            }
        }
        return null;
        static Port Start(Placement p)=>p.Reverse?p.Piece.End:p.Piece.Start;
        static Port End(Placement p)=>p.Reverse?p.Piece.Start:p.Piece.End;
        Dictionary<(string,int,int,int),int> ClosingDistances(Port goal,int maximumDepth,int budget)
        {
            var views=Pieces.SelectMany(p=>new[]{new Placement(p,0,0,false),new Placement(p,0,0,true)}).ToArray();
            var incoming=views.GroupBy(p=>(End(p).BankId,End(p).FreeEndpoint)).ToDictionary(g=>g.Key,g=>g.ToArray());
            var result=new Dictionary<(string,int,int,int),int>();var queue=new Queue<(string,int,int,int)>();
            var target=(goal.BankId,1-goal.FreeEndpoint,goal.BankX,goal.BankY);result[target]=0;queue.Enqueue(target);
            while(queue.TryDequeue(out var at)&&result.Count<budget)
            {
                int depth=result[at];if(depth>=maximumDepth||!incoming.TryGetValue((at.Item1,at.Item2),out var choices))continue;
                foreach(var view in choices)
                {
                    var end=End(view);var start=Start(view);int x=at.Item3-end.BankX,y=at.Item4-end.BankY;
                    if(!view.Piece.GroundAligned(x,y))continue;
                    var previous=(start.BankId,1-start.FreeEndpoint,x+start.BankX,y+start.BankY);
                    if(Math.Abs(previous.Item3-goal.BankX)>maximumWidth||Math.Abs(previous.Item4-goal.BankY)>maximumHeight||result.ContainsKey(previous))continue;
                    result[previous]=depth+1;queue.Enqueue(previous);
                }
            }
            return result;
        }
    }

    private static bool Add(Placement placement,Dictionary<(int,int),Cell> output)
        =>Add(placement,output,out _);

    private static bool Add(Placement placement,Dictionary<(int,int),Cell> output,out string failure)
    {
        failure="";
        // Native 2x2 ground anchors must stay aligned after joining modules.
        // Traversal reversal changes connectivity only; it never transforms cells.
        if(!placement.Piece.GroundAligned(placement.X,placement.Y))
        {failure=$"Native ground anchors do not align at ({placement.X},{placement.Y}); allowed phase mask {placement.Piece.GroundPhaseMask}.";return false;}
        foreach(var saved in placement.Piece.Cells)
        {
            var key=(saved.X+placement.X,saved.Y+placement.Y);var cell=saved.Cell;
            if(!output.TryGetValue(key,out var old)){output[key]=cell.Copy();continue;}
            if(old.MiddleImage!=0&&cell.MiddleImage!=0&&(old.MiddleLibrary!=cell.MiddleLibrary||old.MiddleImage!=cell.MiddleImage))
            {failure=$"Middle references conflict at {key}: {old.MiddleLibrary}:{old.MiddleImage} versus {cell.MiddleLibrary}:{cell.MiddleImage}.";return false;}
            if(old.ForegroundImages.Count>0&&cell.ForegroundImages.Count>0&&(old.Foreground != cell.Foreground))
            {failure=$"Saved Front lists conflict at {key}.";return false;}
            if((old.BackImage&0x1fffffff)!=0&&(cell.BackImage&0x1fffffff)!=0&&((old.BackImage&0x1fffffff)!=(cell.BackImage&0x1fffffff)||old.BackLibrary!=cell.BackLibrary))
            {failure=$"Ground references conflict at {key}: {old.BackLibrary}:{old.BackImage&0x1fffffff} versus {cell.BackLibrary}:{cell.BackImage&0x1fffffff}.";return false;}
            if(cell.MiddleImage!=0){old.MiddleLibrary=cell.MiddleLibrary;old.MiddleImage=cell.MiddleImage;}
            if(cell.ForegroundImages.Count>0)old.Foreground = cell.Foreground;
            if((cell.BackImage&0x1fffffff)!=0){old.BackLibrary=cell.BackLibrary;old.BackImage=(old.BackImage&0x60000000)|cell.BackImage;}
            else old.BackImage|=cell.BackImage&0x60000000;
        }
        return true;
    }
    private static void Require(bool value,string message){if(!value)throw new InvalidDataException(message);}
}
