using System.IO;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;

namespace CrystalStudio;

/// <summary>Derives ordinary ground/collision cells around immutable saved bank
/// pieces. Native pixels are read only to classify concealed/exposed ground;
/// this does not draw the editor, transform artwork or fit seam covers.</summary>
public static class RiverPieceGroundAssembler
{
    // Match the province generator's one-million-cell map limit. Coast
    // analysis uses 12 × 8 samples per cell, including open sea and land.
    public const int MaximumCoastCells = 1_000_000;
    public sealed record Bounds(int X,int Y,int Width,int Height);
    public sealed record Result(int X,int Y,int Width,int Height,IReadOnlyList<StampCell> Cells,
        int WaterGroundTiles,int ExposedMaterialConflicts,int DisconnectedLandCells)
    {
        public IReadOnlySet<(int X,int Y)> RequiredGroundAnchors { get; init; } = new HashSet<(int,int)>();
        // Planning evidence only; this is never written to map cells or used by rendering.
        public IReadOnlyList<bool>? NativeWaterSurface { get; init; }
        // Underlying channel with only the proven wooden bridge fragments omitted.
        // Cliffs, bank artwork and every other saved cover remain in this evidence.
        public IReadOnlyList<bool>? NativeWaterFlow { get; init; }
    }
    private sealed record Command(int Phase,int X,int Y,int Layer,int Order,int Library,int Image,RiverArtwork.Pixels Sprite,bool BankArtwork);
    private sealed record Run(int Start,int End,int Label);

    public static Bounds Measure(RiverCoastPieces catalogue,RiverCoastPieces.Chain chain)
    {
        if(chain.Cells.Count==0)throw new ArgumentException("Missing native piece cells.");
        int l=chain.Cells.Min(c=>c.X)*48,t=chain.Cells.Min(c=>c.Y)*32;
        int r=(chain.Cells.Max(c=>c.X)+1)*48,b=(chain.Cells.Max(c=>c.Y)+1)*32;
        foreach(var cell in chain.Cells)
        {
            if(cell.Cell.MiddleImage>0)Include(cell.X,cell.Y,cell.Cell.MiddleLibrary,cell.Cell.MiddleImage);
            foreach(var front in cell.Cell.ForegroundImages)Include(cell.X,cell.Y,front.Library,front.Image&0x7fff);
        }
        void Include(int x,int y,int library,int image)
        {
            var sprite=catalogue.NativeSprites[(library,image)];int px=x*48,py=y*32+(RiverArtwork.IsFloor(sprite)?0:32-sprite.Height);
            l=Math.Min(l,px);t=Math.Min(t,py);r=Math.Max(r,px+sprite.Width);b=Math.Max(b,py+sprite.Height);
        }
        int left=(int)Math.Floor(l/96.0)*2-4,top=(int)Math.Floor(t/64.0)*2-4;
        int right=(int)Math.Ceiling(r/96.0)*2+4,bottom=(int)Math.Ceiling(b/64.0)*2+4;
        return new(left,top,right-left,bottom-top);
    }

    public static Result Assemble(RiverCoastPieces catalogue,RiverCoastPieces.Chain chain,int seed,
        CancellationToken cancellation=default,bool verifyCovers=false,bool verifyChannel=false,bool sealNativePockets=false)
        =>AssembleCore(catalogue,chain,seed,null,null,cancellation,verifyCovers,verifyChannel,sealNativePockets:sealNativePockets);

    /// <summary>Offline inspection of rejected movement geometry. Never a playable plan.
    /// Native artwork, saved covers and exposed-ground checks remain strict.</summary>
    public static Result ResearchInspect(RiverCoastPieces catalogue,RiverCoastPieces.Chain chain,int seed,
        CancellationToken cancellation=default)
        =>AssembleCore(catalogue,chain,seed,null,null,cancellation,verifyCovers:true,inspectRejectedMovement:true);

    /// <summary>Research coastal underlay: close an open native bank path along the actual
    /// map boundary. No cells are cropped, sprites transformed or covers fitted here.</summary>
    public static Result AssembleCoast(RiverCoastPieces catalogue,RiverCoastPieces.Chain chain,int seed,
        Bounds area,IReadOnlyList<(double X,double Y)> boundaryPath,CancellationToken cancellation=default,bool verifyChannel=false,bool sealNativePockets=false)
    {
        if(chain.Closed||chain.Pieces.Count==0||area.Width<2||area.Height<2
            ||((area.X|area.Y)&1)!=0||boundaryPath.Count<2)
            throw new ArgumentException("Coastal ground requires an open bank path and an even native ground origin.");
        double right=area.X+area.Width,bottom=area.Y+area.Height;
        bool OnBoundary((double X,double Y) p)=>double.IsFinite(p.X)&&double.IsFinite(p.Y)
            &&p.X>=area.X&&p.X<=right&&p.Y>=area.Y&&p.Y<=bottom
            &&(p.X==area.X||p.X==right||p.Y==area.Y||p.Y==bottom);
        if(boundaryPath.Any(p=>!OnBoundary(p)))throw new ArgumentException("Coastal closure must follow the real map boundary.");
        for(int i=1;i<boundaryPath.Count;i++)
        {
            var a=boundaryPath[i-1];var b=boundaryPath[i];
            if(!(a.X==b.X&&(a.X==area.X||a.X==right)||a.Y==b.Y&&(a.Y==area.Y||a.Y==bottom)))
                throw new ArgumentException("Coastal closure cannot cross the map interior.");
        }
        var first=chain.Pieces[0];var last=chain.Pieces[^1];
        var start=first.Reverse?first.Piece.End:first.Piece.Start;var end=last.Reverse?last.Piece.Start:last.Piece.End;
        bool Near((double X,double Y) p,double x,double y)=>Math.Abs(p.X-x)<=.25&&Math.Abs(p.Y-y)<=.25;
        if(!Near(boundaryPath[0],last.X+end.ShoreX,last.Y+end.ShoreY)
            ||!Near(boundaryPath[^1],first.X+start.ShoreX,first.Y+start.ShoreY))
            throw new ArgumentException("The boundary closure must meet both original shore endpoints.");
        if(chain.Cells.Any(c=>c.X<area.X||c.Y<area.Y||c.X>=right||c.Y>=bottom))
            throw new ArgumentException("Coastal pieces must fit the map without cropping saved cells.");
        return AssembleCore(catalogue,chain,seed,area,boundaryPath,cancellation,verifyChannel:verifyChannel,sealNativePockets:sealNativePockets);
    }

    private static Result AssembleCore(RiverCoastPieces catalogue,RiverCoastPieces.Chain chain,int seed,
        Bounds? coastBounds,IReadOnlyList<(double X,double Y)>? boundaryPath,CancellationToken cancellation,bool verifyCovers=false,bool verifyChannel=false,bool inspectRejectedMovement=false,bool sealNativePockets=false)
    {
        if((!chain.Closed&&boundaryPath==null)||chain.Pieces.Count==0)throw new ArgumentException("Ground generation requires a closed native bank outline.");
        cancellation.ThrowIfCancellationRequested();var commands=new List<Command>();
        var nativeFrontBanks=chain.Pieces.SelectMany(p=>p.Piece.NativeFrontBankCommands
            .Select(c=>(X:p.X+c.X,Y:p.Y+c.Y,c.Library,c.Image))).ToHashSet();
        foreach(var saved in chain.Cells)
        {
            if(saved.Cell.MiddleImage>0)Add(saved.X,saved.Y,0,0,saved.Cell.MiddleLibrary,saved.Cell.MiddleImage);
            int order=0;foreach(var front in saved.Cell.ForegroundImages)Add(saved.X,saved.Y,1,order++,front.Library,front.Image&0x7fff);
        }
        void Add(int x,int y,int layer,int order,int library,int image)
        {
            if(!catalogue.NativeSprites.TryGetValue((library,image),out var sprite))throw new InvalidDataException("An assembled piece uses unverified artwork.");
            commands.Add(new(RiverArtwork.IsFloor(sprite)?layer:2,x,y,layer,order,library,image,sprite,
                layer==0||nativeFrontBanks.Contains((x,y,library,image))));
        }
        if(commands.Count==0)throw new InvalidDataException("Missing native bank artwork.");
        int PixelTop(Command c)=>c.Y*32+(c.Phase<2?0:32-c.Sprite.Height);
        var bounds=coastBounds??Measure(catalogue,chain);int left=bounds.X,top=bounds.Y,width=bounds.Width,height=bounds.Height;
        int sw=checked(width*12),sh=checked(height*8),length=checked(sw*sh);
        if(length>(coastBounds!=null?MaximumCoastCells*96:32_000_000))throw new ArgumentException("Native river ground analysis exceeds the working-area limit.");
        var bankRaster=new SparseRaster(sw,sh);var wholeRaster=new SparseRaster(sw,sh);
        var flowRaster=chain.BridgeWalkSupport.Count>0?new SparseRaster(sw,sh):null;
        var drawOrder=commands.OrderBy(c=>c.Phase).ThenBy(c=>c.Y).ThenBy(c=>c.X).ThenBy(c=>c.Layer).ThenBy(c=>c.Order).ToArray();
        foreach(var command in drawOrder)
        {
            cancellation.ThrowIfCancellationRequested();int x=(command.X-left)*48,y=PixelTop(command)-top*32;
            if(command.BankArtwork)bankRaster.Add(command.Sprite,x,y);
            wholeRaster.Add(command.Sprite,x,y);
            if(command.Layer!=1||!chain.BridgeWalkSupport.Any(p=>p.IsDeck(command.X,command.Y,command.Library,command.Image)))
                flowRaster?.Add(command.Sprite,x,y);
        }
        if((boundaryPath!=null||verifyCovers)&&ReorderingOverlaps())
        {
            var unobscuredCovers=new SparseRaster(sw,sh);
            // Keep source-validated banks in their actual native order, even
            // when stored in Front slots. Only scenery is moved after banks
            // for this diagnostic comparison; actual map drawing is unchanged.
            foreach(var command in commands.OrderBy(c=>c.Phase).ThenBy(c=>c.Phase==2&&!c.BankArtwork?1:0)
                .ThenBy(c=>c.Y).ThenBy(c=>c.X).ThenBy(c=>c.Layer).ThenBy(c=>c.Order))
                unobscuredCovers.Add(command.Sprite,(command.X-left)*48,PixelTop(command)-top*32);
            var savedOverlaps=chain.Pieces.Where(p=>p.Piece.BridgeComponent!=null)
                .SelectMany(p=>p.Piece.BridgeComponent!.SavedDrawOrderOverlaps.Select(q=>
                    (X:q.X+(p.X-left)*48,Y:q.Y+(p.Y-top)*32))).ToHashSet();
            if(!wholeRaster.SamePixels(unobscuredCovers,savedOverlaps))
            {
                var point=wholeRaster.FirstDifference(unobscuredCovers);
                string location=point is {} p?$" Near cell ({left+p.X/48},{top+p.Y/32}).":"";
                throw new InvalidDataException((boundaryPath!=null?"A coastal bank obscures an authored seam cover in the existing draw order.":"A river bank obscures an authored seam cover in the existing draw order.")+location);
            }
        }
        bool ReorderingOverlaps()
        {
            // The diagnostic order only moves tall scenery after tall banks.
            // Disjoint rectangles commute, so only an inverted overlapping pair
            // can change pixels and require the second full native raster.
            var scenery=new List<Command>();
            foreach(var command in drawOrder)
            {
                if(command.Phase!=2)continue;
                if(!command.BankArtwork){scenery.Add(command);continue;}
                int x=command.X*48,y=PixelTop(command);
                foreach(var earlier in scenery)
                {
                    int sx=earlier.X*48,sy=PixelTop(earlier);
                    if(x<sx+earlier.Sprite.Width&&sx<x+command.Sprite.Width&&
                        y<sy+earlier.Sprite.Height&&sy<y+command.Sprite.Height)return true;
                }
            }
            return false;
        }
        var banks=bankRaster.Reduce(true);var opaque=wholeRaster.Reduce(false);
        // The saved covers close authored bank gaps too. Ignoring their native
        // opacity lets an interior water region leak into exterior land and
        // selects dirt under an otherwise correctly covered river bank.
        var (labels,parents)=Regions(opaque,sw,sh,cancellation);var wetVotes=new int[parents.Length];var dryVotes=new int[parents.Length];
        foreach(int key in bankRaster.Keys)
        {
            if((banks[key]&1)==0)continue;bool water=(banks[key]&2)!=0;int x=key%sw;
            if(x>0)Vote(key-1);if(x+1<sw)Vote(key+1);if(key>=sw)Vote(key-sw);if(key+sw<length)Vote(key+sw);
            void Vote(int neighbour){int label=labels[neighbour];if(label>0){if(water)wetVotes[label]++;else dryVotes[label]++;}}
        }
        var regionWater=new bool[parents.Length];for(int i=1;i<regionWater.Length;i++)regionWater[i]=wetVotes[i]>dryVotes[i];
        var inside=Outline(chain,left,top,sw,sh,cancellation,boundaryPath);
        var borderMaterial=new sbyte[parents.Length];Array.Fill(borderMaterial,(sbyte)-1);
        void Border(int key)
        {
            int label=labels[key];if(label==0)return;bool water=boundaryPath!=null&&inside[key];
            if(boundaryPath!=null&&borderMaterial[label]>=0&&borderMaterial[label]!=(water?1:0))
                throw new InvalidDataException("An exposed coastal opening joins land and sea at the map boundary.");
            borderMaterial[label]=(sbyte)(water?1:0);regionWater[label]=water;
        }
        for(int x=0;x<sw;x++){Border(x);Border((sh-1)*sw+x);}
        for(int y=0;y<sh;y++){Border(y*sw);Border(y*sw+sw-1);}
        bool Expected(int key)=>opaque[key]!=0?inside[key]:regionWater[labels[key]];

        int columns=(width+1)/2,rows=(height+1)/2,tileCount=checked(columns*rows);var tileParents=Enumerable.Range(0,tileCount).ToArray();
        int Root(int i){while(tileParents[i]!=i){tileParents[i]=tileParents[tileParents[i]];i=tileParents[i];}return i;}
        void Union(int a,int b)=>tileParents[Root(a)]=Root(b);
        for(int gy=0;gy<rows;gy++)
        {
            cancellation.ThrowIfCancellationRequested();for(int gx=0;gx<columns;gx++)
            {
                int bx=gx*24,by=gy*16,tile=gy*columns+gx;
                if(gx>0)for(int y=by;y<Math.Min(sh,by+16);y++)if(opaque[y*sw+bx-1]==0&&opaque[y*sw+bx]==0){Union(tile,tile-1);break;}
                if(gy>0)for(int x=bx;x<Math.Min(sw,bx+24);x++)if(opaque[(by-1)*sw+x]==0&&opaque[by*sw+x]==0){Union(tile,tile-columns);break;}
            }
        }
        var wet=new int[tileCount];var dry=new int[tileCount];var hiddenWet=new int[tileCount];var hiddenTotal=new int[tileCount];
        var coveredTiles=new bool[tileCount];
        foreach(int key in wholeRaster.Keys)coveredTiles[key/sw/16*columns+key%sw/24]=true;
        var pinOrigins=new (int X,int Y)[tileCount];var pinned=new sbyte[tileCount];Array.Fill(pinned,(sbyte)-1);var savedCells=chain.Cells.ToDictionary(c=>(c.X,c.Y),c=>c.Cell);
        for(int gy=0;gy<rows;gy++)
        {
            cancellation.ThrowIfCancellationRequested();for(int gx=0;gx<columns;gx++)
            {
                int component=Root(gy*columns+gx),bx=gx*24,by=gy*16,visible=0;
                // An untouched tile is one connected exposed region. Count it
                // once; retain pixel-level analysis wherever native art occurs.
                if(!coveredTiles[gy*columns+gx])
                {
                    visible=Math.Min(16,sh-by)*Math.Min(24,sw-bx);
                    hiddenTotal[component]+=visible;
                    if(Expected(by*sw+bx)){hiddenWet[component]+=visible;wet[component]+=visible;}
                    else dry[component]+=visible;
                }
                else for(int y=by;y<Math.Min(sh,by+16);y++)for(int x=bx;x<Math.Min(sw,bx+24);x++)
                {
                    int key=y*sw+x;bool water=Expected(key);hiddenTotal[component]++;if(water)hiddenWet[component]++;
                    if(opaque[key]!=0)continue;visible++;if(water)wet[component]++;else dry[component]++;
                }
                if(visible>0&&savedCells.TryGetValue((left+gx*2,top+gy*2),out var cell)&&(cell.BackImage&0x1fffffff)!=0)
                    Pin(component,Water(cell.BackLibrary,cell.BackImage&0x1fffffff),left+gx*2,top+gy*2);
                if(boundaryPath==null&&(gx==0||gx==columns-1||gy==0||gy==rows-1))Pin(component,false,left+gx*2,top+gy*2);
            }
        }
        void Pin(int component,bool water,int x,int y)
        {
            int value=water?1:0;if(pinned[component]>=0&&pinned[component]!=value)throw new InvalidDataException($"Native local ground disagrees across an exposed bank opening between ({pinOrigins[component].X},{pinOrigins[component].Y}) and ({x},{y}).");
            pinned[component]=(sbyte)value;pinOrigins[component]=(x,y);
        }
        var required=new HashSet<(int,int)>();
        foreach(int key in bankRaster.Keys)
            required.Add((left+(key%sw)/24*2,top+(key/sw)/16*2));
        foreach(var saved in savedCells.Where(p=>(p.Value.BackImage&0x1fffffff)!=0))required.Add(saved.Key);
        var material=new bool[tileCount];var output=savedCells.ToDictionary(p=>p.Key,p=>p.Value.Copy());int waterCount=0,conflicts=0;
        foreach(var support in chain.BridgeWalkSupport)support.Validate(savedCells);
        for(int gy=0;gy<rows;gy++)
        {
            cancellation.ThrowIfCancellationRequested();for(int gx=0;gx<columns;gx++)
            {
                int tile=gy*columns+gx,component=Root(tile),x=left+gx*2,y=top+gy*2;
                bool water=pinned[component]>=0?pinned[component]==1:wet[component]+dry[component]>0?wet[component]>dry[component]:hiddenWet[component]>hiddenTotal[component]/2;
                var point=(x,y);if(!output.TryGetValue(point,out var cell))output[point]=cell=new();
                int original=cell.BackImage&0x1fffffff;
                if(original>0)water=Water(cell.BackLibrary,original);
                else
                {
                    long hash=(x*73856093L)^(y*19349663L)^seed;int variation=(int)((hash%5+5)%5);
                    cell.BackLibrary=0;cell.BackImage|=(water?1801:1)+variation;
                }
                material[tile]=water;if(water)
                {
                    required.Add(point);
                    waterCount++;for(int dy=0;dy<2;dy++)for(int dx=0;dx<2;dx++)
                    {
                        var position=(x+dx,y+dy);
                        if(position.Item1>=left+width||position.Item2>=top+height)continue;
                        if(!output.TryGetValue(position,out var wetCell))output[position]=wetCell=new();
                        // Crystal's existing v1 client/server walking check reads
                        // 0x20000000. Keep the editor's high collision marker too.
                        wetCell.BackImage|=0x60000000;
                    }
                }
            }
        }
        for(int x=24;x<sw;x+=24)for(int y=0;y<sh;y++)
            if(material[y/16*columns+x/24-1]!=material[y/16*columns+x/24]&&opaque[y*sw+x-1]==0&&opaque[y*sw+x]==0)conflicts++;
        for(int y=16;y<sh;y+=16)for(int x=0;x<sw;x++)
            if(material[(y/16-1)*columns+x/24]!=material[y/16*columns+x/24]&&opaque[(y-1)*sw+x]==0&&opaque[y*sw+x]==0)conflicts++;
        if(conflicts>0)throw new InvalidDataException("Assembled banks expose incompatible generated ground tiles.");
        // Keep native surface evidence for every river mode, including bridges
        // whose deck can interrupt the visible blue surface.
        var waterSurface=wholeRaster.WaterCells(material,columns,width,height);
        var waterFlow=flowRaster?.WaterCells(material,columns,width,height)??waterSurface;
        if(verifyChannel)
        {
            // Analyse the unchanged native artwork over ordinary generated ground.
            // A walkable map can still contain disconnected blue bank strips around
            // a dry channel; reject that candidate rather than repair its cells.
            int total=waterFlow.Count(v=>v),largest=0;var seen=new bool[waterFlow.Length];var queue=new Queue<int>();
            for(int first=0;first<waterFlow.Length;first++)
            {
                if((first&1023)==0)cancellation.ThrowIfCancellationRequested();
                if(!waterFlow[first]||seen[first])continue;
                int count=0;seen[first]=true;queue.Enqueue(first);
                while(queue.TryDequeue(out int key))
                {
                    count++;int x=key%width,y=key/width;
                    Visit(x-1,y);Visit(x+1,y);Visit(x,y-1);Visit(x,y+1);
                    void Visit(int xx,int yy)
                    {
                        if(xx<0||yy<0||xx>=width||yy>=height)return;
                        int next=yy*width+xx;if(!waterFlow[next]||seen[next])return;
                        seen[next]=true;queue.Enqueue(next);
                    }
                }
                largest=Math.Max(largest,count);
            }
            if(total==0||largest<total*.995)throw new InvalidDataException("The native river surface contains disconnected water strips or dry channel gaps.");
        }
        foreach(var support in chain.BridgeWalkSupport)support.Apply(output);
        var blocked=new bool[checked(width*height)];foreach(var (point,cell) in output)
            if((cell.BackImage&0x60000000)!=0||cell.ForegroundImages.Any(f=>(f.Image&0x8000)!=0))blocked[(point.Item2-top)*width+point.Item1-left]=true;
        if(sealNativePockets)
        {
            // Tiny unreachable ground fragments underneath original bank art
            // must have collision just like the surrounding bank. Change only
            // collision flags; retain every sprite and ground image unchanged.
            var coverage=wholeRaster.Keys.GroupBy(k=>(k/sw/8)*width+(k%sw/12)).ToDictionary(g=>g.Key,g=>g.Count());
            var seenPockets=new bool[blocked.Length];
            for(int first=0;first<blocked.Length;first++)
            {
                if((first&1023)==0)cancellation.ThrowIfCancellationRequested();
                if(blocked[first]||seenPockets[first])continue;
                var component=new List<int>{first};seenPockets[first]=true;
                for(int i=0;i<component.Count;i++)
                {
                    int x=component[i]%width,y=component[i]/width;
                    for(int dy=-1;dy<=1;dy++)for(int dx=-1;dx<=1;dx++)
                    {
                        int xx=x+dx,yy=y+dy;
                        if((uint)xx>=(uint)width||(uint)yy>=(uint)height)continue;
                        int next=yy*width+xx;if(blocked[next]||seenPockets[next])continue;
                        seenPockets[next]=true;component.Add(next);
                    }
                }
                if(component.Count>4||component.Any(k=>coverage.GetValueOrDefault(k)<48))continue;
                foreach(int key in component)
                {
                    var point=(left+key%width,top+key/width);
                    if(!output.TryGetValue(point,out var cell))output[point]=cell=new();
                    cell.BackImage|=0x60000000;blocked[key]=true;
                }
            }
        }
        int disconnected=Disconnected(blocked,width,height,cancellation);
        if(disconnected>0&&!inspectRejectedMovement)
        {
            string location="";
            if(disconnected==1)
                for(int key=0;key<blocked.Length;key++)
                {
                    if(blocked[key])continue;int x=key%width,y=key/width;bool neighbour=false;
                    for(int dy=-1;dy<=1;dy++)for(int dx=-1;dx<=1;dx++)
                    {
                        if(dx==0&&dy==0)continue;int xx=x+dx,yy=y+dy;
                        if((uint)xx<(uint)width&&(uint)yy<(uint)height&&!blocked[yy*width+xx])neighbour=true;
                    }
                    if(neighbour)continue;location=$" Near cell ({x+left},{y+top}).";break;
                }
            throw new InvalidDataException($"The generated river isolates {disconnected} walkable land cells.{location}");
        }
        return new(left,top,width,height,output.OrderBy(p=>p.Key.Item2).ThenBy(p=>p.Key.Item1)
            .Select(p=>new StampCell(p.Key.Item1,p.Key.Item2,p.Value)).ToArray(),waterCount,conflicts,disconnected)
            { RequiredGroundAnchors=required,NativeWaterSurface=waterSurface,NativeWaterFlow=waterFlow };
    }

    private static bool Water(int library,int image)
    {
        if(library!=0||image is not (>=1 and <=5 or >=1801 and <=1805))throw new InvalidDataException("Unsupported native river underlay family.");
        return image>=1801;
    }

    private static bool[] Outline(RiverCoastPieces.Chain chain,int left,int top,int width,int height,CancellationToken cancellation,IReadOnlyList<(double X,double Y)>? boundaryPath=null)
    {
        var points=new List<(double X,double Y)>();
        if(boundaryPath==null)
        {
            for(int i=0;i<chain.Pieces.Count;i++)
            {
                var placed=chain.Pieces[i];var previous=chain.Pieces[(i+chain.Pieces.Count-1)%chain.Pieces.Count];
                var start=placed.Reverse?placed.Piece.End:placed.Piece.Start;var other=previous.Reverse?previous.Piece.Start:previous.Piece.End;
                points.Add(((placed.X+start.ShoreX-left)*12,(placed.Y+start.ShoreY-top)*8));
                points.Add(((previous.X+other.ShoreX-left)*12,(previous.Y+other.ShoreY-top)*8));
            }
        }
        else
        {
            foreach(var placed in chain.Pieces)
            {
                var start=placed.Reverse?placed.Piece.End:placed.Piece.Start;var end=placed.Reverse?placed.Piece.Start:placed.Piece.End;
                points.Add(((placed.X+start.ShoreX-left)*12,(placed.Y+start.ShoreY-top)*8));
                points.Add(((placed.X+end.ShoreX-left)*12,(placed.Y+end.ShoreY-top)*8));
            }
            points.AddRange(boundaryPath.Select(p=>((p.X-left)*12,(p.Y-top)*8)));
        }
        var inside=new bool[checked(width*height)];var intersections=new List<double>();
        for(int y=0;y<height;y++)
        {
            if((y&63)==0)cancellation.ThrowIfCancellationRequested();intersections.Clear();double line=y+.5;
            for(int i=0;i<points.Count;i++)
            {
                var a=points[i];var b=points[(i+1)%points.Count];if((a.Y>line)==(b.Y>line))continue;
                intersections.Add(a.X+(line-a.Y)*(b.X-a.X)/(b.Y-a.Y));
            }
            intersections.Sort();for(int i=0;i+1<intersections.Count;i+=2)
            {
                int start=Math.Max(0,(int)Math.Ceiling(intersections[i]-.5)),end=Math.Min(width,(int)Math.Ceiling(intersections[i+1]-.5));
                if(end>start)inside.AsSpan(y*width+start,end-start).Fill(true);
            }
        }
        return inside;
    }

    private static (int[] Labels,int[] Parents) Regions(byte[] mask,int width,int height,CancellationToken cancellation)
    {
        var labels=new int[mask.Length];var parents=new List<int>{0};var previous=new List<Run>();
        int Root(int i){while(parents[i]!=i){parents[i]=parents[parents[i]];i=parents[i];}return i;}
        for(int y=0;y<height;y++)
        {
            if((y&63)==0)cancellation.ThrowIfCancellationRequested();var current=new List<Run>();int j=0;
            for(int x=0;x<width;)
            {
                // Reduce(false) produces only 0/1. Span searches vectorize the
                // long empty stretches of a province's land and sea.
                int next=mask.AsSpan(y*width+x,width-x).IndexOf((byte)0);
                if(next<0)break;
                x+=next;int start=x;
                int occupied=mask.AsSpan(y*width+x,width-x).IndexOfAnyExcept((byte)0);
                x=occupied<0?width:x+occupied;int end=x,label=0;
                while(j<previous.Count&&previous[j].End<=start)j++;
                for(int k=j;k<previous.Count&&previous[k].Start<end;k++)
                {
                    int other=Root(previous[k].Label);if(label==0)label=other;else parents[other]=Root(label);
                }
                if(label==0){label=parents.Count;parents.Add(label);}
                labels.AsSpan(y*width+start,end-start).Fill(label);current.Add(new(start,end,label));
            }
            previous=current;
        }
        var roots=Enumerable.Range(0,parents.Count).Select(Root).ToArray();
        for(int i=0;i<labels.Length;i++)labels[i]=roots[labels[i]];
        return (labels,roots);
    }

    private static int Disconnected(bool[] blocked,int width,int height,CancellationToken cancellation)
    {
        var seen=new bool[blocked.Length];var queue=new Queue<int>();int total=0,largest=0;
        for(int first=0;first<blocked.Length;first++)
        {
            if((first&1023)==0)cancellation.ThrowIfCancellationRequested();if(blocked[first]||seen[first])continue;
            int count=0;queue.Enqueue(first);seen[first]=true;
            while(queue.TryDequeue(out int key))
            {
                count++;int x=key%width,y=key/width;
                for(int dy=-1;dy<=1;dy++)for(int dx=-1;dx<=1;dx++)
                {
                    int xx=x+dx,yy=y+dy;if((uint)xx>=(uint)width||(uint)yy>=(uint)height)continue;
                    int next=yy*width+xx;if(blocked[next]||seen[next])continue;seen[next]=true;queue.Enqueue(next);
                }
            }
            total+=count;largest=Math.Max(largest,count);
        }
        return total-largest;
    }

    // Sparse original-pixel samples avoid allocating a full-resolution map.
    // Two analysis masks retain the existing floor/tall and Front-list order.
    private sealed class SparseRaster(int width,int height)
    {
        // Allocate native-pixel sample references in small spatial chunks. Bank
        // art is sparse, but neighbouring samples are dense; this avoids several
        // hash lookups and a separate ownership set for every 4x4 pixel block.
        private sealed class Chunk
        {
            public readonly byte[]?[] Pixels=new byte[1024][];
            public readonly uint[] Owned=new uint[32];
        }
        private readonly int chunkWidth=(width+31)/32;
        private readonly Chunk?[] chunks=new Chunk[((width+31)/32)*((height+31)/32)];
        private readonly List<int> keys=[];
        public IEnumerable<int> Keys=>keys;
        private byte[]? Get(int key)
        {
            int y=Math.DivRem(key,width,out int x);
            return chunks[(y>>5)*chunkWidth+(x>>5)]?.Pixels[(y&31)*32+(x&31)];
        }
        private IEnumerable<(int Key,byte[] Value)> Samples
        {
            get {foreach(int key in keys)yield return (key,Get(key)!);}
        }
        private sealed record Block(int X,int Y,byte[] Pixels,bool Opaque);
        private static readonly ConditionalWeakTable<RiverArtwork.Pixels,ConcurrentDictionary<int,Block[]>> Prepared=new();
        private static Block[] Prepare(RiverArtwork.Pixels sprite,int phase)
        {
            int ox=phase&3,oy=phase>>2;var blocks=new List<Block>();
            for(int by=0;by<(oy+sprite.Height+3)/4;by++)
            for(int bx=0;bx<(ox+sprite.Width+3)/4;bx++)
            {
                byte[]? pixels=null;int opaque=0;
                for(int y=Math.Max(oy,by*4);y<Math.Min(oy+sprite.Height,by*4+4);y++)
                for(int x=Math.Max(ox,bx*4);x<Math.Min(ox+sprite.Width,bx*4+4);x++)
                {
                    int src=((y-oy)*sprite.Width+x-ox)*4,alpha=sprite.Bgra[src+3];
                    if(alpha==0)continue;
                    pixels??=new byte[64];sprite.Bgra.AsSpan(src,4).CopyTo(pixels.AsSpan(((y&3)*4+(x&3))*4,4));
                    if(alpha==255)opaque++;
                }
                if(pixels!=null)blocks.Add(new(bx,by,pixels,opaque==16));
            }
            return blocks.ToArray();
        }
        public void Add(RiverArtwork.Pixels sprite,int left,int top)
        {
            int phase=(left&3)|((top&3)<<2);
            var blocks=Prepared.GetValue(sprite,_=>new()).GetOrAdd(phase,p=>Prepare(sprite,p));
            int originX=left>>2,originY=top>>2;
            foreach(var block in blocks)
            {
                int x=originX+block.X,y=originY+block.Y;
                if((uint)x>=(uint)width||(uint)y>=(uint)height)continue;
                int key=y*width+x;var source=block.Pixels;
                var chunk=chunks[(y>>5)*chunkWidth+(x>>5)]??=new Chunk();
                int row=y&31,slot=row*32+(x&31);uint bit=1u<<(x&31);
                var sample=chunk.Pixels[slot];
                if(sample==null){chunk.Pixels[slot]=source;keys.Add(key);continue;}
                if(block.Opaque){chunk.Pixels[slot]=source;chunk.Owned[row]&=~bit;continue;}
                // Most blocks never overlap another sprite. Share immutable
                // prepared pixels; allocate only when alpha blending needs a write.
                if((chunk.Owned[row]&bit)==0)
                {chunk.Owned[row]|=bit;chunk.Pixels[slot]=sample=(byte[])sample.Clone();}
                for(int p=0;p<64;p+=4)
                {
                    int a=source[p+3];if(a==0)continue;
                    int old=sample[p+3];
                    if(a==255||old==0){source.AsSpan(p,4).CopyTo(sample.AsSpan(p,4));continue;}
                    int alpha=a*255+old*(255-a);
                    for(int k=0;k<3;k++)sample[p+k]=(byte)((source[p+k]*a*255+sample[p+k]*old*(255-a)+alpha/2)/alpha);
                    sample[p+3]=(byte)((alpha+127)/255);
                }
            }
        }
        public bool[] WaterCells(bool[] material,int columns,int cellWidth,int cellHeight)
        {
            var blueCounts=new int[checked(cellWidth*cellHeight)];
            for(int y=0;y<cellHeight;y++)for(int x=0;x<cellWidth;x++)
                if(material[y/2*columns+x/2])blueCounts[y*cellWidth+x]=48*32;
            foreach(var (key,pixels) in Samples)
            {
                int x=key%width,y=key/width,cellX=x/12,cellY=y/8;
                if(cellX>=cellWidth||cellY>=cellHeight)continue;
                bool groundWet=material[cellY/2*columns+cellX/2];
                for(int p=0;p<64;p+=4)
                {
                    // Near-transparent edge pixels keep the material beneath;
                    // opaque native pixels determine the visible bank surface.
                    if(pixels[p+3]<=250)continue;
                    bool blue=pixels[p]>pixels[p+2]+35&&pixels[p]>pixels[p+1]+15;
                    blueCounts[cellY*cellWidth+cellX]+=(blue?1:0)-(groundWet?1:0);
                }
            }
            return blueCounts.Select(count=>count>48*32*.45).ToArray();
        }
        public bool SamePixels(SparseRaster other)=>keys.Count==other.keys.Count&&Samples.All(p=>
            other.Get(p.Key) is {} pixels&&(ReferenceEquals(p.Value,pixels)||p.Value.AsSpan().SequenceEqual(pixels)));
        public bool SamePixels(SparseRaster other,IReadOnlySet<(int X,int Y)> savedOverlaps)
        {
            if(savedOverlaps.Count==0)return SamePixels(other);
            foreach(int key in keys.Union(other.keys))
            {
                var a=Get(key);var b=other.Get(key);
                for(int i=0;i<64;i++)if((a?[i]??0)!=(b?[i]??0)
                    &&!savedOverlaps.Contains((key%width*4+(i/4)%4,key/width*4+(i/4)/4)))return false;
            }
            return true;
        }
        public (int X,int Y)? FirstDifference(SparseRaster other)
        {
            foreach(int key in keys.Union(other.keys).Order())
            {
                var a=Get(key);var b=other.Get(key);
                for(int i=0;i<64;i++)if((a?[i]??0)!=(b?[i]??0))
                    return(key%width*4+(i/4)%4,key/width*4+(i/4)/4);
            }
            return null;
        }
        public byte[] Reduce(bool colours)
        {
            var reduced=new byte[checked(width*height)];foreach(var (key,pixels) in Samples)
            {
                int a=0,b=0,g=0,r=0;for(int p=0;p<64;p+=4)
                {int alpha=pixels[p+3];a+=alpha;if(colours){b+=pixels[p]*alpha;g+=pixels[p+1]*alpha;r+=pixels[p+2]*alpha;}}
                if((a+8)/16>250)reduced[key]=1;
                if(colours&&a>0&&(b+a/2)/a>(r+a/2)/a+35&&(b+a/2)/a>(g+a/2)/a+15)reduced[key]|=2;
            }
            return reduced;
        }
    }
}
