using System.Runtime.CompilerServices;

namespace CrystalStudio;

/// <summary>Bounded native bank paths with a protected, full-length channel.</summary>
internal static class TerrainRiverGenerator
{
    private readonly record struct Transition(int Recipe,int Shift);
    private sealed record Recipe(WaterStrip Strip, int AnchorExtent, int MaximumDy, double Cost, Transition[] Next);
    private sealed class Graph
    {
        public readonly Recipe[] Recipes;
        public readonly HashSet<(int,int)>[] Allowed;
        public Graph(TerrainWaterCatalogue catalogue,CancellationToken token)
        {
            Allowed=catalogue.Joins.Take(2).Select(js=>js.Select(j=>(j.A,j.B)).ToHashSet()).ToArray();
            // Compile the same reviewed cell constraints used by the full
            // solver into cross-section transitions once per catalogue.
            // This retains valid turns missing from the observed strip pairs.
            int At(WaterStrip s,int start,int p)=>p<start?(s.LeftWater?1:0):p>=start+s.Cells.Length?(s.RightWater?1:0):s.Cells[p-start];
            Recipes=catalogue.Strips.Select(a=>
            {
                token.ThrowIfCancellationRequested();
                var edges=new List<Transition>();
                foreach(var b in catalogue.Strips.Where(b=>a.Vertical==b.Vertical&&a.LeftWater==b.LeftWater&&a.Phase!=b.Phase))
                for(int shift=-4;shift<=4;shift++)
                {
                    if(((a.StartPhase+shift)&1)!=b.StartPhase)continue;
                    bool compatible=true;
                    for(int p=Math.Min(0,shift)-1;p<=Math.Max(a.Cells.Length,shift+b.Cells.Length);p++)
                    {
                        int sa=At(a,0,p),sb=At(b,shift,p);
                        if(!Allowed[a.Vertical?1:0].Contains((sa,sb))||a.Phase==0&&catalogue.States[sa].Water!=catalogue.States[sb].Water)
                        {compatible=false;break;}
                    }
                    if(compatible)edges.Add(new(b.Id,shift));
                }
                return new Recipe(a,a.Cells.Select((id,p)=>p+catalogue.States[id].Fragments.Max(f=>f.Dy)).Max(),
                    a.Cells.SelectMany(id=>catalogue.States[id].Fragments).Max(f=>f.Dy),-Math.Log(a.Weight)*.03,edges.ToArray());
            }).ToArray();
        }
    }
    private static readonly ConditionalWeakTable<TerrainWaterCatalogue,Graph> Graphs = new();
    private readonly record struct Placement(int Recipe,int Position);

    public static TerrainWaterPlan Plan(TerrainArea area,TerrainWaterCatalogue catalogue,TerrainOptions options,CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var graph=Graphs.GetValue(catalogue,c=>new(c,token));
        var rng=new Random(options.Seed ^ 0x51A7E);
        bool preferredVertical=rng.Next(2)==0;
        double phase=rng.NextDouble()*Math.Tau, offset=(rng.NextDouble()-.5)*.12;
        // Retry only the small bank graph. A flatter final horizontal guide
        // always has repeating original bank recipes, including complete anchors.
        foreach(double bend in new[]{1.0,.5,0.0})
        foreach(bool vertical in new[]{preferredVertical,!preferredVertical})
        {
            token.ThrowIfCancellationRequested();
            int across=vertical?area.Width:area.Height,along=vertical?area.Height:area.Width;
            double radius=Math.Min(options.WaterWidth,(across-24)*.32);
            double amplitude=Math.Min(Math.Min(across*.10,along*.06),Math.Max(0,across*.5-radius-15))*bend;
            double[] centres=Enumerable.Range(0,along).Select(t=>Math.Clamp(across*(.5+offset)
                +amplitude*(Math.Sin(t/(double)along*Math.Tau+phase)+.22*Math.Sin(t/(double)along*Math.PI*4+phase)),
                radius+12,across-radius-12)).ToArray();
            var first=Bank(false);if(first==null)continue;
            var second=Bank(true);if(second==null)continue;
            var plan=Assemble(area,catalogue,graph,vertical,first,second,token);
            if(plan!=null)return plan;

            Placement[]? Bank(bool waterBefore)
            {
                const int corridor=28;
                var recipes=graph.Recipes.Where(r=>r.Strip.Vertical==vertical&&r.Strip.LeftWater==waterBefore).ToArray();
                var local=Enumerable.Repeat(-1,graph.Recipes.Length).ToArray();
                for(int i=0;i<recipes.Length;i++)local[recipes[i].Strip.Id]=i;
                int size=recipes.Length*corridor;
                var previous=new double[size];var current=new double[size];
                Array.Fill(previous,double.PositiveInfinity);
                var history=new int[along][];
                var origins=new int[along];
                int best=-1;
                for(int t=0;t<along;t++)
                {
                    token.ThrowIfCancellationRequested();
                    double target=centres[t]+(waterBefore?radius:-radius);
                    int origin=(int)Math.Floor(target)-corridor/2;origins[t]=origin;
                    var parents=new int[size];Array.Fill(parents,-1);history[t]=parents;
                    Array.Fill(current,double.PositiveInfinity);
                    double bestCost=double.PositiveInfinity;best=-1;
                    bool Fits(Recipe r,int p)
                    {
                        var s=r.Strip;
                        if(s.Phase!=(t&1)||(p&1)!=s.StartPhase||p<4||p+s.Cells.Length>across-4)return false;
                        if(vertical)
                        {
                            if(t+r.MaximumDy>=area.Height)return false;
                        }
                        else if(p+r.AnchorExtent>=area.Height)return false;
                        // Keep both banks away from an uninterrupted water core.
                        return waterBefore?p>=centres[t]+4:p+s.Cells.Length<=centres[t]-4;
                    }
                    double Cost(Recipe r,int p)
                    {
                        double delta=p+r.Strip.Cells.Length*.5-target;
                        uint hash=unchecked((uint)(options.Seed*397+r.Strip.Id*65537+t*7919));
                        hash^=hash>>16;
                        return delta*delta*.15+r.Cost+(hash&255)*.0005;
                    }
                    void Set(int i,int p,double cost,int parent)
                    {
                        int slot=p-origin;if((uint)slot>=corridor)return;
                        var r=recipes[i];if(!Fits(r,p))return;
                        int key=i*corridor+slot;cost+=Cost(r,p);
                        if(cost>=current[key])return;
                        current[key]=cost;parents[key]=parent;
                        if(cost<bestCost){bestCost=cost;best=key;}
                    }
                    if(t==0)
                    {
                        for(int i=0;i<recipes.Length;i++)for(int p=origin;p<origin+corridor;p++)Set(i,p,0,-1);
                    }
                    else
                    {
                        for(int key=0;key<size;key++)
                        {
                            double cost=previous[key];if(double.IsPositiveInfinity(cost))continue;
                            var recipe=recipes[key/corridor];int p=origins[t-1]+key%corridor;
                            foreach(var edge in recipe.Next)
                            {
                                int i=local[edge.Recipe];if(i>=0)Set(i,p+edge.Shift,cost,key);
                            }
                        }
                    }
                    if(best<0)return null;
                    (previous,current)=(current,previous);
                }
                var result=new Placement[along];
                for(int t=along-1;t>=0;t--)
                {
                    result[t]=new(recipes[best/corridor].Strip.Id,origins[t]+best%corridor);
                    best=history[t][best];
                }
                return result;
            }
        }
        throw new InvalidOperationException("The reviewed river bank catalogue has no complete channel recipes.");
    }

    private static TerrainWaterPlan? Assemble(TerrainArea area,TerrainWaterCatalogue catalogue,Graph graph,bool vertical,
        Placement[] first,Placement[] second,CancellationToken token)
    {
        var states=new int[area.Width*area.Height];
        for(int t=0;t<first.Length;t++)
        {
            var a=catalogue.Strips[first[t].Recipe];var b=catalogue.Strips[second[t].Recipe];
            int start=first[t].Position,end=second[t].Position;
            if(start+a.Cells.Length+8>end)return null;
            int Key(int u)=>vertical?t*area.Width+u:u*area.Width+t;
            for(int u=start+a.Cells.Length;u<end;u++)states[Key(u)]=1;
            for(int p=0;p<a.Cells.Length;p++)states[Key(start+p)]=a.Cells[p];
            for(int p=0;p<b.Cells.Length;p++)states[Key(end+p)]=b.Cells[p];
        }
        var banks=new Dictionary<(int,int),TerrainBankTile>();
        for(int y=0;y<area.Height;y++)
        {
            token.ThrowIfCancellationRequested();
            for(int x=0;x<area.Width;x++)
            {
                int id=states[y*area.Width+x];var s=catalogue.States[id];
                if(x+1<area.Width&&!graph.Allowed[0].Contains((id,states[y*area.Width+x+1]))
                    ||y+1<area.Height&&!graph.Allowed[1].Contains((id,states[(y+1)*area.Width+x]))
                    ||s.Water!=catalogue.States[states[(y&~1)*area.Width+(x&~1)]].Water)return null;
                foreach(var f in s.Fragments)
                {
                    var bank=new TerrainBankTile(area.X+x,area.Y+y+f.Dy,f.Library,f.Image);
                    if(banks.TryGetValue((bank.X,bank.Y),out var old)&&old!=bank)return null;
                    banks[(bank.X,bank.Y)]=bank;
                }
            }
        }
        var plan=new TerrainWaterPlan(area,states,banks.Values.OrderBy(b=>b.Y).ThenBy(b=>b.X).ToArray(),catalogue,vertical);
        return TerrainWaterGenerator.HasCompleteSprites(plan)?plan:null;
    }
}
