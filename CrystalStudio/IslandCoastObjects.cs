using System.IO;
using System.Security.Cryptography;
using System.Text.Json;

namespace CrystalStudio;

/// <summary>Closed island shores assembled from unchanged objects used in 5.map.</summary>
public sealed class IslandCoastObjects
{
    private readonly Dictionary<int,IReadOnlyList<StampCell>> pieces;
    private IslandCoastObjects(Dictionary<int,IReadOnlyList<StampCell>> pieces)=>this.pieces=pieces;

    public static IslandCoastObjects Load(AssetStore assets)
    {
        string folder=DataPaths.ResolveEditor("catalogue","island");
        using var metadata=JsonDocument.Parse(File.ReadAllText(Path.Combine(folder,"catalogue.json")));
        var pieces=new Dictionary<int,IReadOnlyList<StampCell>>();
        foreach(var proof in metadata.RootElement.GetProperty("pieces").EnumerateArray())
        {
            string file=proof.GetProperty("file").GetString()!;
            if(Path.GetFileName(file)!=file)throw new InvalidDataException("Invalid island coast object path.");
            string path=Path.Combine(folder,file);
            if(Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)))!=proof.GetProperty("sha256").GetString())
                throw new InvalidDataException("Island coast object differs from its original artwork.");
            var cells=CellCodec.LoadStamp(path);
            if(cells.Count>256||cells.Any(c=>c.Cell.MiddleImage!=0||c.Cell.ForegroundImages.Count!=0||c.Cell.Door!=0
                ||(c.Cell.BackImage&0x1fffffff)!=0&&((c.X|c.Y)&1)!=0))throw new InvalidDataException("Invalid native island shore piece.");
            pieces.Add(proof.GetProperty("number").GetInt32(),cells);
        }
        foreach(var proof in metadata.RootElement.GetProperty("sprites").EnumerateArray())
        {
            int library=proof.GetProperty("library").GetInt32(),image=proof.GetProperty("image").GetInt32();
            if(!assets.Libraries.TryGetValue(library,out var lib)||lib.Decode(image-1) is not {} sprite||sprite.Width!=96||sprite.Height!=64)
                throw new InvalidDataException("The map-5 island tiles are unavailable in these libraries.");
        }
        return new(pieces);
    }

    internal TerrainCoastPlacement Plan(MapDocument document,int seed,CancellationToken cancellation)
    {
        int width=document.Width,height=document.Height;
        if(width<96||height<96)throw new ArgumentException("Island terrain needs a map at least 96×96 cells to fit the complete native shore.");
        var random=new Random(seed^0x17A5D);
        // Each side has independent headlands and recessed bays. Their total
        // diagonal travel is shared, so the original corner groups close exactly.
        // Width is chosen after the side courses, rather than constraining every
        // coast to the same horizontal diamond.
        int peaks=width<128?1:Math.Clamp((height-110)/130,1,4);
        peaks=random.Next(height>=400&&width>=240?2:1,peaks+1);
        int cornerHeight=40*peaks+42*(peaks-1);
        int travel=Math.Max(peaks, (height-cornerHeight-72)/16);
        travel=Math.Min(travel,Math.Max(peaks,(width-72)/16));
        travel=Math.Max(peaks,(int)(travel*(.50+random.NextDouble()*.36)));
        int leanLimit=Math.Min(Math.Max(0,travel-peaks)/2,Math.Max(0,(width-112)/64));
        int lean=random.Next(-leanLimit,leanLimit+1);
        int[] west=[],east=[];
        int sideWidth=0,minimumCount=2;
        for(;travel>=peaks;travel--)
        {
            lean=Math.Clamp(lean,-Math.Max(0,travel-peaks)/2,Math.Max(0,travel-peaks)/2);
            west=Excursions(travel,peaks,travel*2,lean);
            east=Excursions(travel,peaks,travel*2,-lean);
            sideWidth=(Peak(west)+Peak(east))*8;
            minimumCount=(peaks>1?10:2)+2*Math.Abs(lean);
            if(sideWidth<=width-minimumCount*8-28)break;
        }
        int count=Math.Max(minimumCount,((width-sideWidth-28)/8)&~1);
        count=Math.Max(minimumCount,(int)(count*(.62+random.NextDouble()*.38))&~1);
        int rippleCount=Math.Min((travel-peaks)/2, Math.Max(0,travel/3));
        int[] westSlopes=Ripples(west,rippleCount),eastSlopes=Ripples(east,rippleCount);
        int sideHeight=16*(travel-rippleCount)+cornerHeight;
        int spare=Math.Max(0,(height-sideHeight-32)/8);
        int northRoom=(int)(spare*(.2+random.NextDouble()*.6));
        int inlet=Math.Max(0,(sideHeight-48)/24);
        var north=ShoreCourse(count,Math.Max(1,northRoom),inlet);
        var south=ShoreCourse(count,Math.Max(1,spare-northRoom),inlet);
        var placed=new List<(int Number,int X,int Y)>();
        AppendHorizontal(north,-1,0,1977,0);
        AppendHorizontal(south,1,sideHeight,1981,-16*lean);
        AppendSide(east,eastSlopes,(count-1)*8,1);
        AppendSide(west,westSlopes,0,-1);
        int left=placed.Min(p=>p.X+pieces[p.Number].Min(c=>c.X)),topY=placed.Min(p=>p.Y+pieces[p.Number].Min(c=>c.Y));
        int right=placed.Max(p=>p.X+pieces[p.Number].Max(c=>c.X)+1),bottomY=placed.Max(p=>p.Y+pieces[p.Number].Max(c=>c.Y)+1);
        int shiftX=((width-(right-left))/2-left)&~1,shiftY=((height-(bottomY-topY))/2-topY)&~1;
        var saved=new Dictionary<(int X,int Y),Cell>();
        foreach(var (number,x,y) in placed)
        {
            cancellation.ThrowIfCancellationRequested();
            foreach(var s in pieces[number])
            {
                var key=(x+s.X+shiftX,y+s.Y+shiftY);
                if(!document.Contains(key.Item1,key.Item2))throw new InvalidDataException("Native island course exceeds the map.");
                if(saved.TryGetValue(key,out var previous))
                {
                    int a=previous.BackImage&0x1fffffff,b=s.Cell.BackImage&0x1fffffff;
                    if(a!=0&&b!=0&&(a!=b||previous.BackLibrary!=s.Cell.BackLibrary))throw new InvalidDataException("Conflicting native island coast anchors.");
                    previous.BackImage|=s.Cell.BackImage&0x60000000;
                    if(a==0&&b!=0){previous.BackImage|=b;previous.BackLibrary=s.Cell.BackLibrary;}
                }
                else saved[key]=s.Cell.Copy();
            }
        }
        // Flood only empty ground anchors. The complete native ring separates
        // ocean from the island, so the fill cannot leave seams or inland holes.
        int w=(width+1)/2,h=(height+1)/2;
        var shore=new bool[w*h];
        foreach(var (point,cell) in saved)if((cell.BackImage&0x1fffffff)!=0)shore[point.Y/2*w+point.X/2]=true;
        var ocean=new bool[w*h];var queue=new Queue<int>();queue.Enqueue(0);ocean[0]=true;
        while(queue.TryDequeue(out int key))
        {
            if((key&1023)==0)cancellation.ThrowIfCancellationRequested();
            int x=key%w,y=key/w;
            if(x>0)Visit(key-1);if(x+1<w)Visit(key+1);if(y>0)Visit(key-w);if(y+1<h)Visit(key+w);
        }
        for(int y=0;y<h;y++)
        {
            cancellation.ThrowIfCancellationRequested();
            for(int x=0;x<w;x++)if(ocean[y*w+x])
            {
                int image=3001+random.Next(5);
                for(int dy=0;dy<2;dy++)for(int dx=0;dx<2;dx++)
                {
                    int px=x*2+dx,py=y*2+dy;if(px>=width||py>=height)continue;
                    if(!saved.TryGetValue((px,py),out var cell))saved[(px,py)]=cell=new();
                    cell.BackImage|=0x60000000;
                    if(dx==0&&dy==0){cell.BackLibrary=0;cell.BackImage=(cell.BackImage&0x60000000)|image;}
                }
            }
        }
        var cells=saved.OrderBy(p=>p.Key.Y).ThenBy(p=>p.Key.X).Select(p=>new StampCell(p.Key.X,p.Key.Y,p.Value)).ToArray();
        var layout=new RiverCoastLayout($"island-{seed}","Island coast · 5.map",cells)
            {Width=width,Height=height,WaterMode=TerrainWaterMode.Beach,BoundaryEdges=["north","east","south","west"]};
        var placement=new TerrainCoastPlacement(layout,0,0){DiagonalAccess=true};
        if(!TerrainCoastObjects.CanPlace(document,layout,0,0))throw new ArgumentException("Island generation needs clear space for its shore and surrounding ocean. Existing objects and collisions are protected.");
        TerrainCoastObjects.VerifyAccess(document,placement,cancellation);
        return placement;

        void Visit(int p){if(!ocean[p]&&!shore[p]){ocean[p]=true;queue.Enqueue(p);}}
        void Add(int number,int x,int y)=>placed.Add((number,x,y));
        void AppendHorizontal(int[] course,int sign,int y,int first,int startX)
        {
            for(int i=0;i<count;i++)
            {
                if(i>0)y+=(course[i-1]+course[i])*sign*4;
                Add(first+(course[i]*sign>0?2:0)+random.Next(2),startX+i*8,y);
            }
        }
        void AppendSide(int[] course,int[] slopes,int x,int outward)
        {
            int y=0,previous=1;
            for(int i=0;i<course.Length;i++)
            {
                int direction=course[i];
                x+=direction*outward*8;y+=(previous+slopes[i])*4;previous=slopes[i];
                // The last piece is already the end of the southern course.
                if(i+1<course.Length)
                {
                    int family=outward>0?(direction>0?(slopes[i]>0?1979:1977):(slopes[i]>0?1981:1983))
                        :(direction>0?(slopes[i]>0?1977:1979):(slopes[i]>0?1983:1981));
                    Add(family+random.Next(2),x,y);
                }
                if(i+1<course.Length&&course[i+1]!=direction)
                {
                    if(outward>0&&direction>0)
                    {Add(1991,x+8,y+4);Add(1992,x+12,y+20);Add(1993,x+10,y+34);y+=40;}
                    else if(outward>0)
                    {Add(1985,x-10,y+4);Add(1986,x-14,y+24);Add(1987,x-10,y+38);y+=42;}
                    else if(direction>0)
                    {Add(1994,x-10,y+6);Add(1995,x-10,y+22);Add(1996,x-8,y+36);y+=40;}
                    else
                    {Add(1988,x+12,y+6);Add(1989,x+14,y+22);Add(1990,x+14,y+38);y+=42;}
                    Add((outward>0?(direction>0?1981:1979):(direction>0?1983:1977))+random.Next(2),x,y);
                }
            }
        }
        int[] Ripples(int[] course,int count)
        {
            var result=Enumerable.Repeat(1,course.Length).ToArray();
            var slots=Enumerable.Range(0,course.Length-1).Where(i=>course[i]==course[i+1]).ToArray();
            random.Shuffle(slots);
            int remaining=count;
            foreach(int i in slots)
            {
                if(remaining==0)break;
                if(i>0&&result[i-1]<0||result[i+1]<0)continue;
                result[i]=-1;remaining--;
            }
            return result;
        }
        int[] Roughen(int[] course,int low,int high)
        {
            if(course.Length<6)return course;
            var baseline=new int[course.Length];int original=0;
            for(int i=0;i<course.Length;i++){original+=course[i];baseline[i]=original;}
            for(int attempt=0;attempt<course.Length;attempt++)
            {
                int a=random.Next(1,course.Length-1),b=random.Next(1,course.Length-1);
                if(course[a]==course[b]||course[a-1]!=course[a]||course[a+1]!=course[a]
                    ||course[b-1]!=course[b]||course[b+1]!=course[b])continue;
                (course[a],course[b])=(course[b],course[a]);
                int level=0;bool fits=true;
                for(int i=0;i<course.Length;i++){level+=course[i];if(level<low||level>high||Math.Abs(level-baseline[i])>2)fits=false;}
                if(!fits)(course[a],course[b])=(course[b],course[a]);
            }
            return course;
        }
        int[] ShoreCourse(int length,int limit,int inlet)
        {
            int total=length/2;
            int minimum=Math.Max(total>=6?2:1,(total+limit+inlet-1)/(limit+inlet));
            int lobes=random.Next(minimum,Math.Min(total,minimum+3)+1);
            int[]? best=null;double bestScore=double.NegativeInfinity;
            bool bay=random.NextDouble()<.75;
            for(int attempt=0;attempt<96;attempt++)
            {
                cancellation.ThrowIfCancellationRequested();
                var up=Partition(total,lobes);var down=Partition(total,lobes);
                int level=0,low=0,high=0;bool fits=true;
                for(int i=0;i<lobes;i++)
                {
                    level+=up[i];high=Math.Max(high,level);if(level>limit)fits=false;
                    level-=down[i];low=Math.Min(low,level);if(level < -inlet)fits=false;
                }
                double score=(bay?-low:high)*2+(high-low)*.3+random.NextDouble()*2;
                if(fits&&score>bestScore){best=Expand(up,down);bestScore=score;}
            }
            return Roughen(best??Excursions(total,Math.Max(lobes,(total+limit-1)/limit),limit),-inlet,limit);
        }
        int[] Excursions(int total,int lobes,int limit,int drift=0)
        {
            // A bounded composition of unequal rises and falls gives broad bays,
            // rather than a regular one-piece sawtooth around a smooth envelope.
            for(int attempt=0;attempt<48;attempt++)
            {
                cancellation.ThrowIfCancellationRequested();
                var up=Partition(total+drift,lobes);var down=Partition(total-drift,lobes);
                int level=0;bool fits=true;
                for(int i=0;i<lobes;i++)
                {
                    level+=up[i];if(level>limit)fits=false;
                    level-=down[i];if(level<Math.Min(0,drift*2))fits=false;
                }
                if(fits)return Expand(up,down);
            }
            var even=new int[lobes];
            for(int i=0;i<total;i++)even[i%lobes]++;
            random.Shuffle(even);
            var fall=(int[])even.Clone();
            for(int i=0;i<Math.Abs(drift);i++)
            {
                if(drift>0){even[0]++;fall[Array.FindLastIndex(fall,n=>n>1)]--;}
                else{fall[^1]++;even[Array.FindIndex(even,n=>n>1)]--;}
            }
            return Expand(even,fall);
        }
        int[] Partition(int total,int parts)
        {
            var sizes=Enumerable.Repeat(1,parts).ToArray();
            // Unequal weights produce a few substantial peninsulas alongside
            // smaller coves; left and right receive independent allocations.
            var weights=Enumerable.Range(0,parts).Select(_=>.1+Math.Pow(random.NextDouble(),2)).ToArray();
            double sum=weights.Sum();
            for(int n=parts;n<total;n++)
            {
                double pick=random.NextDouble()*sum;int at=0;
                while(at+1<parts&&pick>=weights[at])pick-=weights[at++];
                sizes[at]++;
            }
            return sizes;
        }
        static int[] Expand(int[] up,int[] down)
        {
            var result=new List<int>();
            for(int i=0;i<up.Length;i++){result.AddRange(Enumerable.Repeat(1,up[i]));result.AddRange(Enumerable.Repeat(-1,down[i]));}
            return result.ToArray();
        }
        static int Peak(int[] steps){int level=0,peak=0;foreach(int step in steps){level+=step;peak=Math.Max(peak,level);}return peak;}
    }
}
