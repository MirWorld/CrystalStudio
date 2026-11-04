namespace CrystalStudio;

/// <summary>Plans a whole native bank cross-section at each step, using measured shifts.</summary>
internal static class TerrainWaterStrips
{
    private readonly record struct Key(int Strip, int Position);
    private readonly record struct Step(double Cost, Key Previous);
    private const int BeamWidth=512;
    private static Dictionary<Key,Step> Beam(Dictionary<Key,Step> candidates)
    {
        if(candidates.Count<=BeamWidth)return candidates;
        // A straight native cycle can have hundreds of cheap positions near
        // the first guide points. Preserve every currently viable recipe so
        // its diagonal or turning continuation survives until it is needed.
        var bestByRecipe=new Dictionary<int,KeyValuePair<Key,Step>>();
        foreach(var candidate in candidates)
            if(!bestByRecipe.TryGetValue(candidate.Key.Strip,out var best)||candidate.Value.Cost<best.Value.Cost)
                bestByRecipe[candidate.Key.Strip]=candidate;
        var result=bestByRecipe.Values.OrderBy(p=>p.Value.Cost).Take(BeamWidth).ToDictionary();
        foreach(var candidate in candidates.OrderBy(p=>p.Value.Cost).Take(BeamWidth))
        {
            if(result.Count==BeamWidth)break;
            result.TryAdd(candidate.Key,candidate.Value);
        }
        return result;
    }
    public static TerrainWaterPlan? Plan(TerrainArea area,TerrainWaterCatalogue catalogue,TerrainOptions options,bool vertical,Func<int,double> profile,CancellationToken token,
        bool waterBefore=false,Func<int,double>? radiusLeft=null,Func<int,double>? radiusRight=null,bool allowOutside=false)
    {
        int across=vertical?area.Width:area.Height,along=vertical?area.Height:area.Width;
        var recipes=catalogue.Strips.Where(s=>s.Vertical==vertical&&s.LeftWater!=s.RightWater).ToDictionary(s=>s.Id);
        var edges=catalogue.StripJoins.Where(j=>recipes.ContainsKey(j.A)&&recipes.ContainsKey(j.B)).GroupBy(j=>j.A).ToDictionary(g=>g.Key,g=>g.ToArray());
        bool outside=allowOutside||options.WaterMode==TerrainWaterMode.RiverToSea;
        var maximumDy=recipes.ToDictionary(p=>p.Key,p=>p.Value.Cells.SelectMany(s=>catalogue.States[s].Fragments).Max(f=>f.Dy));
        // A cheap prefix can otherwise enter a repeating bank family that has
        // no complete sprite ending at the map's bottom. Keep only recipes
        // with a native continuation through every remaining row or column.
        var viable=new bool[along][];
        for(int t=along-1;t>=0;t--)
        {
            token.ThrowIfCancellationRequested();
            viable[t]=new bool[catalogue.Strips.Count];
            foreach(var recipe in recipes.Values)
            {
                if(recipe.Phase!=t%2||vertical&&t+maximumDy[recipe.Id]>=area.Height)continue;
                viable[t][recipe.Id]=t==along-1||edges.TryGetValue(recipe.Id,out var next)&&next.Any(e=>viable[t+1][e.B]);
            }
        }
        Key[]? Bank(bool leftWater,Func<int,double> target)
        {
            var targets=Enumerable.Range(0,along).Select(target).ToArray();
            bool Fits(WaterStrip recipe,int position,int t)
            {
                if(!viable[t][recipe.Id]||(position&1)!=recipe.StartPhase||recipe.Phase!=t%2)return false;
                if(outside?position < -24||position > across+24:position<1||position+recipe.Cells.Length>=across)return false;
                for(int p=0;p<recipe.Cells.Length;p++)
                    if(position+p>=0&&position+p<across&&catalogue.States[recipe.Cells[p]].Fragments.Any(f=>(vertical?t:position+p)+f.Dy>=area.Height))return false;
                return true;
            }
            double Cost(WaterStrip recipe,int position,int t)
            {
                double centre=position+recipe.Cells.Length*.5;
                double delta=centre-targets[t];
                double jitter=Math.Sin(recipe.Id*19+t*13+position*11+options.Seed*.071)*.8;
                return delta*delta*.1-Math.Log(Math.Max(1,recipe.Weight))*.08+jitter+1;
            }
            var history=new List<Dictionary<Key,Step>>(along);
            var first=new Dictionary<Key,Step>();
            foreach(var recipe in recipes.Values.Where(s=>s.LeftWater==leftWater&&s.Phase==0))
                for(int p=outside?-24:1;p<(outside?across+25:across-recipe.Cells.Length);p++)
                    if(Fits(recipe,p,0))first[new(recipe.Id,p)]=new(Cost(recipe,p,0),default);
            history.Add(Beam(first));
            for(int t=1;t<along;t++)
            {
                token.ThrowIfCancellationRequested();
                var current=new Dictionary<Key,Step>();
                foreach(var (previous,step) in history[^1])
                {
                    if(!edges.TryGetValue(previous.Strip,out var next))continue;
                    foreach(var edge in next)
                    {
                        var recipe=recipes[edge.B];int position=previous.Position+edge.Shift;
                        if(recipe.LeftWater!=leftWater||!Fits(recipe,position,t))continue;
                        var key=new Key(edge.B,position);double cost=step.Cost+Cost(recipe,position,t);
                        if(!current.TryGetValue(key,out var existing)||cost<existing.Cost)current[key]=new(cost,previous);
                    }
                }
                if(current.Count==0){System.Diagnostics.Trace.WriteLine($"Bank strip {leftWater}: no continuation at {t}");return null;}
                history.Add(Beam(current));
            }
            if(history[^1].Count==0)return null;
            Key chosen=history[^1].MinBy(p=>p.Value.Cost).Key;var result=new Key[along];
            for(int t=along-1;t>=0;t--){result[t]=chosen;chosen=history[t][chosen].Previous;}
            var visible=Enumerable.Range(0,along).Where(t=>targets[t]>=-8&&targets[t]<=across+8).ToArray();
            if(visible.Length>0)
            {
                double Centre(int t)=>result[t].Position+recipes[result[t].Strip].Cells.Length*.5;
                double rmse=Math.Sqrt(visible.Average(t=>Math.Pow(Centre(t)-targets[t],2)));
                double guideSpan=visible.Max(t=>targets[t])-visible.Min(t=>targets[t]);
                double actualSpan=visible.Max(Centre)-visible.Min(Centre);
                double tolerance=Math.Max(6,across*.07);
                // Native banks are several cells wide and have discrete turns;
                // reject a flattened broad guide without demanding exact pixels.
                if(rmse>tolerance||guideSpan>Math.Max(12,across*.15)&&actualSpan<guideSpan*.5)
                {
                    System.Diagnostics.Trace.WriteLine($"Bank strip {leftWater}: guide mismatch, RMSE {rmse:F1}/{tolerance:F1}, span {actualSpan:F1}/{guideSpan:F1}");
                    return null;
                }
            }
            return result;
        }
        double radius=Math.Min(options.WaterWidth,across*.27);
        double Radius(int t)=>options.WaterMode==TerrainWaterMode.RiverToSea?radius+(across*(.5+options.WaterCoverage*.125)-radius)*Math.Clamp(1-t/(along*(.65+options.WaterCoverage*.6)),0,1):radius;
        Func<int,double> leftRadius=radiusLeft??Radius,rightRadius=radiusRight??Radius;
        Key[]? first=Bank(options.WaterMode==TerrainWaterMode.Beach&&waterBefore,t=>profile(t)-(options.WaterMode==TerrainWaterMode.Beach?0:leftRadius(t)));
        Key[]? second=options.WaterMode==TerrainWaterMode.Beach?null:Bank(true,t=>profile(t)+rightRadius(t));
        if(first==null||options.WaterMode!=TerrainWaterMode.Beach&&second==null){System.Diagnostics.Trace.WriteLine("Bank strips: incomplete path");return null;}
        var selected=new int[area.Width*area.Height];
        for(int t=0;t<along;t++)
        {
            var a=recipes[first[t].Strip];int start=first[t].Position;
            int waterStart=second==null&&waterBefore?0:start+a.Cells.Length;
            int waterEnd=second==null?(waterBefore?start:across):second[t].Position;
            if(second!=null&&start+a.Cells.Length+2>waterEnd){System.Diagnostics.Trace.WriteLine($"Bank strips overlap at {t}");return null;}
            for(int u=Math.Max(0,waterStart);u<Math.Min(across,waterEnd);u++)selected[vertical?t*area.Width+u:u*area.Width+t]=1;
            void Place(Key key)
            {
                var row=recipes[key.Strip].Cells;
                for(int p=0;p<row.Length;p++){int u=key.Position+p;if(u>=0&&u<across)selected[vertical?t*area.Width+u:u*area.Width+t]=row[p];}
            }
            Place(first[t]);if(second!=null)Place(second[t]);
        }
        var banks=new Dictionary<(int,int),TerrainBankTile>();
        bool hasLand=false,hasWater=false;
        var allowed=catalogue.Joins.Take(2).Select(js=>js.Select(j=>(j.A,j.B)).ToHashSet()).ToArray();
        for(int y=0;y<area.Height;y++)for(int x=0;x<area.Width;x++)
        {
            int id=selected[y*area.Width+x];var state=catalogue.States[id];
            hasWater|=state.Water;hasLand|=!state.Water;
            if(x+1<area.Width&&!allowed[0].Contains((id,selected[y*area.Width+x+1]))||y+1<area.Height&&!allowed[1].Contains((id,selected[(y+1)*area.Width+x])))
            {System.Diagnostics.Trace.WriteLine($"Bank strips: unreviewed composed seam at {x},{y}");return null;}
            if(state.Water!=catalogue.States[selected[(y&~1)*area.Width+(x&~1)]].Water){System.Diagnostics.Trace.WriteLine($"Bank strips split ground at {x},{y}");return null;}
            foreach(var f in state.Fragments)
            {
                var tile=new TerrainBankTile(area.X+x,area.Y+y+f.Dy,f.Library,f.Image);var key=(tile.X,tile.Y);
                if(tile.X<area.X||tile.X>=area.X+area.Width||tile.Y<area.Y||tile.Y>=area.Y+area.Height)return null;
                if(banks.TryGetValue(key,out var existing)&&existing!=tile)return null;
                banks[key]=tile;
            }
        }
        if(banks.Count==0||!hasLand||!hasWater)return null;
        var plan=new TerrainWaterPlan(area,selected,banks.Values.OrderBy(b=>b.Y).ThenBy(b=>b.X).ToList(),catalogue,vertical);
        if(!TerrainWaterGenerator.HasCompleteSprites(plan)){System.Diagnostics.Trace.WriteLine("Bank strips: incomplete sprites");return null;}return plan;
    }
}
