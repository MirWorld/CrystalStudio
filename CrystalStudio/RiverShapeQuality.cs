namespace CrystalStudio;

/// <summary>Rejects square elbows using native-water planning evidence.
/// It never draws, changes map cells, or repairs a bank with scenery.</summary>
public static class RiverShapeQuality
{
    public sealed record Elbow(double X,double Y,double AngleDegrees,double ChannelWidth,double IntersectionDistance,string Location="Channel");
    public sealed record Report(int Ends,int Junctions,IReadOnlyList<Elbow> Elbows)
    {
        public bool SimpleGentleChannel=>Ends==2&&Junctions==0&&Elbows.Count==0;
        public bool GentleTributaryChannel=>Ends==3&&Junctions==1&&Elbows.Count==0;
    }
    private readonly record struct Point(double X,double Y)
    {
        public static Point operator +(Point a,Point b)=>new(a.X+b.X,a.Y+b.Y);
        public static Point operator -(Point a,Point b)=>new(a.X-b.X,a.Y-b.Y);
        public static Point operator *(Point a,double b)=>new(a.X*b,a.Y*b);
        public double Length=>Math.Sqrt(X*X+Y*Y);
        public double Dot(Point other)=>X*other.X+Y*other.Y;
        public double Cross(Point other)=>X*other.Y-Y*other.X;
    }

    public static Report Analyse(RiverPieceGroundAssembler.Result ground,CancellationToken cancellation=default,double minimumBranchLength=8)
    {
        if(ground.NativeWaterSurface==null)throw new ArgumentException("Shape quality requires native water evidence.",nameof(ground));
        return Analyse(ground.NativeWaterFlow??ground.NativeWaterSurface,ground.Width,ground.Height,cancellation,minimumBranchLength);
    }

    public static Report Analyse(IReadOnlyList<bool> water,int width,int height,CancellationToken cancellation=default,double minimumBranchLength=8)
    {
        if(width<3||height<3||water.Count!=checked(width*height))throw new ArgumentException("Invalid native water dimensions.");
        var skeleton=Thin(water,width,height,cancellation);
        Dictionary<int,List<int>> Graph()
        {
            var edges=new Dictionary<int,List<int>>();
            for(int key=0;key<skeleton.Length;key++)
            {
                if(!skeleton[key])continue;int x=key%width,y=key/width;var next=new List<int>();
                for(int dy=-1;dy<=1;dy++)for(int dx=-1;dx<=1;dx++)
                {
                    int xx=x+dx,yy=y+dy;
                    if(dx==0&&dy==0||xx<0||yy<0||xx>=width||yy>=height||!skeleton[yy*width+xx])continue;
                    if(dx!=0&&dy!=0&&(skeleton[y*width+xx]||skeleton[yy*width+x]))continue;
                    next.Add(yy*width+xx);
                }
                edges[key]=next;
            }
            return edges;
        }
        for(int iteration=0;iteration<20;iteration++)
        {
            var currentEdges=Graph();var remove=new HashSet<int>();
            // Native shoreline texture can leave tiny isolated blue flecks.
            // They are not river ends or tributaries.
            var seen=new HashSet<int>();
            foreach(int root in currentEdges.Keys)
            {
                if(!seen.Add(root))continue;
                var component=new List<int>{root};
                for(int i=0;i<component.Count;i++)foreach(int next in currentEdges[component[i]])
                    if(seen.Add(next))component.Add(next);
                if(component.Count<8)remove.UnionWith(component);
            }
            foreach(int end in currentEdges.Keys.Where(p=>currentEdges[p].Count==1))
            {
                int previous=-1,current=end;var branch=new List<int>();
                while(true)
                {
                    branch.Add(current);var next=currentEdges[current].Where(p=>p!=previous).ToArray();
                    if(next.Length!=1)break;
                    previous=current;current=next[0];
                }
                if(currentEdges[current].Count>2)
                {
                    double length=0;
                    for(int i=1;i<branch.Count;i++)
                    {int dx=branch[i]%width-branch[i-1]%width,dy=branch[i]/width-branch[i-1]/width;length+=Math.Sqrt(dx*dx*2.25+dy*dy);}
                    if(length<minimumBranchLength)foreach(int p in branch.Take(branch.Count-1))remove.Add(p);
                }
            }
            if(remove.Count==0)break;
            foreach(int p in remove)skeleton[p]=false;
        }
        var edges=Graph();int ends=edges.Values.Count(p=>p.Count==1),junctions=edges.Values.Count(p=>p.Count>2);
        var boundary=new List<Point>();
        bool Wet(int x,int y)=>x>=0&&y>=0&&x<width&&y<height&&water[y*width+x];
        for(int y=0;y<height;y++)for(int x=0;x<width;x++)
            if(Wet(x,y)&&!(Wet(x-1,y)&&Wet(x+1,y)&&Wet(x,y-1)&&Wet(x,y+1)))boundary.Add(new(x*1.5,y));
        var boundaryIndex=new PointIndex(boundary);
        var widths=new Dictionary<Point,double>();
        var visited=new HashSet<(int,int)>();var witnesses=new List<Elbow>();
        foreach(int start in edges.Keys.Where(p=>edges[p].Count!=2).Order())
        foreach(int first in edges[start])
        {
            cancellation.ThrowIfCancellationRequested();
            if(visited.Contains((start,first)))continue;
            var path=new List<Point>{Physical(start)};int previous=start,current=first;
            while(true)
            {
                visited.Add((previous,current));visited.Add((current,previous));path.Add(Physical(current));
                if(edges[current].Count!=2)break;
                int next=edges[current].First(p=>p!=previous);previous=current;current=next;
            }
            Inspect(path,WidthAt,"Channel");
        }
        // A branch divides the skeleton into separate paths. Check the paths
        // through its junction too, otherwise a right-angle tributary can hide
        // its corner exactly where the individual path checks stop.
        foreach(int junction in edges.Keys.Where(p=>edges[p].Count>2))
        {
            cancellation.ThrowIfCancellationRequested();
            var arms=new List<List<Point>>();
            foreach(int first in edges[junction])
            {
                var arm=new List<Point>{Physical(junction)};
                var seen=new HashSet<int>{junction};int previous=junction,current=first;
                while(seen.Add(current))
                {
                    arm.Add(Physical(current));
                    if(edges[current].Count!=2)break;
                    int next=edges[current].First(p=>p!=previous);previous=current;current=next;
                }
                arms.Add(arm);
            }
            for(int a=0;a<arms.Count;a++)for(int b=a+1;b<arms.Count;b++)
                Inspect(arms[a].AsEnumerable().Reverse().Concat(arms[b].Skip(1)).ToArray(),WidthAt,"Junction");
        }
        var centres=edges.Keys.Select(Physical).Select(p=>(Point:p,Width:WidthAt(p))).ToArray();
        var centreIndex=new PointIndex(centres.Select(c=>c.Point).ToArray());
        var bankWidths=new Dictionary<Point,double>();
        var outline=new Dictionary<int,List<int>>();int stride=width+1;
        void Edge(int ax,int ay,int bx,int by)
        {
            int a=ay*stride+ax,b=by*stride+bx;
            if(!outline.TryGetValue(a,out var next))outline[a]=next=[];
            next.Add(b);
        }
        for(int y=0;y<height;y++)for(int x=0;x<width;x++)
        {
            if(!Wet(x,y))continue;
            if(!Wet(x,y-1))Edge(x,y,x+1,y);
            if(!Wet(x+1,y))Edge(x+1,y,x+1,y+1);
            if(!Wet(x,y+1))Edge(x+1,y+1,x,y+1);
            if(!Wet(x-1,y))Edge(x,y+1,x,y);
        }
        var traced=new HashSet<(int,int)>();
        foreach(var pair in outline)
        foreach(int first in pair.Value)
        {
            if(traced.Contains((pair.Key,first)))continue;
            int current=pair.Key,next=first;var bank=new List<Point>();
            while(traced.Add((current,next)))
            {
                bank.Add(new((current%stride-.5)*1.5,current/stride-.5));current=next;
                if(!outline.TryGetValue(current,out var choices))break;
                next=choices.FirstOrDefault(p=>!traced.Contains((current,p)),-1);
                if(next<0)break;
            }
            if(bank.Count<32||centres.Length==0)continue;
            // Include the wraparound of each closed shoreline in the same check.
            int overlap=Math.Min(bank.Count/2,100);
            var loop=bank.TakeLast(overlap).Concat(bank).Concat(bank.Take(overlap)).ToArray();
            Inspect(loop,BankWidth,"Bank");
        }
        var selected=new List<Elbow>();
        foreach(var witness in witnesses.OrderBy(p=>p.IntersectionDistance))
            if(!selected.Any(p=>new Point((p.X-witness.X)*1.5,p.Y-witness.Y).Length<witness.ChannelWidth))selected.Add(witness);
        return new(ends,junctions,selected);

        Point Physical(int key)=>new(key%width*1.5,key/width);
        double WidthAt(Point p)
        {
            if(widths.TryGetValue(p,out double value))return value;
            value=boundary.Count==0?0:2*Math.Sqrt(boundaryIndex.Nearest(p).DistanceSquared);
            widths.Add(p,value);return value;
        }
        double BankWidth(Point p)
        {
            if(bankWidths.TryGetValue(p,out double value))return value;
            value=centres[centreIndex.Nearest(p).Index].Width;
            bankWidths.Add(p,value);return value;
        }

        void Inspect(IReadOnlyList<Point> path,Func<Point,double> channelWidth,string location)
        {
            if(path.Count<16)return;
            var lengths=new double[path.Count];for(int i=1;i<path.Count;i++)lengths[i]=lengths[i-1]+(path[i]-path[i-1]).Length;
            for(int i=1;i+1<path.Count;i++)
            {
                cancellation.ThrowIfCancellationRequested();
                double channel=channelWidth(path[i]);if(channel<5)continue;
                var before=Range(-2.5*channel,-.8*channel);
                var after=Range(.8*channel,2.5*channel);
                List<Point> Range(double minimum,double maximum)
                {
                    int low=0,high=path.Count;
                    while(low<high)
                    {
                        int middle=low+(high-low)/2;
                        if(lengths[middle]-lengths[i]<minimum)low=middle+1;else high=middle;
                    }
                    var points=new List<Point>();
                    for(int j=low;j<path.Count&&lengths[j]-lengths[i]<=maximum;j++)points.Add(path[j]);
                    return points;
                }
                if(before.Count<8||after.Count<8)continue;
                channel=(channelWidth(before[before.Count/2])+channelWidth(after[after.Count/2]))/2;
                if((before[^1]-before[0]).Length<1.1*channel||(after[^1]-after[0]).Length<1.1*channel)continue;
                var (a,u,ra)=Fit(before);var (b,v,rb)=Fit(after);
                double angle=Math.Acos(Math.Clamp(u.Dot(v),-1,1))*180/Math.PI;
                if(angle<75||angle>105||Math.Max(ra,rb)>.12*channel)continue;
                var intersection=a+u*((b-a).Cross(v)/u.Cross(v));
                double offset=(intersection-path[i]).Length/channel;
                // The outside shore is farther from the tangent intersection than
                // the centre line, particularly where a square corner widens water.
                if(offset<=(location=="Bank"?.65:.4))witnesses.Add(new(path[i].X/1.5,path[i].Y,angle,channel,offset,location));
            }
        }
    }

    // Exact nearest-neighbour lookup. Original indices break equal-distance
    // ties identically to the previous ordered MinBy scan.
    private sealed class PointIndex
    {
        private sealed record Node(Point Point,int Index,int Axis,Node? Left,Node? Right);
        private readonly Node? root;
        internal PointIndex(IReadOnlyList<Point> points)
        {
            var items=points.Select((p,i)=>(Point:p,Index:i)).ToArray();root=Build(0,items.Length,0);
            Node? Build(int start,int count,int axis)
            {
                if(count==0)return null;
                Array.Sort(items,start,count,Comparer<(Point Point,int Index)>.Create((a,b)=>
                {
                    int order=(axis==0?a.Point.X.CompareTo(b.Point.X):a.Point.Y.CompareTo(b.Point.Y));
                    return order!=0?order:a.Index.CompareTo(b.Index);
                }));
                int middle=start+count/2;var item=items[middle];
                return new(item.Point,item.Index,axis,Build(start,count/2,1-axis),Build(middle+1,count-count/2-1,1-axis));
            }
        }
        internal (int Index,double DistanceSquared) Nearest(Point point)
        {
            int best=int.MaxValue;double distance=double.PositiveInfinity;Visit(root);return(best,distance);
            void Visit(Node? node)
            {
                if(node==null)return;
                var delta=node.Point-point;double squared=delta.Dot(delta);
                if(squared<distance||squared==distance&&node.Index<best){distance=squared;best=node.Index;}
                double split=node.Axis==0?point.X-node.Point.X:point.Y-node.Point.Y;
                Visit(split<=0?node.Left:node.Right);
                if(split*split<=distance)Visit(split<=0?node.Right:node.Left);
            }
        }
    }

    private static (Point Centre,Point Direction,double Residual) Fit(IReadOnlyList<Point> points)
    {
        var centre=new Point(points.Average(p=>p.X),points.Average(p=>p.Y));double xx=0,yy=0,xy=0;
        foreach(var p in points){var d=p-centre;xx+=d.X*d.X;yy+=d.Y*d.Y;xy+=d.X*d.Y;}
        double angle=.5*Math.Atan2(2*xy,xx-yy);var direction=new Point(Math.Cos(angle),Math.Sin(angle));
        if(direction.Dot(points[^1]-points[0])<0)direction=direction*-1;
        double sum=0;foreach(var p in points){double residual=direction.Cross(p-centre);sum+=residual*residual;}
        return(centre,direction,Math.Sqrt(sum/points.Count));
    }

    private static bool[] Thin(IReadOnlyList<bool> water,int width,int height,CancellationToken cancellation)
    {
        var result=water.ToArray();var remove=new List<int>();
        // Only wet pixels can survive into a skeleton. Keep their indices so
        // successive erosion passes do not repeatedly scan the surrounding land.
        var active=new List<int>();
        for(int y=1;y+1<height;y++)for(int x=1;x+1<width;x++)
            if(result[y*width+x])active.Add(y*width+x);
        for(int iteration=0;iteration<width+height;iteration++)
        {
            cancellation.ThrowIfCancellationRequested();bool changed=false;
            for(int phase=0;phase<2;phase++)
            {
                remove.Clear();
                foreach(int key in active)
                {
                    if(!result[key])continue;
                    int mask=(result[key-width]?1:0)|(result[key-width+1]?2:0)|(result[key+1]?4:0)|
                        (result[key+width+1]?8:0)|(result[key+width]?16:0)|(result[key+width-1]?32:0)|
                        (result[key-1]?64:0)|(result[key-width-1]?128:0);
                    if(ThinningRules[phase*256+mask])remove.Add(key);
                }
                if(remove.Count>0)changed=true;
                foreach(int key in remove)result[key]=false;
            }
            if(!changed)break;
            active.RemoveAll(key=>!result[key]);
        }
        return result;
    }

    private static readonly bool[] ThinningRules=CreateThinningRules();
    private static bool[] CreateThinningRules()
    {
        var rules=new bool[512];
        for(int mask=0;mask<256;mask++)
        {
            bool N(int i)=>(mask&(1<<(i&7)))!=0;
            int count=0,transitions=0;
            for(int i=0;i<8;i++){if(N(i))count++;if(!N(i)&&N(i+1))transitions++;}
            if(count<2||count>6||transitions!=1)continue;
            rules[mask]=!(N(0)&&N(2)&&N(4)||N(2)&&N(4)&&N(6));
            rules[256+mask]=!(N(0)&&N(2)&&N(6)||N(0)&&N(4)&&N(6));
        }
        return rules;
    }
}
