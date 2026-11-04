namespace CrystalStudio;

/// <summary>Procedural channel outlines assembled from small saved native pieces.
/// Lengths are solved together so both banks close without scaling artwork.
/// This research builder exports no whole-river stamp or fitting scenery.</summary>
public static class RiverPieceRiverBuilder
{
    public sealed record Result(RiverCoastPieces.Chain Chain,int InnerVertical,int OuterBroad,int OuterNarrow,
        int EntryLength,int TopLength,int InnerReturn,int OuterReturn,int OuterDiagonal)
    {
        public int ClosingLength { get; init; }
    }
    public sealed record SuccessiveResult(RiverCoastPieces.Chain Chain,int EntryLength,int FirstVertical,int SecondVertical,
        int FirstDiagonal,int SecondDiagonal,int FirstReturn,int SecondReturn,int ClosingLength);
    public sealed record BendPair(IReadOnlyList<(string Id,bool Reverse)> Upper,IReadOnlyList<(string Id,bool Reverse)> Lower);

    public sealed record WindingResult(RiverCoastPieces.Chain Chain,string Shape)
    {
        public RiverPieceGroundAssembler.Result? PreparedGround { get; init; }
        public int BranchCount=>Chain.Pieces.Count(p=>p.Piece.Id is "diagonal-fork-concave" or "tributary-south-join");
    }

    public static WindingResult? ResearchBranching(RiverCoastPieces catalogue,int seed,int maximumWidth,int maximumHeight,
        out string failure,CancellationToken cancellation=default,bool verifyGround=true)
    {
        if(catalogue.Pieces.Any(p=>p.Id=="west-diagonal-parallel-8-9"))
        {
            // Start with a seeded course, then grow limbs at compatible reaches.
            // Every addition is assembled and checked before it replaces the main river.
            var (mainWidth,mainHeight)=RiverSeed.MainArea(seed,maximumWidth,maximumHeight);
            var varied=ResearchWinding(catalogue,seed,mainWidth,mainHeight,out failure,cancellation);
            if(varied!=null)
            {
                int desired=DesiredBranchCount(seed,maximumWidth,maximumHeight,true);
                for(int branch=0;branch<desired;branch++)
                {
                    var next=RiverBranchGrowth.Attach(catalogue,varied.Chain,unchecked(seed+branch*104729),
                        maximumWidth,maximumHeight,out failure,cancellation);
                    if(next==null)break;
                    varied=next with{Shape=varied.Shape+"-"+next.Shape};
                }
                if(varied.BranchCount>0)return varied;
            }
            // The two-group fork currently leaves small disconnected land
            // pockets in its alternate ground phase. Do not retry that known
            // rejected layout during every interactive preview.
            if(maximumWidth>=640&&maximumHeight>=314)
            {
                var longer=RiverBankOnlyWindingBuilder.Build(catalogue,seed,maximumWidth-48,maximumHeight-54,
                    out failure,cancellation,bendRepeats:3,northwardPeriods:[1,1,1]);
                if(longer!=null)longer=RiverUniformWidthBuilder.Build(catalogue,longer,seed,maximumWidth,maximumHeight,out failure,cancellation);
                if(longer!=null)
                {
                    var forked=RiverBranchGrowth.Attach(catalogue,longer.Chain,seed,maximumWidth,maximumHeight,out failure,cancellation);
                    if(forked!=null)return forked with{Shape=$"{longer.Shape}-{forked.Shape}"};
                }
            }
            var main=RiverBankOnlyWindingBuilder.Build(catalogue,seed,maximumWidth-48,maximumHeight-24,out failure,cancellation);
            if(main!=null)main=RiverUniformWidthBuilder.Build(catalogue,main,seed,maximumWidth,maximumHeight,out failure,cancellation);
            if(main!=null)
            {
                var attached=RiverBranchGrowth.Attach(catalogue,main.Chain,seed,maximumWidth,maximumHeight,out failure,cancellation);
                if(attached!=null)return attached with{Shape=$"{main.Shape}-{attached.Shape}"};
            }
        }
        if(catalogue.Pieces.Any(p=>p.Id=="diagonal-fork-concave"))
            return ResearchDiagonalFork(catalogue,seed,maximumWidth,maximumHeight,out failure,cancellation);
        if(verifyGround&&new Random(seed^0x17498A3).Next(3)==0)
        {
            var wave=ResearchWaveTributary(catalogue,seed,maximumWidth,maximumHeight,out failure,cancellation);
            if(wave!=null)return wave;
            var curvedMain=ResearchCurvedTributary(catalogue,seed,maximumWidth,maximumHeight,out failure,cancellation);
            if(curvedMain!=null)return curvedMain;
        }
        if(catalogue.Pieces.Any(p=>p.Id=="west-south-water-join")&&new Random(seed^0x2197403).Next(4)!=0)
        {
            var curved=ResearchTributary(catalogue,seed,maximumWidth,maximumHeight,out failure,cancellation,meander:true,verifyGround:verifyGround,curvedArm:true);
            if(curved!=null)return curved;
        }
        return ResearchTributary(catalogue,seed,maximumWidth,maximumHeight,out failure,cancellation,meander:true,verifyGround:verifyGround)
            ??ResearchTributary(catalogue,seed,maximumWidth,maximumHeight,out failure,cancellation,verifyGround:verifyGround);
    }

    internal static int DesiredBranchCount(int seed,int width,int height,bool required)
    {
        int maximum=Math.Clamp(Math.Min(width/110,height/65),1,5);
        return new Random(seed^0x2143AB).Next(required?1:0,maximum+1);
    }

    /// <summary>Add a limb at a matched reach using two saved junctions and ordinary bend pieces.</summary>
    public static WindingResult? ResearchAttachDiagonalFork(RiverCoastPieces catalogue,RiverCoastPieces.Chain source,int seed,
        int maximumWidth,int maximumHeight,out string failure,CancellationToken cancellation=default,int parentDepth=0,int parentBranchId=0,Action<RiverPieceGroundAssembler.Result,RiverShapeQuality.Report>? inspect=null,
        string? preferredCourse=null)
    {
        failure="No complete native tributary fits the available reaches.";
        var pieces=catalogue.Pieces.ToDictionary(p=>p.Id);var original=source.Pieces;
        var random=new Random(seed^0x174F38);
        int existing=original.Count(p=>p.Piece.Id is "diagonal-fork-concave" or "tributary-south-join");
        // Match horizontal banks by their physical reach, not by a fixed
        // piece index. This permits tributaries between later meanders too.
        var sites=new List<(int Lower,int Upper)>();
        for(int i=1;i<original.Count;i++)
        {
            var top=original[i];
            if(top.BranchId!=parentBranchId||top.BranchDepth!=parentDepth||top.Piece.Id!="north-straight"||top.Reverse||original[i-1].Piece.Id=="north-straight")continue;
            if(parentDepth>0&&original[i-1].Piece.Id=="west-north-offset-join")continue;
            // Nearby junctions are allowed when their actual banks and water
            // remain separate; a piece-count exclusion discarded long usable reaches.
            int lower=Enumerable.Range(0,i).Where(j=>original[j].BranchId==parentBranchId&&original[j].BranchDepth==parentDepth&&original[j].Piece.Id=="south-straight"&&original[j].Reverse
                &&original[j].Y-top.Y is >=20 and <=40&&Math.Abs(original[j].X-top.X)<32)
                .OrderBy(j=>Math.Abs(original[j].X-top.X)).FirstOrDefault(-1);
            if(lower>=0)sites.Add((lower,i));
        }
        if(sites.Count==0)return null;
        var routes=RiverLimbBends.Courses(catalogue,seed,maximumWidth,maximumHeight,parentDepth,cancellation);
        int groundAttempts=0;string lastGroundFailure=failure;
        var routeAttempts=new Dictionary<string,int>();var routeGroundAttempts=new Dictionary<string,int>();
        var orderedSites=sites.OrderBy(_=>random.Next()).ToArray();
        foreach(var route in routes.Where(r=>preferredCourse==null||r.Name==preferredCourse)
            .OrderBy(r=>RiverLimbBends.Repetitions(catalogue,source,r)))
        foreach(int padding in new[]{0,2,4})
        foreach(var site in orderedSites)
        {
            if(routeAttempts.GetValueOrDefault(route.Name)>=12||routeGroundAttempts.GetValueOrDefault(route.Name)>=2)continue;
            int lower=site.Lower,upper=site.Upper;
            int run=original.Skip(upper).TakeWhile(p=>p.Piece.Id=="north-straight"&&!p.Reverse).Count();
            // Use an existing reach first. Any extra room is added to both
            // shores in matching 24-cell increments.
            int minimumExtra=Math.Max(0,((4-run+1)/2)*2);
            int extraNorth=minimumExtra+padding;
            int extraSouth=extraNorth/2*3;
            var extended=new List<(string Id,bool Reverse,int Depth,int Branch)>();
            for(int i=0;i<original.Count;i++)
            {
                if(i==lower)extended.AddRange(Enumerable.Repeat(("south-straight",true,parentDepth,parentBranchId),extraSouth));
                if(i==upper)extended.AddRange(Enumerable.Repeat(("north-straight",false,parentDepth,parentBranchId),extraNorth));
                extended.Add((original[i].Piece.Id,original[i].Reverse,original[i].BranchDepth,original[i].BranchId));
            }
            foreach(int offset in Enumerable.Range(0,run+extraNorth-3).OrderBy(_=>random.Next()).Take(3))
            {
                cancellation.ThrowIfCancellationRequested();
                routeAttempts[route.Name]=routeAttempts.GetValueOrDefault(route.Name)+1;
                var steps=new List<(string Id,bool Reverse,int Depth,int Branch)>(extended);
                int start=upper+extraSouth+offset;
                if(steps.Count<start+4||!steps.Skip(start).Take(4).All(p=>p.Id=="north-straight"&&!p.Reverse))continue;
                steps.RemoveRange(start,4);
                steps.InsertRange(start,route.Steps.Select(p=>(p.Id,p.Reverse,parentDepth+1,original.Max(p=>p.BranchId)+1)));
                var placed=new List<RiverCoastPieces.Placement>{original[0]};bool valid=true;
                foreach(var step in steps.Skip(1))
                {
                    var previous=placed[^1];var link=catalogue.FindConnection(previous.Piece.Id,previous.Reverse,step.Id,step.Reverse);
                    if(link==null||!pieces.TryGetValue(step.Id,out var piece)){valid=false;break;}
                    placed.Add(new(piece,previous.X+link.Dx,previous.Y+link.Dy,step.Reverse){BranchDepth=step.Depth,BranchId=step.Branch});
                }
                if(!valid)continue;var chain=catalogue.ResearchAssemble(placed,out failure);
                if(chain is not {Closed:true})continue;
                var bounds=RiverPieceGroundAssembler.Measure(catalogue,chain);
                if(bounds.Width>maximumWidth||bounds.Height>maximumHeight||(long)bounds.Width*bounds.Height*96>32_000_000)
                {failure="The complete native fork and winding banks do not fit this area.";continue;}
                if(++groundAttempts>8){failure=lastGroundFailure;return null;}
                routeGroundAttempts[route.Name]=routeGroundAttempts.GetValueOrDefault(route.Name)+1;
                var result=RiverBranchAssembly.Build(catalogue,chain,seed,existing+1,
                    $"fork-reach-{upper}-offset-{offset}-depth-{parentDepth+1}-{route.Name}",out failure,cancellation,inspect);
                if(result!=null)return result;
                lastGroundFailure=failure;
                if(routeGroundAttempts[route.Name]>=2)break;
            }
        }
        failure=lastGroundFailure;
        return null;
    }

    /// <summary>Assemble a three-ended channel from two saved junctions and ordinary bend pieces.</summary>
    public static WindingResult? ResearchDiagonalFork(RiverCoastPieces catalogue,int seed,int maximumWidth,int maximumHeight,
        out string failure,CancellationToken cancellation=default)
    {
        failure="No gradual native tributary fits this area.";
        var byId=catalogue.Pieces.ToDictionary(p=>p.Id);
        if(!byId.ContainsKey("diagonal-fork-concave")||!byId.ContainsKey("diagonal-fork-return")
            ||!byId.ContainsKey("diagonal-gentle-cap"))return null;
        var random=new Random(seed^0x713843);
        int preferred=maximumWidth>=500?random.Next(2,4):maximumWidth>=340?2:1;
        // Vary the matched upstream/downstream reaches together. Their native
        // offsets keep the two shores closed while changing each bend's position.
        var directions=GentleDirections.Where(p=>p.Pattern.Length+1==preferred
            &&p.Width<=maximumWidth-48&&p.Height<=maximumHeight-32).ToArray();
        var routes=new List<(int Sweep,int[]? Pattern)>();
        if(directions.Length>0)routes.Add((preferred,directions[random.Next(directions.Length)].Pattern));
        routes.AddRange(new[]{preferred,3,2,1}.Distinct().Select(s=>(s,(int[]?)null)));
        foreach(var route in routes)
        foreach(int sourceSeed in new[]{seed,seed+7919,0,1,2,3,4,5,6,7}.Distinct())
        {
            cancellation.ThrowIfCancellationRequested();
            // Only assemble an outline here. Analyse the completed fork once,
            // after its small bank joins replace the matching straight reach.
            var main=ResearchDiagonalSweep(catalogue,sourceSeed,maximumWidth-48,maximumHeight,4,out failure,cancellation,
                diagonalEnd:true,capRoute:[("diagonal-gentle-cap",false)],sweepCount:route.Sweep,verifyFinalGround:false,
                northwardPeriods:route.Pattern);
            if(main==null)continue;
            var source=main.Chain.Pieces;int turn=source.ToList().FindIndex(p=>p.Piece.Id=="west-north-offset-join"&&!p.Reverse);
            int lower=turn-3,upper=turn+1;
            while(upper<source.Count&&source[upper].Piece.Id=="north-straight"&&!source[upper].Reverse)upper++;
            if(lower<1||source[lower].Piece.Id!="south-west-turn-entry"||upper<=turn+1)continue;
            var steps=new List<(string Id,bool Reverse)>();
            for(int i=0;i<source.Count;i++)
            {
                if(i==lower)steps.AddRange(Enumerable.Repeat(("south-straight",true),6));
                if(i==upper)steps.AddRange(Enumerable.Repeat(("north-straight",false),4));
                steps.Add((source[i].Piece.Id,source[i].Reverse));
            }
            var unbranchedSteps=steps;int preferredOffset=1+random.Next(2);
            foreach(int offset in new[]{preferredOffset,3-preferredOffset})
            {
                steps=new(unbranchedSteps);
                int start=turn+1+6+offset;
                if(!steps.Skip(start).Take(4).All(p=>p.Id=="north-straight"&&!p.Reverse)||steps.Count<start+4)continue;
                steps.RemoveRange(start,4);
                (string Id,bool Reverse)[] fork=[("diagonal-fork-concave",false),
                    ..Enumerable.Repeat(("west-diagonal",true),4),
                    ("west-diagonal-exit",false),("west-north-offset-join",false),
                    ("native-1377-1379-15-6",false),("native-1379-1382-10-2",false),
                    ("east-turn-entry",false),("east-diagonal-entry",false),
                    ..Enumerable.Repeat(("east-diagonal",false),4),("diagonal-fork-return",false)];
                steps.InsertRange(start,fork);
                var placed=new List<RiverCoastPieces.Placement>{source[0]};bool valid=true;
                foreach(var step in steps.Skip(1))
                {
                    var previous=placed[^1];var link=catalogue.FindConnection(previous.Piece.Id,previous.Reverse,step.Id,step.Reverse);
                    if(link==null){valid=false;break;}
                    placed.Add(new(byId[step.Id],previous.X+link.Dx,previous.Y+link.Dy,step.Reverse));
                }
                if(!valid)continue;
                var chain=catalogue.ResearchAssemble(placed,out failure);
                if(chain is not {Closed:true})continue;
                var bounds=RiverPieceGroundAssembler.Measure(catalogue,chain);
                if(bounds.Width>maximumWidth||bounds.Height>maximumHeight){failure="The native tributary banks do not fit this area.";continue;}
                try
                {
                    var ground=RiverPieceGroundAssembler.Assemble(catalogue,chain,seed,cancellation,verifyCovers:true,verifyChannel:true);
                    if(!RiverShapeQuality.Analyse(ground,cancellation).GentleTributaryChannel)
                    {failure="No three-ended native tributary without square bends is available for these settings.";continue;}
                    string directionsLabel=route.Pattern==null?"":"-northward-"+string.Join('-',route.Pattern);
                    return new(chain,$"diagonal-fork-sweeps-{route.Sweep}{directionsLabel}-source-{sourceSeed}-offset-{offset}"){PreparedGround=ground};
                }
                catch(System.IO.InvalidDataException error){failure=error.Message;}
            }
        }
        return null;
    }

    public static WindingResult? ResearchWaveTributary(RiverCoastPieces catalogue,int seed,int maximumWidth,int maximumHeight,
        out string failure,CancellationToken cancellation=default)
    {
        failure="No compatible native wave tributary fits this area.";
        // Reserve space for the small curved arm instead of repeatedly trying
        // a main channel that already occupies the available area.
        for(int trial=0;trial<2;trial++)
        {
            cancellation.ThrowIfCancellationRequested();
            var main=ResearchWave(catalogue,seed+trial*7919,maximumWidth-48,maximumHeight-48,out failure,cancellation,shortHead:true);
            if(main==null)continue;
            var branch=ResearchTributary(catalogue,seed,maximumWidth,maximumHeight,out failure,cancellation,
                curvedArm:true,baseChain:main.Chain,compactCurvedArm:true,shortCurvedArm:true,
                verifyChannel:true,baseShape:main.Shape,curvedArmReturn:"west-south-offset-join");
            if(branch!=null)return branch;
        }
        return null;
    }

    public static WindingResult? ResearchGentleTributary(RiverCoastPieces catalogue,RiverCoastPieces.Chain source,int seed,
        int maximumWidth,int maximumHeight,int armPeriods,out string failure,CancellationToken cancellation=default,bool verifyChannel=true)
    {
        if(armPeriods is not (1 or 3))throw new ArgumentOutOfRangeException(nameof(armPeriods));
        failure="The gentle tributary needs four matching narrow-bank periods.";
        var pieces=catalogue.Pieces.ToDictionary(p=>p.Id);var original=source.Pieces;
        var cap=pieces.ContainsKey("diagonal-gentle-cap")?new[]{("diagonal-gentle-cap",false)}:
            new[]{("native-1384-1385-4-7",false),("east-narrow-entry",false),("native-1385-1393-1-m9",true),
                ("native-1385-1386-m1-9",false),("south-cap-exit",false),("south-west-turn-entry",false),("west-diagonal-entry",false)};
        for(int start=1;start+4<=original.Count;start++)
        {
            if(!original.Skip(start).Take(4).All(p=>p.Piece.Id=="east-narrow-straight"&&!p.Reverse))continue;
            var placed=original.Take(start).ToList();
            bool ok=Append("tributary-north-join")&&Repeat("north-straight",false,armPeriods)
                &&Append("native-1377-1379-15-6")&&Append("native-1379-1382-10-2")
                &&Append("native-1382-1384-4-7")&&Repeat("east-diagonal",false,3);
            foreach(var step in cap)if(ok)ok=Append(step.Item1,step.Item2);
            ok=ok&&Repeat("west-diagonal",true,2)&&Append("west-diagonal-exit")
                &&Append("west-concave-entry")&&Append("west-south-offset-join")
                &&Repeat("south-straight",true,(5+3*armPeriods)/2)&&Append("tributary-south-join");
            if(!ok)continue;
            foreach(var next in original.Skip(start+4))if(!Append(next.Piece.Id,next.Reverse)){ok=false;break;}
            if(!ok)continue;
            var chain=catalogue.ResearchAssemble(placed,out failure);
            if(chain is not {Closed:true})continue;
            var bounds=RiverPieceGroundAssembler.Measure(catalogue,chain);
            if(bounds.Width>maximumWidth||bounds.Height>maximumHeight){failure="The gentle arm does not fit the requested area.";continue;}
            try
            {
                var ground=RiverPieceGroundAssembler.Assemble(catalogue,chain,seed,cancellation,verifyCovers:true,verifyChannel:verifyChannel);
                return new(chain,"gentle-diagonal-main-gentle-tributary"){PreparedGround=ground};
            }
            catch(System.IO.InvalidDataException error){failure=error.Message;}

            bool Repeat(string id,bool reverse,int count)
            {for(int i=0;i<count;i++)if(!Append(id,reverse))return false;return true;}
            bool Append(string id,bool reverse=false)
            {
                var previous=placed[^1];var links=catalogue.Links.Where(l=>l.FromId==previous.Piece.Id&&l.FromReverse==previous.Reverse&&l.ToId==id&&l.ToReverse==reverse).ToArray();
                if(links.Length!=1||!pieces.TryGetValue(id,out var next))return false;
                var link=links[0];placed.Add(new(next,previous.X+link.Dx,previous.Y+link.Dy,reverse));return true;
            }
        }
        return null;
    }

    /// <summary>Research a saved diagonal fork on the paired bridge's short horizontal reach.</summary>
    public static WindingResult? ResearchBridgeDiagonalFork(RiverCoastPieces catalogue,RiverCoastPieces.Chain source,int seed,
        int maximumWidth,int maximumHeight,out string failure,CancellationToken cancellation=default,int extraPeriods=0,int forkOffset=0)
    {
        if(extraPeriods is not (0 or 2 or 4)||forkOffset<0||forkOffset>extraPeriods)throw new ArgumentOutOfRangeException(nameof(extraPeriods));
        failure="The bridge needs a matching pair of horizontal bank reaches.";
        var byId=catalogue.Pieces.ToDictionary(p=>p.Id);var original=source.Pieces;
        for(int start=1;start+2<=original.Count;start++)
        {
            cancellation.ThrowIfCancellationRequested();
            if(!original.Skip(start).Take(2).All(p=>p.Piece.Id=="north-straight"&&!p.Reverse))continue;
            var steps=original.Select(p=>(Id:p.Piece.Id,p.Reverse)).ToList();
            // The fork advances 48 cells instead of the original 24. Three
            // eight-cell opposite-bank continuations preserve the exact closure.
            int opposite=steps.FindIndex(start+2,p=>p.Id=="south-straight"&&p.Reverse);
            if(opposite<0)continue;
            steps.InsertRange(opposite,Enumerable.Repeat(("south-straight",true),3+extraPeriods*3/2));
            steps.InsertRange(start,Enumerable.Repeat(("north-straight",false),extraPeriods));
            int forkStart=start+forkOffset;
            steps.RemoveRange(forkStart,2);
            (string Id,bool Reverse)[] fork=[("diagonal-fork-concave",false),
                ..Enumerable.Repeat(("west-diagonal",true),4),
                ("west-diagonal-exit",false),("west-north-offset-join",false),
                ("native-1377-1379-15-6",false),("native-1379-1382-10-2",false),
                ("east-turn-entry",false),("east-diagonal-entry",false),
                ..Enumerable.Repeat(("east-diagonal",false),4),("diagonal-fork-return",false)];
            if(fork.Any(p=>!byId.ContainsKey(p.Id)))return null;
            steps.InsertRange(forkStart,fork);
            if(byId.ContainsKey("research-north-fork-entry"))
            {
                if(forkStart<1||steps[forkStart-1]!=("north-straight",false))
                {failure="The saved fork entry needs an incoming north-bank period.";continue;}
                steps[forkStart-1]=("research-north-fork-entry",false);
            }
            var placed=new List<RiverCoastPieces.Placement>{original[0]};bool valid=true;
            foreach(var step in steps.Skip(1))
            {
                var previous=placed[^1];var link=catalogue.FindConnection(previous.Piece.Id,previous.Reverse,step.Id,step.Reverse);
                if(link==null){valid=false;break;}
                placed.Add(new(byId[step.Id],previous.X+link.Dx,previous.Y+link.Dy,step.Reverse));
            }
            if(!valid)continue;
            var chain=catalogue.ResearchAssemble(placed,out failure);
            if(chain is not {Closed:true})continue;
            var bounds=RiverPieceGroundAssembler.Measure(catalogue,chain);
            if(bounds.Width>maximumWidth||bounds.Height>maximumHeight){failure="The saved bridge tributary does not fit this area.";continue;}
            try
            {
                var ground=RiverPieceGroundAssembler.Assemble(catalogue,chain,seed,cancellation,verifyCovers:true,verifyChannel:true);
                return new(chain,$"bridge-saved-diagonal-fork-periods-{extraPeriods}-offset-{forkOffset}"){PreparedGround=ground};
            }
            catch(System.IO.InvalidDataException error){failure=error.Message;}
        }
        return null;
    }

    public static WindingResult? ResearchNorthwardGentleTributary(RiverCoastPieces catalogue,RiverCoastPieces.Chain source,int seed,
        int maximumWidth,int maximumHeight,int verticalPeriods,out string failure,CancellationToken cancellation=default,bool extendHead=false)
    {
        if(verticalPeriods is not (0 or 2 or 4))throw new ArgumentOutOfRangeException(nameof(verticalPeriods));
        int replaced=2+verticalPeriods/2;
        failure="The northward arm needs a matching horizontal bank reach.";
        var pieces=catalogue.Pieces.ToDictionary(p=>p.Id);var original=source.Pieces;
        if(extendHead)
        {
            var head=original.ToList();int turn=head.FindIndex(p=>p.Piece.Id=="west-north-offset-join"&&!p.Reverse);
            int lower=turn-3,upper=turn+1;
            while(upper<head.Count&&head[upper].Piece.Id=="north-straight"&&!head[upper].Reverse)upper++;
            if(lower<1||head[lower].Piece.Id!="south-west-turn-entry"||head[lower-1].Piece.Id!="south-straight"||upper==turn+1)return null;
            var steps=new List<(string Id,bool Reverse)>();
            for(int i=0;i<head.Count;i++)
            {
                if(i==lower)steps.AddRange(Enumerable.Repeat(("south-straight",true),3));
                if(i==upper)steps.AddRange(Enumerable.Repeat(("north-straight",false),2));
                steps.Add((head[i].Piece.Id,head[i].Reverse));
            }
            var expanded=new List<RiverCoastPieces.Placement>{head[0]};
            foreach(var step in steps.Skip(1))
            {
                var previous=expanded[^1];var link=catalogue.FindConnection(previous.Piece.Id,previous.Reverse,step.Id,step.Reverse);
                if(link==null)return null;expanded.Add(new(pieces[step.Id],previous.X+link.Dx,previous.Y+link.Dy,step.Reverse));
            }
            var extended=catalogue.ResearchAssemble(expanded,out failure);
            if(extended is not {Closed:true})return null;original=extended.Pieces;
        }
        for(int start=original.Count-replaced;start>=1;start--)
        {
            if(!original.Skip(start).Take(replaced).All(p=>p.Piece.Id=="north-straight"&&!p.Reverse))continue;
            var placed=original.Take(start).ToList();
            bool ok=Append("north-west-flexible-join")&&Repeat("west-bank",true,verticalPeriods)
                &&Append("native-1391-1392-4-9",true)&&Repeat("west-diagonal",true,5-verticalPeriods)
                &&Append("west-diagonal-exit")&&Append("west-north-offset-join")
                &&Append("native-1377-1379-15-6")&&Append("native-1379-1382-10-2")&&Append("native-1382-1384-4-7")
                &&Append("east-diagonal")&&Append("native-1384-1393-5-5")
                &&Repeat("east-narrow-straight",false,6)&&Append("tributary-north-join");
            if(!ok)continue;
            foreach(var next in original.Skip(start+replaced))if(!Append(next.Piece.Id,next.Reverse)){ok=false;break;}
            if(!ok)continue;
            var chain=catalogue.ResearchAssemble(placed,out failure);
            if(chain is not {Closed:true})continue;
            var bounds=RiverPieceGroundAssembler.Measure(catalogue,chain);
            if(bounds.Width>maximumWidth||bounds.Height>maximumHeight){failure="The northward arm does not fit the requested area.";continue;}
            try
            {
                var ground=RiverPieceGroundAssembler.Assemble(catalogue,chain,seed,cancellation,verifyCovers:true,verifyChannel:true);
                return new(chain,"gentle-diagonal-main-northward-tributary"){PreparedGround=ground};
            }
            catch(System.IO.InvalidDataException error){failure=error.Message;}
            bool Repeat(string id,bool reverse,int count)
            {for(int i=0;i<count;i++)if(!Append(id,reverse))return false;return true;}
            bool Append(string id,bool reverse=false)
            {
                var previous=placed[^1];var links=catalogue.Links.Where(l=>l.FromId==previous.Piece.Id&&l.FromReverse==previous.Reverse&&l.ToId==id&&l.ToReverse==reverse).ToArray();
                if(links.Length!=1||!pieces.TryGetValue(id,out var next))return false;
                var link=links[0];placed.Add(new(next,previous.X+link.Dx,previous.Y+link.Dy,reverse));return true;
            }
        }
        return null;
    }

    public static WindingResult? ResearchCurvedTributary(RiverCoastPieces catalogue,int seed,int maximumWidth,int maximumHeight,
        out string failure,CancellationToken cancellation=default,bool verifyGround=true)
    {
        failure="The compatible curved main-channel pieces are unavailable.";
        if(!NativeCurves[0].Upper.Concat(NativeCurves[0].Lower).All(step=>catalogue.Pieces.Any(p=>p.Id==step.Id)))return null;
        for(int trial=0;trial<2;trial++)
        {
            cancellation.ThrowIfCancellationRequested();
            var main=ResearchHairpin(catalogue,seed+trial*7919,maximumWidth,maximumHeight,out failure,cancellation,
                meander:true,balancedBend:true,maximumStraightCells:72,bendPairs:[NativeCurves[0]]);
            if(main==null)continue;
            var branch=ResearchTributary(catalogue,seed,maximumWidth,maximumHeight,out failure,cancellation,
                curvedArm:true,baseChain:main.Chain,compactCurvedArm:true,shortCurvedArm:true,
                verifyGround:verifyGround,verifyChannel:true,baseShape:"native-curve");
            if(branch!=null)return branch;
        }
        return null;
    }

    public static WindingResult? ResearchBridgedBranching(RiverCoastPieces catalogue,int seed,int maximumWidth,int maximumHeight,
        out string failure,CancellationToken cancellation=default,bool verifyGround=true)
    {
        failure="No compatible native bridged tributary fits this area.";
        for(int trial=0;trial<8;trial++)
        {
            cancellation.ThrowIfCancellationRequested();
            var outline=ResearchBridged(catalogue,seed+trial*7919,maximumWidth,maximumHeight,out failure,cancellation,verifyGround:false);
            if(outline==null)continue;
            var branch=ResearchTributary(catalogue,seed,maximumWidth,maximumHeight,out failure,cancellation,
                curvedArm:true,baseChain:outline.Chain,compactCurvedArm:true,shortCurvedArm:true,verifyGround:verifyGround);
            if(branch!=null)return branch;
        }
        return null;
    }

    public static WindingResult? ResearchTributary(RiverCoastPieces catalogue,int seed,int maximumWidth,int maximumHeight,
        out string failure,CancellationToken cancellation=default,bool meander=false,bool verifyGround=true,bool curvedArm=false,
        RiverCoastPieces.Chain? baseChain=null,bool compactCurvedArm=false,bool shortCurvedArm=false,bool verifyChannel=false,string? baseShape=null,string curvedArmReturn="west-south-water-join")
    {
        failure="The reusable tributary junction pieces are unavailable.";
        var pieces=catalogue.Pieces.ToDictionary(p=>p.Id);
        if(new[]{"tributary-north-join","tributary-south-join","native-1385-1386-m1-9"}.Any(id=>!pieces.ContainsKey(id))
            ||curvedArm&&!pieces.ContainsKey(curvedArmReturn))return null;
        var random=new Random(seed^0x1742171);
        int groundAttempts=0;
        // A branch replaces four seven-cell continuations on one shore.
        // Its two junctions preserve that exact displacement. Solve both
        // arm lengths together; no complete course or scenery is fitted.
        foreach(int trial in Enumerable.Range(0,8))
        {
            cancellation.ThrowIfCancellationRequested();
            var outline=baseChain??ResearchHairpin(catalogue,seed+trial*7919,maximumWidth,maximumHeight,out failure,cancellation,meander,balancedBend:meander,maximumStraightCells:72)?.Chain;
            if(outline==null)continue;
            var original=outline.Pieces;
            const int replacedReaches=4;
            var starts=Enumerable.Range(1,Math.Max(0,original.Count-replacedReaches))
                .Where(i=>original.Skip(i).Take(replacedReaches).All(p=>p.Piece.Id=="east-narrow-straight"&&!p.Reverse)).OrderBy(_=>random.Next());
            foreach(int start in starts)
            foreach(int northLength in (curvedArm?(compactCurvedArm?(shortCurvedArm?new[]{1,3,5}:new[]{3,5,7}):new[]{2,4,6}):new[]{1,3,5}).OrderBy(_=>random.Next()))
            {
                string connectionFailure="";var placed=original.Take(start).ToList();
                bool ok=Append("tributary-north-join")&&Repeat("north-straight",northLength)&&(!curvedArm||OuterArmBend())&&Append("north-east-turn")
                    &&Append("east-turn-entry")&&Append("native-1385-1386-m1-9")&&Append("south-cap-exit")
                    &&(!curvedArm||InnerArmBend())&&Repeat("south-straight",curvedArm?(compactCurvedArm?(3*northLength-1)/2:(3*northLength-4)/2):(1+3*northLength)/2)&&Append("tributary-south-join");
                if(!ok){if(!string.IsNullOrEmpty(connectionFailure))failure=connectionFailure;continue;}
                foreach(var remaining in original.Skip(start+replacedReaches))
                    if(!Append(remaining.Piece.Id,remaining.Reverse)){ok=false;break;}
                if(!ok)continue;
                var chain=catalogue.ResearchAssemble(placed,out failure);
                if(chain==null||!chain.Closed)
                {
                    if(curvedArm&&chain!=null)failure="The curved arm does not return to the matching native bank endpoint.";
                    continue;
                }
                var bounds=RiverPieceGroundAssembler.Measure(catalogue,chain);
                if(bounds.Width>maximumWidth||bounds.Height>maximumHeight)continue;
                string shape=(baseChain!=null?"tributary-"+(baseShape??"bridged"):meander?"tributary-compound":"tributary-hairpin")+(curvedArm?(compactCurvedArm?"-compact-curved-arm":"-curved-arm"):"");
                if(!verifyGround){failure="";return new(chain,shape);}
                RiverPieceGroundAssembler.Result ground;
                try { groundAttempts++;ground=RiverPieceGroundAssembler.Assemble(catalogue,chain,seed,cancellation,verifyCovers:true,verifyChannel:verifyChannel); }
                catch(System.IO.InvalidDataException error){failure=error.Message;if(groundAttempts>=6)return null;continue;}
                failure="";return new(chain,shape){PreparedGround=ground};

                bool OuterArmBend()=>Append("north-east-turn")&&Append("east-turn-entry")&&Repeat("east-bank",compactCurvedArm?2:5)
                    &&Append("east-southeast-turn")&&Append("southeast-turn-entry")&&Append("southeast-turn-exit")&&Append("southeast-north-turn");
                bool InnerArmBend()
                {
                    // Move this return shore eight cells away from the outer bend.
                    // Compensate in the final return length so the junction ports still close.
                    // A shorter arm removes three outer 12-cell reaches and
                    // two inner (-6,-18) steps. An odd north length with its
                    // solved eight-cell return cancels the remaining x offset.
                    if(!Repeat("south-straight",4)||!Append("south-west-turn-entry")||!Append("west-diagonal-entry"))return false;
                    for(int i=0;i<(compactCurvedArm?1:3);i++)if(!Append("west-diagonal-exit")||!Append("native-1391-1392-4-9"))return false;
                    return Append("west-diagonal-exit")&&Repeat("west-bank",1)&&Append("west-concave-entry")&&Append(curvedArmReturn);
                }
                bool Repeat(string id,int count){for(int i=0;i<count;i++)if(!Append(id))return false;return true;}
                bool Append(string id,bool? reverse=null)
                {
                    var previous=placed[^1];var links=catalogue.Links.Where(l=>l.FromId==previous.Piece.Id&&l.FromReverse==previous.Reverse&&l.ToId==id&&(!reverse.HasValue||l.ToReverse==reverse.Value)).ToArray();
                    if(links.Length!=1){connectionFailure=$"Native connection unavailable: {previous.Piece.Id} to {id}";return false;}
                    var link=links[0];var piece=pieces[id];int x=previous.X+link.Dx,y=previous.Y+link.Dy;
                    if(!piece.GroundAligned(x,y)){connectionFailure=$"Native ground phase unavailable: {id} at {x},{y}";return false;}
                    placed.Add(new(piece,x,y,link.ToReverse));return true;
                }
            }
        }
        if(string.IsNullOrEmpty(failure))failure="No compatible native tributary fits this area.";return null;
    }

    public static WindingResult? ResearchNorthward(RiverCoastPieces catalogue,int seed,int maximumWidth,int maximumHeight,
        out string failure,CancellationToken cancellation=default,
        IReadOnlyList<(string Id,bool Reverse)>? upperRoute=null,IReadOnlyList<(string Id,bool Reverse)>? lowerRoute=null,int bendRepeats=1,
        IReadOnlyList<BendPair>? bendPairs=null,bool compactEnds=false)
    {
        var pieces=catalogue.Pieces.ToDictionary(p=>p.Id);
        string[] required={"north-west-flexible-join","native-1388-1389-7-m3","native-1387-1388-7-m7","south-diagonal","native-1387-1388-m6-6","native-1388-1389-m6-0"};
        if(upperRoute==null&&lowerRoute==null&&pieces.ContainsKey("west-north-return-shift-p2"))
        {
            upperRoute=[("north-west-flexible-join",false),("west-bank",true),("west-north-return-shift-p2",false),("north-west-flexible-join",false),("west-bank",true),("west-north-return-shift-p2",false),("north-straight",false)];
            lowerRoute=[("native-1388-1389-7-m3",true),("native-1387-1388-7-m7",true),("south-diagonal",true),("south-diagonal",true),("native-1387-1388-m6-6",false),("native-1388-1389-m6-0",false)];
        }
        failure="The northward native bank pieces are unavailable.";
        if(required.Any(id=>!pieces.ContainsKey(id)))return null;
        for(int trial=0;trial<8;trial++)
        {
            var original=ResearchHairpin(catalogue,seed+trial*7919,maximumWidth,maximumHeight,out failure,cancellation,meander:true,balancedBend:true,maximumStraightCells:72,bendRepeats:bendRepeats,bendPairs:bendPairs,compactEnds:compactEnds);
            if(original==null)continue;
            var source=original.Chain.Pieces;int upper=original.EntryLength-1;
            int lastSouth=source.ToList().FindLastIndex(p=>p.Piece.Id=="south-straight");
            if(lastSouth<0)continue;int lower=lastSouth;
            while(lower>0&&source[lower-1].Piece.Id=="south-straight")lower--;
            var placed=new List<RiverCoastPieces.Placement>{source[0]};bool valid=true;string appendFailure="";
            for(int i=0;i<source.Count;i++)
            {
                cancellation.ThrowIfCancellationRequested();
                if(i>0&&!Append(source[i].Piece.Id=="north-west-concave-join"?"north-west-flexible-join":source[i].Piece.Id,source[i].Reverse)){valid=false;break;}
                // Match both shore displacements. The curated return uses (+42,-36)
                // and (-42,+36), including its shorter native return join.
                // Short northward steps follow the opposite diagonal shore,
                // avoiding the broad pool left by one long right-angle turn.
                if(i==upper&&!NorthwardSteps()){valid=false;break;}
                if(i==lower&&!OppositeSteps()){valid=false;break;}
            }
            if(!valid){failure=appendFailure;continue;}
            var chain=catalogue.ResearchAssemble(placed,out failure);if(chain==null||!chain.Closed)continue;
            var bounds=RiverPieceGroundAssembler.Measure(catalogue,chain);if(bounds.Width>maximumWidth||bounds.Height>maximumHeight)continue;
            failure="";return new(chain,"northward-compound");
            bool Route(IReadOnlyList<(string Id,bool Reverse)> route)
            {
                foreach(var step in route)if(!Append(step.Id,step.Reverse))return false;return true;
            }
            bool OppositeSteps()=>lowerRoute!=null?Route(lowerRoute):
                Append("native-1388-1389-7-m3",true)&&Append("native-1387-1388-7-m7",true)&&Repeat("south-diagonal",true,2)
                &&Append("native-1387-1388-m6-6",false)&&Append("native-1388-1389-m6-0",false)&&Repeat("south-straight",true,1);
            bool NorthwardSteps()
            {
                if(upperRoute!=null)return Route(upperRoute);
                for(int step=0;step<2;step++)
                    if(!Append("north-west-flexible-join",false)||!Repeat("west-bank",true,1)||!Append("west-north-offset-join",false)||!Append("north-straight",false))return false;
                return true;
            }
            bool Repeat(string id,bool reverse,int count){for(int i=0;i<count;i++)if(!Append(id,reverse))return false;return true;}
            bool Append(string id,bool reverse)
            {
                var previous=placed[^1];var links=catalogue.Links.Where(l=>l.FromId==previous.Piece.Id&&l.FromReverse==previous.Reverse&&l.ToId==id&&l.ToReverse==reverse).ToArray();
                if(links.Length!=1){appendFailure=$"No unique native link from {previous.Piece.Id} to {id}.";return false;}var link=links[0];var piece=pieces[id];int x=previous.X+link.Dx,y=previous.Y+link.Dy;
                if(!piece.GroundAligned(x,y)){appendFailure=$"Saved ground alignment rejects {id} at ({x},{y}).";return false;}placed.Add(new(piece,x,y,reverse));return true;
            }
        }
        return null;
    }

    public static WindingResult? ResearchGentleBridgeSelection(RiverCoastPieces catalogue,int seed,int maximumWidth,int maximumHeight,
        out string failure,CancellationToken cancellation=default)
    {
        failure="No gradual native bridge river fits this area.";
        int preferred=new Random(seed^0x385174).Next(2);
        if(catalogue.Pieces.Any(p=>p.Id=="native-south-diagonal-return"))
        {
            // All ten arrangements have native bank, bend and crossing proofs.
            // Compact courses also belong in large maps: a seed should change
            // the course itself rather than only its position or upper reach.
            (int West,int East,int Upstream,bool Compact,int After)[] arrangements=[
                (7,3,0,false,0),(7,3,1,false,0),
                (8,2,0,false,0),(8,2,1,false,0),
                (6,0,0,true,1),(6,1,0,true,1),(5,1,0,true,2),
                (6,0,0,true,2),(6,1,0,true,2),(7,0,0,true,2)];
            var selection=new Random(seed^0x174385);
            for(int i=arrangements.Length-1;i>0;i--)
            {
                int swap=selection.Next(i+1);(arrangements[i],arrangements[swap])=(arrangements[swap],arrangements[i]);
            }
            foreach(var arrangement in arrangements)
            {
                cancellation.ThrowIfCancellationRequested();
                var winding=ResearchGentleBridged(catalogue,seed,maximumWidth,maximumHeight,arrangement.Upstream,out failure,cancellation,
                    pairedDownstream:true,pairedWestBalance:arrangement.West,pairedEastBalance:arrangement.East,
                    pairedPeriods:arrangement.Compact?0:1,pairedCompact:arrangement.Compact,pairedSouthAfter:arrangement.After);
                if(winding?.PreparedGround is {} windingGround&&RiverShapeQuality.Analyse(windingGround,cancellation).SimpleGentleChannel)return winding;
            }
        }
        foreach(int periods in new[]{preferred,1-preferred})
        {
            var built=ResearchGentleBridged(catalogue,seed,maximumWidth,maximumHeight,periods,out failure,cancellation);
            if(built?.PreparedGround is {} ground&&RiverShapeQuality.Analyse(ground,cancellation).SimpleGentleChannel)return built;
        }
        return null;
    }

    public static WindingResult? ResearchGentleBridged(RiverCoastPieces catalogue,int seed,int maximumWidth,int maximumHeight,
        int upstreamPeriods,out string failure,CancellationToken cancellation=default,bool verifyChannel=true,bool inspectRejectedMovement=false,
        int downstreamPeriods=0,int downstreamDiagonalBalance=0,int downstreamVerticalBalance=0,
        bool pairedDownstream=false,int pairedWestBalance=4,int pairedEastBalance=1,int pairedPeriods=0,bool pairedNativeSouthTurn=false,
        int pairedHorizontalPeriods=0,bool pairedCompact=false,int pairedSouthAfter=0)
    {
        if(upstreamPeriods is <-1 or >1)throw new ArgumentOutOfRangeException(nameof(upstreamPeriods));
        if(downstreamPeriods is <0 or >1)throw new ArgumentOutOfRangeException(nameof(downstreamPeriods));
        if(downstreamDiagonalBalance<0||downstreamDiagonalBalance>6*downstreamPeriods)throw new ArgumentOutOfRangeException(nameof(downstreamDiagonalBalance));
        if(downstreamVerticalBalance<0||downstreamVerticalBalance>3*downstreamPeriods)throw new ArgumentOutOfRangeException(nameof(downstreamVerticalBalance));
        if(pairedPeriods is <0 or >1||pairedWestBalance<0||pairedWestBalance>8+7*pairedPeriods+(pairedCompact?4:0)
            ||pairedEastBalance<0||pairedEastBalance>1+4*pairedPeriods)throw new ArgumentOutOfRangeException(nameof(pairedWestBalance));
        if(pairedHorizontalPeriods is <-1 or >1)throw new ArgumentOutOfRangeException(nameof(pairedHorizontalPeriods));
        int southPeriods=5+3*pairedHorizontalPeriods-(pairedCompact?3:0);
        if(pairedSouthAfter<0||pairedSouthAfter>southPeriods)throw new ArgumentOutOfRangeException(nameof(pairedSouthAfter));
        failure="The gentle bridge needs the short native bridge component and its saved diagonal cap.";
        var pieces=catalogue.Pieces.ToDictionary(p=>p.Id);
        if(!pieces.TryGetValue("bridge-west-shore",out var west)||west.End.BankId!="mir/mir_01391"
            ||!pieces.ContainsKey("diagonal-gentle-cap"))return null;
        var placed=new List<RiverCoastPieces.Placement>{new(west,0,0,false)};
        // Both routes return exactly to the existing four native bank ports.
        // Increasing the upstream reach keeps their original displacement.
        // The optional downstream study balances six (7,7) east diagonals,
        // three (0,7) narrow reaches and seven (-6,-9) opposite-bank diagonals.
        // Most combinations remain research only. Selection uses the two
        // paired offsets verified with the saved south diagonal return;
        // closure alone does not establish a natural winding course.
        int embeddedApproach=west.End.BankX==5&&west.End.BankY==16?1:0;
        bool mountainFree=west.End.BankX==15&&west.End.BankY==66;
        if(upstreamPeriods==-1&&(!mountainFree||!pieces.ContainsKey("native-1384-1385-4-7")))
        {failure="The short upstream approach requires the mountain-free bridge and native diagonal-to-vertical join.";return null;}
        bool ok=Repeat("west-diagonal",true,(mountainFree?16:6-embeddedApproach)+7*upstreamPeriods)&&Append("west-diagonal-exit")
            &&Append("west-north-offset-join")&&Append("native-1377-1379-15-6")
            &&Append("native-1379-1382-10-2")&&Append("native-1382-1384-4-7")
            &&Repeat("east-diagonal",false,(mountainFree?12:4)+6*upstreamPeriods)&&UpstreamApproach()
            &&Append("bridge-east-shore")&&Downstream();
        if(!ok)return null;
        bool UpstreamApproach()=>upstreamPeriods==-1?Append("native-1384-1385-4-7"):
            Append("native-1384-1393-5-5")&&Repeat("east-narrow-straight",false,(mountainFree?2:3)+3*upstreamPeriods)
            &&Append("native-1385-1393-1-m9",true);
        bool Downstream()
        {
            if(pairedDownstream)return PairedDownstream();
            return Repeat("east-diagonal",false,2+downstreamDiagonalBalance)
            &&Append("native-1384-1393-5-5")&&Repeat("east-narrow-straight",false,downstreamVerticalBalance)
            &&Append("native-1384-1393-m2-m9",true)
            &&Repeat("east-diagonal",false,2+6*downstreamPeriods-downstreamDiagonalBalance)&&Append("native-1384-1393-5-5")
            &&Repeat("east-narrow-straight",false,3*downstreamPeriods-downstreamVerticalBalance)
            &&Append("native-1384-1393-m2-m9",true)&&Append("diagonal-gentle-cap")&&Repeat("west-diagonal",true,(mountainFree?8:9)+7*downstreamPeriods);
        }
        bool PairedDownstream()
        {
            // Paired native excursions add (25,21). Removing three east
            // diagonals and adding one north/two south periods cancels it.
            return Repeat("east-diagonal",false,pairedEastBalance)
                &&Append("native-1384-1393-5-5")&&Append("east-narrow-straight")
                &&Append("native-1383-1393-m4-m13",true)&&Append("southeast-turn-entry")
                &&Append("southeast-turn-exit")&&Append("southeast-north-turn")
                &&Route(WaveNorthward.Upper.Take(pairedCompact?3:WaveNorthward.Upper.Count-1).ToArray())&&Repeat("north-straight",false,2+2*pairedHorizontalPeriods)
                &&Append("native-1377-1379-15-6")&&Append("native-1379-1382-10-2")&&Append("native-1382-1384-4-7")
                &&Repeat("east-diagonal",false,1+4*pairedPeriods-pairedEastBalance+(pairedNativeSouthTurn?1:0))
                &&Append("native-1384-1393-5-5")&&Append("native-1384-1393-m2-m9",true)
                &&Repeat("east-diagonal",false,2*pairedPeriods+(pairedCompact?1:0))
                &&Append("native-1384-1393-5-5")&&Repeat("east-narrow-straight",false,3*pairedPeriods+(pairedNativeSouthTurn?2:0)+(pairedCompact?3:0))
                &&Append("native-1384-1393-m2-m9",true)
                &&Append("diagonal-gentle-cap")&&Repeat("west-diagonal",true,pairedWestBalance)
                &&Append("west-diagonal-exit")&&Append("west-concave-entry")&&Append("west-south-offset-join")
                &&Repeat("south-straight",true,southPeriods-pairedSouthAfter)
                &&Route(WaveNorthward.Lower.Where((_,i)=>!pairedCompact||i!=3).ToArray())
                &&Repeat("south-straight",true,pairedSouthAfter)
                &&SouthTurn()
                &&Repeat("west-diagonal",true,8+7*pairedPeriods-pairedWestBalance+(pairedNativeSouthTurn?2:0)+(pairedCompact?4:0));
        }
        bool SouthTurn()=>pieces.ContainsKey("research-south-straight-diagonal-direct")?Append("research-south-straight-diagonal-direct"):
            pieces.ContainsKey("research-south-diagonal-direct")?
            Append("native-1388-1389-7-m3",true)&&Append("research-south-diagonal-direct"):
            pieces.ContainsKey("native-south-diagonal-return")?Append("native-south-diagonal-return"):
            (pairedNativeSouthTurn?Append("south-west-turn-entry"):
            Append("native-1388-1389-7-m3",true)&&Append("native-1388-1390-m3-1"))&&Append("west-diagonal-entry");
        bool Route(IReadOnlyList<(string Id,bool Reverse)> route)
        {foreach(var part in route)if(!Append(part.Id,part.Reverse))return false;return true;}
        var chain=catalogue.ResearchAssemble(placed,out failure);
        if(chain is not {Closed:true})return null;
        var bounds=RiverPieceGroundAssembler.Measure(catalogue,chain);
        if(bounds.Width>maximumWidth||bounds.Height>maximumHeight){failure="The gentle native bridge river does not fit the requested area.";return null;}
        try
        {
            var ground=inspectRejectedMovement?RiverPieceGroundAssembler.ResearchInspect(catalogue,chain,seed,cancellation):
                RiverPieceGroundAssembler.Assemble(catalogue,chain,seed,cancellation,verifyCovers:true,verifyChannel:verifyChannel);
            return new(chain,$"gentle-bridged-diagonal-u{upstreamPeriods}"+(pairedDownstream?$"-paired-p{pairedPeriods}-w{pairedWestBalance}-e{pairedEastBalance}"+(pairedCompact?$"-compact-a{pairedSouthAfter}":""):downstreamPeriods==0?"":$"-d{downstreamDiagonalBalance}-v{downstreamVerticalBalance}")){PreparedGround=ground};
        }
        catch(System.IO.InvalidDataException error){failure=error.Message;return null;}
        bool Repeat(string id,bool reverse,int count){for(int i=0;i<count;i++)if(!Append(id,reverse))return false;return true;}
        bool Append(string id,bool reverse=false)
        {
            if(!pieces.TryGetValue(id,out var next))return false;
            var previous=placed[^1];var links=catalogue.Links.Where(l=>l.FromId==previous.Piece.Id&&l.FromReverse==previous.Reverse&&l.ToId==id&&l.ToReverse==reverse).ToArray();
            if(links.Length!=1)return false;var link=links[0];placed.Add(new(next,previous.X+link.Dx,previous.Y+link.Dy,reverse));return true;
        }
    }

    public static WindingResult? ResearchBridged(RiverCoastPieces catalogue,int seed,int maximumWidth,int maximumHeight,
        out string failure,CancellationToken cancellation=default,bool verifyGround=true,bool steppedDownstream=false)
    {
        failure="The native bridge component and its connecting pieces are unavailable.";
        var pieces=catalogue.Pieces.ToDictionary(p=>p.Id);
        string[] required={"bridge-west-shore","bridge-east-shore","native-1384-1393-5-5","native-1386-1387-m7-7","native-1387-1390-m4-4","west-diagonal"};
        if(required.Any(id=>!pieces.ContainsKey(id)))return null;
        bool shortUpstream=pieces["bridge-west-shore"].End.BankId=="mir/mir_01391";
        string[] upperRequired=shortUpstream?["native-1382-1384-4-7","native-1385-1393-1-m9"]:
            ["native-1377-1379-15-6","native-1379-1379-17-6","native-1379-1382-10-2"];
        if(upperRequired.Any(id=>!pieces.ContainsKey(id)))return null;
        var random=new Random(seed^0x57A613);
        var options=new[]{(Upper:0,Lower:0),(Upper:1,Lower:0),(Upper:0,Lower:1),(Upper:1,Lower:1)};
        foreach(var option in options.OrderBy(_=>random.Next()))
        {
            cancellation.ThrowIfCancellationRequested();string appendFailure="";
            var placed=new List<RiverCoastPieces.Placement>{new(pieces["bridge-west-shore"],0,0,false)};
            bool ok=(shortUpstream?ShortUpper():Repeat("west-bank",true,4*option.Upper)&&Append("native-1391-1392-4-9",true)
                &&Repeat("west-diagonal",true,2)&&Append("west-diagonal-exit")&&Append("west-north-offset-join")
                &&Append("native-1377-1379-15-6")&&Append("native-1379-1379-17-6")&&Append("native-1379-1382-10-2")
                &&Append("east-turn-entry")&&Repeat("east-bank",false,2+3*option.Upper))&&Append("bridge-east-shore")
                &&(steppedDownstream?LowerSteps(option.Lower):Repeat("east-diagonal",false,6*option.Lower))&&Append("native-1384-1393-5-5")
                &&Repeat("east-narrow-straight",false,1+(steppedDownstream?0:3*option.Lower))&&Append("east-narrow-cap")
                &&Append("native-1386-1387-m7-7")&&Append("native-1387-1390-m4-4")&&Append("west-diagonal-entry")
                &&Repeat("west-diagonal",true,2+7*option.Lower);
            if(!ok){failure=appendFailure;continue;}
            var chain=catalogue.ResearchAssemble(placed,out failure);if(chain==null||!chain.Closed)continue;
            var bounds=RiverPieceGroundAssembler.Measure(catalogue,chain);
            if(bounds.Width>maximumWidth||bounds.Height>maximumHeight){failure="The full native bridge course does not fit this area.";continue;}
            RiverPieceGroundAssembler.Result? ground=null;
            if(verifyGround)try{ground=RiverPieceGroundAssembler.Assemble(catalogue,chain,seed,cancellation);}
            catch(System.IO.InvalidDataException error){failure=error.Message;continue;}
            failure="";return new(chain,$"{(shortUpstream?"short-upstream-":"")}{(steppedDownstream?"stepped-":"")}bridged-u{option.Upper}-d{option.Lower}"){PreparedGround=ground};
            bool ShortUpper()
            {
                if(!Append("west-diagonal-exit"))return false;
                for(int i=0;i<2*option.Upper;i++)
                    if(!Append("native-1391-1392-4-9",true)||!Append("west-diagonal-exit"))return false;
                return Append("native-1391-1392-4-9",true)&&Repeat("west-diagonal",true,1)
                    &&Append("west-diagonal-exit")&&Append("west-north-offset-join")&&Repeat("north-straight",false,option.Upper)
                    &&Append("north-east-turn")&&Append("native-1382-1384-4-7")&&Repeat("east-diagonal",false,3)
                    &&Append("native-1384-1393-5-5")&&Repeat("east-narrow-straight",false,1)
                    &&Append("native-1385-1393-1-m9",true)&&Repeat("east-bank",false,3*option.Upper);
            }
            bool LowerSteps(int count)
            {
                if(count==0)return true;
                // The two native turns add (7,14) each. Two short diagonal
                // reaches and one seven-cell narrow reach total (42,63),
                // matching seven west-bank diagonal periods exactly.
                return Repeat("east-diagonal",false,2)&&Append("native-1384-1393-5-5")
                    &&Repeat("east-narrow-straight",false,1)&&Append("native-1384-1393-m2-m9",true)
                    &&Repeat("east-diagonal",false,2)&&Append("native-1384-1393-5-5")
                    &&Append("native-1384-1393-m2-m9",true);
            }
            bool Repeat(string id,bool reverse,int count){for(int i=0;i<count;i++)if(!Append(id,reverse))return false;return true;}
            bool Append(string id,bool reverse=false)
            {
                if(!pieces.TryGetValue(id,out var piece)){appendFailure=$"Missing native bridge connector: {id}.";return false;}
                var previous=placed[^1];var links=catalogue.Links.Where(l=>l.FromId==previous.Piece.Id&&l.FromReverse==previous.Reverse&&l.ToId==id&&l.ToReverse==reverse).ToArray();
                if(links.Length!=1){appendFailure=$"No unique bridge bank connection from {previous.Piece.Id} to {id}.";return false;}
                var link=links[0];int x=previous.X+link.Dx,y=previous.Y+link.Dy;
                if(!piece.GroundAligned(x,y)){appendFailure=$"Bridge connector ground phase rejects {id}.";return false;}
                placed.Add(new(piece,x,y,reverse));return true;
            }
        }
        return null;
    }

    private static readonly BendPair[] NativeCurves =
    [
        new([("native-1377-1379-15-6",false),("native-1379-1382-10-2",false),("east-turn-entry",false),("east-bank",false),("east-bank",false),("east-southeast-turn",false),("southeast-turn-entry",false),("southeast-turn-exit",false),("southeast-north-turn",false)],[("native-1388-1389-7-m3",true),("native-1388-1390-m3-1",false),("west-diagonal-entry",false),("west-diagonal",true),("west-diagonal",true),("west-diagonal",true),("west-diagonal-exit",false),("west-bank",true),("west-concave-entry",false),("west-south-offset-join",false),("south-straight",true),("south-straight",true),("south-straight",true)]),
        new([("native-1377-1379-15-6",false),("native-1379-1382-10-2",false),("native-1382-1384-4-7",false),("native-1384-1393-5-5",false),("east-narrow-straight",false),("east-narrow-straight",false),("native-1383-1393-m4-m13",true),("southeast-turn-entry",false),("southeast-turn-exit",false),("southeast-north-turn",false)],[("native-1388-1389-7-m3",true),("native-1388-1390-m3-1",false),("west-diagonal-entry",false),("west-diagonal",true),("west-diagonal",true),("west-diagonal",true),("west-diagonal-exit",false),("west-concave-entry",false),("west-south-even-join",false),("south-straight",true),("south-straight",true),("south-straight",true),("south-straight",true)]),
    ];

    private static readonly BendPair[] WaveEntries =
    [
        new([("north-east-turn",false),("east-turn-entry",false),("east-diagonal-entry",false),("native-1384-1393-5-5",false),("east-narrow-straight",false),("native-1383-1393-m4-m13",true),("southeast-turn-entry",false),("southeast-turn-exit",false),("southeast-north-turn",false)],[("south-straight",true),("south-straight",true),("south-straight",true),("native-1388-1389-7-m3",true),("native-1388-1390-m3-1",false),("west-diagonal-entry",false),("west-diagonal",true),("west-diagonal",true),("west-diagonal",true),("west-diagonal-exit",false),("west-concave-entry",false),("west-south-offset-join",false),("south-straight",true)]),
        new([("north-east-turn",false),("native-1382-1384-4-7",false),("native-1384-1385-4-7",false),("east-narrow-entry",false),("east-narrow-straight",false),("native-1383-1393-m4-m13",true),("southeast-turn-entry",false),("southeast-turn-exit",false),("southeast-north-turn",false)],[("south-straight",true),("south-straight",true),("south-straight",true),("native-1388-1389-7-m3",true),("native-1388-1390-m3-1",false),("west-diagonal-entry",false),("west-diagonal",true),("west-diagonal",true),("west-diagonal",true),("west-diagonal-exit",false),("west-concave-entry",false),("west-south-offset-join",false),("south-straight",true)])
    ];
    private static readonly BendPair WaveReturn = new([("native-1377-1379-15-6",false),("native-1379-1382-10-2",false),("native-1382-1384-4-7",false),("native-1384-1393-5-5",false),("east-narrow-straight",false),("native-1383-1393-m4-m13",true),("southeast-turn-entry",false),("southeast-turn-exit",false),("southeast-north-turn",false)],[("south-straight",true),("south-straight",true),("south-west-turn-entry",false),("west-diagonal-entry",false),("west-diagonal",true),("west-diagonal",true),("west-diagonal-exit",false),("west-concave-entry",false),("west-south-offset-join",false),("south-straight",true)]);
    private static readonly BendPair WaveNorthward = new(
        [("north-west-flexible-join",false),("west-bank",true),("west-north-return-shift-p2",false),("north-west-flexible-join",false),("west-bank",true),("west-north-return-shift-p2",false),("north-straight",false)],
        [("native-1388-1389-7-m3",true),("native-1387-1388-7-m7",true),("south-diagonal",true),("south-diagonal",true),("native-1387-1388-m6-6",false),("native-1388-1389-m6-0",false)]);

    internal static IReadOnlyList<BendPair> CoastBends => [NativeCurves[0],NativeCurves[1],WaveNorthward];

    public static WindingResult? ResearchBalancedWave(RiverCoastPieces catalogue,int seed,int maximumWidth,int maximumHeight,
        int firstBend,int lastBend,out string failure,CancellationToken cancellation=default)
    {
        if(firstBend is <0 or >1||lastBend is <0 or >1)throw new ArgumentOutOfRangeException(nameof(firstBend));
        return ResearchWave(catalogue,seed,maximumWidth,maximumHeight,out failure,cancellation,shortHead:true,gentleEnd:true,
            bodyPairs:[NativeCurves[firstBend],WaveNorthward,NativeCurves[lastBend]]);
    }

    public static WindingResult? ResearchShiftedReturn(RiverCoastPieces catalogue,int seed,int maximumWidth,int maximumHeight,
        int leadingPeriods,out string failure,CancellationToken cancellation=default)
        =>ResearchWave(catalogue,seed,maximumWidth,maximumHeight,out failure,cancellation,shortHead:true,gentleEnd:true,returnLeadPeriods:leadingPeriods);

    public static WindingResult? ResearchDiagonalSweep(RiverCoastPieces catalogue,int seed,int maximumWidth,int maximumHeight,
        int leadingPeriods,out string failure,CancellationToken cancellation=default,bool diagonalEnd=false,
        IReadOnlyList<(string Id,bool Reverse)>? capRoute=null,int sweepCount=2,bool verifyFinalGround=true,
        IReadOnlyList<int>? northwardPeriods=null,IReadOnlyList<int>? bodyStraightPeriods=null,string? alternateNorthwestJoinId=null)
    {
        if(leadingPeriods is <0 or >7)throw new ArgumentOutOfRangeException(nameof(leadingPeriods));
        if(sweepCount is <1 or >4)throw new ArgumentOutOfRangeException(nameof(sweepCount));
        var upper=WaveReturn.Upper.Where(p=>p.Id!="east-narrow-straight")
            .SelectMany(p=>p.Id=="native-1384-1393-5-5"?Enumerable.Repeat(("east-diagonal",false),7).Append(p):new[]{p}).ToArray();
        (string Id,bool Reverse)[] core=[("native-1388-1389-7-m3",true),("native-1388-1390-m3-1",false),
            ("west-diagonal-entry",false),..Enumerable.Repeat(("west-diagonal",true),7),
            ("west-diagonal-exit",false),("west-concave-entry",false),("west-south-offset-join",false)];
        if(northwardPeriods!=null&&(northwardPeriods.Count!=sweepCount-1||northwardPeriods.Any(n=>n is <1 or >3)
            ||sweepCount+northwardPeriods.Sum()>8))throw new ArgumentException("Northward reaches need one matched count between each diagonal sweep.",nameof(northwardPeriods));
        var body=new List<BendPair>();
        for(int i=0;i<sweepCount;i++)
        {
            var lower=Enumerable.Repeat(("south-straight",true),leadingPeriods).Concat(core)
                .Concat(Enumerable.Repeat(("south-straight",true),7-leadingPeriods)).ToArray();
            body.Add(new BendPair(upper,lower));
            if(i+1<sweepCount)body.AddRange(Enumerable.Repeat(WaveNorthward,northwardPeriods?[i]??1));
        }
        if(alternateNorthwestJoinId!=null)
        {
            const string baseId="north-west-flexible-join";
            var original=catalogue.Pieces.Single(p=>p.Id==baseId);
            var alternative=catalogue.Pieces.SingleOrDefault(p=>p.Id==alternateNorthwestJoinId)
                ??throw new ArgumentException("The alternative join must be a verified saved piece in this catalogue.",nameof(alternateNorthwestJoinId));
            var originalBanks=original.Cells.Where(c=>c.Cell.MiddleImage!=0).OrderBy(c=>c.Y).ThenBy(c=>c.X)
                .Select(c=>(c.X,c.Y,c.Cell.MiddleLibrary,c.Cell.MiddleImage));
            var alternativeBanks=alternative.Cells.Where(c=>c.Cell.MiddleImage!=0).OrderBy(c=>c.Y).ThenBy(c=>c.X)
                .Select(c=>(c.X,c.Y,c.Cell.MiddleLibrary,c.Cell.MiddleImage));
            if(alternative.Start!=original.Start||alternative.End!=original.End||!originalBanks.SequenceEqual(alternativeBanks)
                ||alternative.Cells.Any(c=>(c.Cell.BackImage&0x1fffffff)!=0))
                throw new ArgumentException("Saved cover alternatives must retain the complete original banks and both native ports.",nameof(alternateNorthwestJoinId));
            int occurrence=seed&1;
            (string Id,bool Reverse) Select((string Id,bool Reverse) step)
                =>step.Id==baseId&&++occurrence%2==0?(alternateNorthwestJoinId,step.Reverse):step;
            // Select whole saved pieces while constructing the bank route.
            // Their covers are already baked; no scenery repair or second
            // full native ground assembly is needed to obtain variation.
            body=body.Select(p=>new BendPair(p.Upper.Select(Select).ToArray(),p.Lower.Select(Select).ToArray())).ToList();
        }
        var wave=ResearchWave(catalogue,seed,diagonalEnd?maximumWidth+160:maximumWidth,diagonalEnd?maximumHeight+128:maximumHeight,
            out failure,cancellation,shortHead:true,gentleEnd:true,
            bodyPairs:body,bankSpacing:(12,20),verifyNativeAssembly:!diagonalEnd,bodyStraightPeriods:bodyStraightPeriods);
        if(wave==null||!diagonalEnd)return wave;
        var shortened=ResearchDiagonalTerminal(catalogue,wave.Chain,out failure,capRoute);
        if(shortened==null)return null;
        var bounds=RiverPieceGroundAssembler.Measure(catalogue,shortened);
        if(bounds.Width>maximumWidth||bounds.Height>maximumHeight){failure="The shortened diagonal river does not fit the requested area.";return null;}
        if(!verifyFinalGround)return new(shortened,"diagonal-sweep-short-diagonal-end");
        try
        {
            var ground=RiverPieceGroundAssembler.Assemble(catalogue,shortened,seed,cancellation,verifyCovers:true,verifyChannel:true);
            return new(shortened,"diagonal-sweep-short-diagonal-end"+(wave.Shape.EndsWith("-short-head",StringComparison.Ordinal)?"-short-head":"")){PreparedGround=ground};
        }
        catch(System.IO.InvalidDataException error){failure=error.Message;return null;}
    }

    public static WindingResult? ResearchGentleDiagonalSweep(RiverCoastPieces catalogue,int seed,int maximumWidth,int maximumHeight,
        int sweepCount,out string failure,CancellationToken cancellation=default,IReadOnlyList<int>? northwardPeriods=null,
        IReadOnlyList<int>? bodyStraightPeriods=null,string? alternateNorthwestJoinId=null)
    {
        (string Id,bool Reverse)[] cap=[("native-1384-1385-4-7",false),("east-narrow-entry",false),
            ("native-1385-1393-1-m9",true),("native-1385-1386-m1-9",false),("south-cap-exit",false),
            ("south-west-turn-entry",false),("west-diagonal-entry",false)];
        if(catalogue.Pieces.Any(p=>p.Id=="diagonal-gentle-cap"))cap=[("diagonal-gentle-cap",false)];
        failure="No gentle native diagonal sweep fits this area.";
        for(int trial=0;trial<8;trial++)
        {
            var result=ResearchDiagonalSweep(catalogue,seed+trial*7919,maximumWidth,maximumHeight,4,out failure,cancellation,
                diagonalEnd:true,capRoute:cap,sweepCount:sweepCount,northwardPeriods:northwardPeriods,bodyStraightPeriods:bodyStraightPeriods,
                alternateNorthwestJoinId:alternateNorthwestJoinId);
            string variation=northwardPeriods?.Any(n=>n!=1)==true?"-northward-"+string.Join('-',northwardPeriods):"";
            if(bodyStraightPeriods!=null)variation+="-matched-reaches-"+string.Join('-',bodyStraightPeriods);
            if(result!=null)return result with{Shape=$"diagonal-sweep-{sweepCount}{variation}-gentle-diagonal-end"+(result.Shape.EndsWith("-short-head",StringComparison.Ordinal)?"-short-head":"")};
        }
        return null;
    }

    public static RiverCoastPieces.Chain? ResearchDiagonalTerminal(RiverCoastPieces catalogue,RiverCoastPieces.Chain source,out string failure,
        IReadOnlyList<(string Id,bool Reverse)>? capRoute=null)
    {
        failure="The native diagonal shores do not meet a diagonal cap.";
        var original=source.Pieces;var pieces=catalogue.Pieces.ToDictionary(p=>p.Id);
        int firstLower=original.ToList().FindIndex(p=>p.Piece.Id=="west-diagonal"&&p.Reverse);
        int lastUpper=original.ToList().FindLastIndex(p=>p.Piece.Id=="east-diagonal"&&!p.Reverse);
        if(firstLower<0||lastUpper<=firstLower)return null;
        int lowerEnd=firstLower;while(lowerEnd+1<original.Count&&original[lowerEnd+1].Piece.Id=="west-diagonal"&&original[lowerEnd+1].Reverse)lowerEnd++;
        int upperStart=lastUpper;while(upperStart>0&&original[upperStart-1].Piece.Id=="east-diagonal"&&!original[upperStart-1].Reverse)upperStart--;
        for(int upper=lastUpper;upper>=upperStart;upper--)
        for(int lower=firstLower;lower<=lowerEnd;lower++)
        for(int narrow=0;narrow<=(capRoute==null?4:0);narrow++)
        {
            var placed=original.Skip(lower).Take(upper-lower+1).ToList();
            bool ok=true;
            if(capRoute!=null)
            {
                foreach(var step in capRoute)if(!Append(step.Id,step.Reverse)){ok=false;break;}
            }
            else
            {
                ok=Append("native-1384-1393-5-5");
                for(int i=0;i<narrow&&ok;i++)ok=Append("east-narrow-straight");
                ok=ok&&Append("east-narrow-cap")&&Append("native-1386-1387-m7-7")
                    &&Append("native-1387-1390-m4-4")&&Append("west-diagonal-entry");
            }
            if(!ok)continue;
            var chain=catalogue.ResearchAssemble(placed,out string rejected);
            if(chain is {Closed:true}){failure="";return chain;}
            if(chain==null)failure=rejected;

            bool Append(string id,bool reverse=false)
            {
                var previous=placed[^1];var links=catalogue.Links.Where(l=>l.FromId==previous.Piece.Id&&l.FromReverse==previous.Reverse&&l.ToId==id&&l.ToReverse==reverse).ToArray();
                if(links.Length!=1||!pieces.TryGetValue(id,out var next))return false;
                var link=links[0];placed.Add(new(next,previous.X+link.Dx,previous.Y+link.Dy,reverse));return true;
            }
        }
        return null;
    }

    public static WindingResult? ResearchWave(RiverCoastPieces catalogue,int seed,int maximumWidth,int maximumHeight,
        out string failure,CancellationToken cancellation=default,bool curvedEnd=false,bool extended=false,bool roundedEnd=false,bool shortHead=false,bool gentleEnd=false,
        IReadOnlyList<(string Id,bool Reverse)>? lowerReturn=null,IReadOnlyList<BendPair>? bodyPairs=null,int? returnLeadPeriods=null,
        IReadOnlyList<(string Id,bool Reverse)>? lowerEntry=null,(int Minimum,int Maximum)? bankSpacing=null,int entryVerticalPeriods=1,
        bool verifyNativeAssembly=true,IReadOnlyList<int>? bodyStraightPeriods=null,int pairedOffsetX=0,int pairedOffsetY=0)
    {
        failure="The native direction-changing bend pieces are unavailable.";
        if(!WaveEntries.Concat(new[]{WaveReturn,WaveNorthward}).SelectMany(p=>p.Upper.Concat(p.Lower))
            .All(step=>catalogue.Pieces.Any(p=>p.Id==step.Id)))return null;
        var random=new Random(seed^0x3E4169);int first=random.Next(2),mask=random.Next(8);
        if(bodyPairs!=null&&bodyPairs.Count is <1 or >8)throw new ArgumentException("Each wave bend requires a matched native shore pair.",nameof(bodyPairs));
        if(entryVerticalPeriods is <0 or >3)throw new ArgumentOutOfRangeException(nameof(entryVerticalPeriods));
        var returnBend=lowerReturn==null?WaveReturn:new BendPair(WaveReturn.Upper.Where(p=>p.Id!="east-narrow-straight").ToArray(),lowerReturn);
        if(returnLeadPeriods.HasValue)
        {
            int lead=returnLeadPeriods.Value;
            if(lead is <0 or >3)throw new ArgumentOutOfRangeException(nameof(returnLeadPeriods));
            var lower=Enumerable.Repeat(("south-straight",true),lead).Concat(WaveReturn.Lower.Skip(2).SkipLast(1))
                .Concat(Enumerable.Repeat(("south-straight",true),3-lead)).ToArray();
            returnBend=new(WaveReturn.Upper,lower);
        }
        // Keep the two physical shores matched while shortening reaches to fit.
        // Only existing saved bank pieces are assembled; no complete course is loaded.
        // Explicit shore pairs already determine the entry, and explicit reach
        // counts already determine spacing. Repeating those same outlines
        // cannot find a different native assembly.
        foreach(int entry in bodyPairs!=null?new[]{first}:new[]{first,1-first})
        foreach(int spacing in (bodyStraightPeriods!=null?new[]{0}:
            gentleEnd?new[]{mask,mask&3,mask&1,0}.Concat(Enumerable.Range(0,8)):new[]{mask,mask&3,mask&1,0}).Distinct())
        {
            var entryBend=lowerEntry==null?WaveEntries[entry]:new BendPair(
                new[]{("native-1377-1379-15-6",false),("native-1379-1382-10-2",false)}.Concat(WaveEntries[entry].Upper.Skip(1)
                    .SelectMany(p=>p.Id=="east-narrow-straight"?Enumerable.Repeat(p,entryVerticalPeriods):new[]{p})).ToArray(),lowerEntry);
            var route=extended
                ?new[]{WaveEntries[entry],WaveNorthward,returnBend,WaveEntries[1-entry],WaveNorthward,returnBend}
                :new[]{entryBend,WaveNorthward,returnBend};
            if(bodyPairs!=null)route=bodyPairs.ToArray();
            if(bodyStraightPeriods!=null&&(bodyStraightPeriods.Count!=route.Length||bodyStraightPeriods.Any(n=>n is <0 or >3)
                ||gentleEnd&&bodyStraightPeriods[^1]==0))
                throw new ArgumentException("Each body bend needs zero to three matched reaches, with at least one before the gentle end.",nameof(bodyStraightPeriods));
            route=route.Select((p,i)=>
            {
                int periods=bodyStraightPeriods?[i]??(((spacing|(extended?56:0)|(gentleEnd?1<<(route.Length-1):0))&(1<<i))==0?0:1);
                // Two 12-cell north-bank pieces match three 8-cell south-bank
                // pieces. Extend both shores together, preserving native joins.
                return periods==0?p:new BendPair(
                    p.Upper.Concat(Enumerable.Repeat(("north-straight",false),2*periods)).ToArray(),
                    Enumerable.Repeat(("south-straight",true),3*periods).Concat(p.Lower).ToArray());
            }).ToArray();
            if(curvedEnd)
            {
                var headed=ResearchNorthward(catalogue,seed,maximumWidth,maximumHeight,out failure,cancellation,
                    bendRepeats:route.Length,bendPairs:route,compactEnds:true);
                if(headed!=null)return Finish(headed.Chain,extended?"mixed-curves-wave-extended-curved-end":"mixed-curves-wave-curved-end");
                continue;
            }
            var built=ResearchHairpin(catalogue,seed,maximumWidth,maximumHeight,out failure,cancellation,meander:true,
                balancedBend:true,maximumStraightCells:72,bendRepeats:route.Length,bendPairs:route,compactEnds:true,
                minimumBankSpacing:bankSpacing?.Minimum??(bodyPairs==null?12:24),maximumBankSpacing:bankSpacing?.Maximum??(bodyPairs==null?20:44),
                pairedOffsetX:pairedOffsetX,pairedOffsetY:pairedOffsetY);
            if(built!=null)
            {
                var chain=gentleEnd?ResearchGentleTerminal(catalogue,built.Chain,out failure):roundedEnd?ResearchRoundedTerminal(catalogue,built.Chain,out failure):built.Chain;
                if(chain!=null)
                {
                    var result=Finish(chain,(extended?"mixed-curves-wave-extended":"mixed-curves-wave")+(gentleEnd?"-gentle-end":roundedEnd?"-rounded-end":""));
                    if(!gentleEnd)return result;
                    var bounds=RiverPieceGroundAssembler.Measure(catalogue,result.Chain);
                    if(bounds.Width>maximumWidth||bounds.Height>maximumHeight)continue;
                    if(!verifyNativeAssembly)return result;
                    try
                    {
                        var ground=RiverPieceGroundAssembler.Assemble(catalogue,result.Chain,seed,cancellation,verifyCovers:true,verifyChannel:true);
                        return result with{PreparedGround=ground};
                    }
                    catch(System.IO.InvalidDataException error){failure=error.Message;}
                }
            }
        }
        return null;

        WindingResult Finish(RiverCoastPieces.Chain chain,string shape)
        {
            if(!shortHead)return new(chain,shape);
            int periods=new Random(seed^0x47361D).Next(2)+1;
            var shorter=ResearchShortHead(catalogue,chain,periods,out _)
                ??(periods==2?ResearchShortHead(catalogue,chain,1,out _):null);
            return shorter==null?new(chain,shape):new(shorter,shape+"-short-head");
        }
    }

    public static RiverCoastPieces.Chain? ResearchShortHead(RiverCoastPieces catalogue,RiverCoastPieces.Chain source,int periods,out string failure)
    {
        if(periods is <1 or >2)throw new ArgumentOutOfRangeException(nameof(periods));
        failure="The short native head does not fit these bank periods.";
        var original=source.Pieces.ToList();
        int join=original.FindIndex(p=>p.Piece.Id=="west-north-offset-join"&&!p.Reverse);
        if(join<4||original[join-3].Piece.Id!="south-west-turn-entry"
            ||original[join-2].Piece.Id!="west-diagonal-entry"||original[join-1].Piece.Id!="west-diagonal-exit")return null;
        int lower=join-4;
        while(lower>0&&original[lower-1].Piece.Id=="south-straight"&&original[lower-1].Reverse)lower--;
        int upper=join+1;
        while(upper<original.Count&&original[upper].Piece.Id=="north-straight"&&!original[upper].Reverse)upper++;
        int lowerCount=join-3-lower,upperCount=upper-join-1;
        // Three eight-cell and two twelve-cell native reaches both span 24 cells.
        // Remove matching periods from both shores; the river body stays fixed.
        if(lowerCount<3*periods||upperCount<=2*periods)return null;
        var pieces=catalogue.Pieces.ToDictionary(p=>p.Id);var placed=original.Take(lower).ToList();
        for(int i=0;i<lowerCount-3*periods;i++)if(!Append("south-straight",true))return null;
        foreach(var part in original.Skip(join-3).Take(4))if(!Append(part.Piece.Id,part.Reverse))return null;
        for(int i=0;i<upperCount-2*periods;i++)if(!Append("north-straight",false))return null;
        for(int i=upper;i<original.Count;i++)
        {
            var part=original[i];if(!Append(part.Piece.Id,part.Reverse))return null;
            if(placed[^1].X!=part.X||placed[^1].Y!=part.Y)return null;
        }
        var chain=catalogue.ResearchAssemble(placed,out failure);
        return chain is {Closed:true}?chain:null;

        bool Append(string id,bool reverse)
        {
            var previous=placed[^1];
            var links=catalogue.Links.Where(l=>l.FromId==previous.Piece.Id&&l.FromReverse==previous.Reverse&&l.ToId==id&&l.ToReverse==reverse).ToArray();
            if(links.Length!=1||!pieces.TryGetValue(id,out var next))return false;
            int x=previous.X+links[0].Dx,y=previous.Y+links[0].Dy;
            if(!next.GroundAligned(x,y))return false;
            placed.Add(new(next,x,y,reverse));return true;
        }
    }

    public static RiverCoastPieces.Chain? ResearchGentleTerminal(RiverCoastPieces catalogue,RiverCoastPieces.Chain source,out string failure)
    {
        failure="The gentle native end does not match this bank outline.";
        var original=source.Pieces.ToList();
        int corner=original.FindLastIndex(p=>p.Piece.Id=="north-east-turn"&&!p.Reverse),start=corner-1;
        if(start<0||original[0].Piece.Id!="north-straight"||original[0].Reverse||original[start].Reverse)return null;
        var previous=original[start];
        int baseDx,baseDy;
        if(previous.Piece.Id=="north-straight"){baseDx=-9;baseDy=45;}
        else if(previous.Piece.Id=="southeast-north-turn"){baseDx=-7;baseDy=46;}
        else return null;
        int dx=original[0].X-previous.X,dy=original[0].Y-previous.Y;
        if(dy!=baseDy||dx>baseDx||baseDx-dx>24||(baseDx-dx)%8!=0)return null;
        var pieces=catalogue.Pieces.ToDictionary(p=>p.Id);var placed=original.Take(start+1).ToList();
        (string Id,bool Reverse)[] first=[("north-east-turn",false),("native-1382-1384-4-7",false),
            ("native-1384-1385-4-7",false),("east-narrow-entry",false),("east-narrow-cap",false),
            ("native-1386-1387-m7-7",false),("south-diagonal",true),("south-diagonal",true),
            ("native-1387-1388-m6-6",false),("native-1388-1389-m6-0",false)];
        (string Id,bool Reverse)[] last=[("south-west-turn-entry",false),("west-diagonal-entry",false),
            ("west-diagonal-exit",false),("west-north-offset-join",false),("north-straight",false),("north-straight",false)];
        foreach(var step in first)if(!Append(step.Id,step.Reverse))return null;
        for(int i=0;i<1+(baseDx-dx)/8;i++)if(!Append("south-straight",true))return null;
        foreach(var step in last)if(!Append(step.Id,step.Reverse))return null;
        if(placed[^1].X!=original[0].X||placed[^1].Y!=original[0].Y)return null;
        placed.RemoveAt(placed.Count-1);
        var chain=catalogue.ResearchAssemble(placed,out failure);
        return chain is {Closed:true}?chain:null;

        bool Append(string id,bool reverse)
        {
            var prior=placed[^1];
            var links=catalogue.Links.Where(l=>l.FromId==prior.Piece.Id&&l.FromReverse==prior.Reverse&&l.ToId==id&&l.ToReverse==reverse).ToArray();
            if(links.Length!=1||!pieces.TryGetValue(id,out var next))return false;
            int x=prior.X+links[0].Dx,y=prior.Y+links[0].Dy;
            if(!next.GroundAligned(x,y))return false;
            placed.Add(new(next,x,y,reverse));return true;
        }
    }

    public static RiverCoastPieces.Chain? ResearchRoundedTerminal(RiverCoastPieces catalogue,RiverCoastPieces.Chain source,out string failure)
    {
        failure="The native rounded terminal does not match this bank outline.";
        var original=source.Pieces.ToList();
        int start=original.FindLastIndex(p=>p.Piece.Id=="north-east-turn"&&!p.Reverse);
        if(start<0||original[0].Piece.Id!="north-straight"||original[0].Reverse)return null;
        int dx=original[0].X-original[start].X,dy=original[0].Y-original[start].Y;
        // Exact native endpoints, with optional eight-cell lower-bank periods.
        // This changes the end outline by assembling existing pieces only.
        if(dy!=45||dx> -21||(dx+21)%8!=0||dx< -45)return null;
        var pieces=catalogue.Pieces.ToDictionary(p=>p.Id);var placed=original.Take(start+1).ToList();
        (string Id,bool Reverse)[] first=[("east-turn-entry",false),("east-bank",false),("east-bank",false),
            ("east-narrow-entry",false),("east-narrow-cap",false),("native-1386-1387-m7-7",false),
            ("native-1387-1388-m6-6",false),("native-1388-1389-m6-0",false)];
        (string Id,bool Reverse)[] last=[("native-1388-1389-7-m3",true),("native-1388-1390-m3-1",false),
            ("west-diagonal-entry",false),("west-diagonal-exit",false),("west-north-offset-join",false),("north-straight",false)];
        foreach(var step in first)if(!Append(step.Id,step.Reverse))return null;
        for(int i=0;i<(-dx-21)/8;i++)if(!Append("south-straight",true))return null;
        foreach(var step in last)if(!Append(step.Id,step.Reverse))return null;
        if(placed[^1].X!=original[0].X||placed[^1].Y!=original[0].Y)return null;
        placed.RemoveAt(placed.Count-1);
        var chain=catalogue.ResearchAssemble(placed,out failure);
        return chain is {Closed:true}?chain:null;

        bool Append(string id,bool reverse)
        {
            var previous=placed[^1];
            var links=catalogue.Links.Where(l=>l.FromId==previous.Piece.Id&&l.FromReverse==previous.Reverse&&l.ToId==id&&l.ToReverse==reverse).ToArray();
            if(links.Length!=1||!pieces.TryGetValue(id,out var next))return false;
            int x=previous.X+links[0].Dx,y=previous.Y+links[0].Dy;
            if(!next.GroundAligned(x,y))return false;
            placed.Add(new(next,x,y,reverse));return true;
        }
    }

    private static Result? ResearchCurved(RiverCoastPieces catalogue,int seed,int maximumWidth,int maximumHeight,
        out string failure,CancellationToken cancellation,int bends)
    {
        var random=new Random(seed^0x51781B3);
        var choices=Enumerable.Range(0,bends).Select(_=>random.Next(NativeCurves.Length)).ToArray();
        if(bends>1&&choices.All(i=>i==choices[0]))choices[^1]=1-choices[0];
        var pairs=choices.Select(i=>NativeCurves[i]).ToArray();
        return ResearchHairpin(catalogue,seed,maximumWidth,maximumHeight,out failure,cancellation,meander:true,
            balancedBend:true,maximumStraightCells:72,bendRepeats:bends,bendPairs:pairs,compactEnds:true);
    }

    // Minimum supported bounds of the native direction patterns. Their banks
    // and end pieces are unchanged; variation comes from paired reaches.
    private static readonly (int[] Pattern,int Width,int Height)[] GentleDirections=
    [
        ([1],242,142),([2],308,136),([3],350,146),
        ([1,1],404,202),([1,2],470,196),([2,1],470,166),([2,2],512,160),
        ([1,3],512,196),([3,1],512,146),([2,3],554,160),([3,2],554,146)
    ];

    public static WindingResult? ResearchWinding(RiverCoastPieces catalogue,int seed,int maximumWidth,int maximumHeight,
        out string failure,CancellationToken cancellation=default)
    {
        if(catalogue.Pieces.Any(p=>p.Id=="west-diagonal-parallel-8-9"&&p.NativeFrontBankCommands.Count==20))
        {
            // Choose direction before fitting the horizontal bend grammar.
            // Taller courses have their own paired native vertical/offset reaches.
            if(maximumHeight>=300&&maximumWidth>=242&&((uint)RiverSeed.Derive(seed,39)&1)==0)
            {
                var vertical=RiverTerminalCourses.Vertical(catalogue,seed,maximumWidth,maximumHeight,out _,cancellation);
                if(vertical!=null){failure="";return vertical;}
            }
            // Mix the seed before choosing proportions so adjacent seeds do not
            // inherit correlations in Random's first few choices.
            var courseRandom=new Random(RiverSeed.Derive(seed,0));
            int maximumGroups=maximumWidth>=800&&maximumHeight>=384?4:maximumWidth>=600&&maximumHeight>=280?3:
                maximumWidth>=404&&maximumHeight>=202?2:1;
            var groupOrder=Enumerable.Range(1,maximumGroups).OrderBy(_=>courseRandom.Next()).ToArray();
            var candidates=Enumerable.Range(0,3*maximumGroups).Select(attempt=>
            {
                // Give the requested number of bends several fitting attempts
                // before moving on, instead of favouring the first small course.
                int groups=groupOrder[attempt/3];
                // Select from every balanced composition, rather than a small
                // table of repeating waves. Equal total displacement is required
                // by the native terminal; each bend can still have its own length.
                var compositions=new List<int[]>();
                void Compose(int[] values,int at,int remaining)
                {
                    if(at==groups){if(remaining==0)compositions.Add((int[])values.Clone());return;}
                    for(int n=3;n<=9;n+=2)
                        if(remaining-n>=3*(groups-at-1)&&remaining-n<=9*(groups-at-1))
                        {values[at]=n;Compose(values,at+1,remaining-n);}
                }
                Compose(new int[groups],0,groups*7);
                int[] counts=compositions[courseRandom.Next(compositions.Count)];
                var up=new int[groups];
                int availableRises=Math.Max(0,(maximumWidth-176-(groups-1)*138-24)/42);
                int extra=courseRandom.Next(Math.Min(availableRises,Math.Min(5*groups,7-groups))+1);
                while(extra-->0)
                {
                    var available=Enumerable.Range(0,groups).Where(i=>up[i]<5).ToArray();
                    up[available[courseRandom.Next(available.Length)]]++;
                }
                var riseBefore=Enumerable.Range(0,groups).Select(_=>courseRandom.Next(2)==0).ToArray();
                var reaches=new int[groups+up.Sum()+1];reaches[^1]=1;
                int availableReaches=Math.Clamp((maximumWidth-176-(groups-1)*138-up.Sum()*42)/24,1,reaches.Length*2);
                int reachCount=courseRandom.Next(1,availableReaches+1);
                while(--reachCount>0)
                {
                    var available=Enumerable.Range(0,reaches.Length).Where(i=>reaches[i]<2).ToArray();
                    reaches[available[courseRandom.Next(available.Length)]]++;
                }
                return (Counts:counts,Up:up,RiseBefore:riseBefore,Reaches:reaches,Lead:courseRandom.Next(2,5),Seed:courseRandom.Next());
            }).ToArray();
            WindingResult? Balance(WindingResult? candidate)=>candidate==null?null:
                RiverUniformWidthBuilder.Build(catalogue,candidate,seed,maximumWidth,maximumHeight,out _,cancellation);
            foreach(var candidate in candidates)
            {
                cancellation.ThrowIfCancellationRequested();
                var varied=RiverBankOnlyWindingBuilder.Build(catalogue,candidate.Seed,maximumWidth,maximumHeight,out _,cancellation,
                    bendRepeats:candidate.Counts.Length,northwardPeriods:candidate.Up,diagonalPeriodPattern:candidate.Counts,riseBeforeGroup:candidate.RiseBefore,
                    bankLeadPeriods:candidate.Lead,reachPeriodPattern:candidate.Reaches);
                varied=Balance(varied);
                if(varied!=null){failure="";return varied;}
            }
            if(maximumWidth>=452&&maximumHeight>=202)
            {
                int[][] fallbackLengths=[[9,5],[5,9],[7,7]];
                foreach(var lengths in fallbackLengths.OrderBy(_=>courseRandom.Next()))
                {
                    var unequal=RiverBankOnlyWindingBuilder.Build(catalogue,seed,maximumWidth,maximumHeight,out _,cancellation,
                        bendRepeats:2,diagonalPeriodPattern:lengths);
                    unequal=Balance(unequal);
                    if(unequal!=null){failure="";return unequal;}
                }
            }
            if(maximumWidth>=404&&maximumHeight>=202)
            {
                int[][] patterns=[[1,1],[1,2],[2,1],[2,2]];
                int firstBankPattern=new Random(seed^0x45862B).Next(patterns.Length);
                for(int attempt=0;attempt<patterns.Length;attempt++)
                {
                    var expanded=RiverBankOnlyWindingBuilder.Build(catalogue,seed,maximumWidth,maximumHeight,out _,cancellation,
                        bendRepeats:2,northwardPeriods:patterns[(firstBankPattern+attempt)%patterns.Length]);
                    expanded=Balance(expanded);
                    if(expanded!=null){failure="";return expanded;}
                }
            }
            var bankOnly=RiverBankOnlyWindingBuilder.Build(catalogue,seed,maximumWidth,maximumHeight,out _,cancellation);
            bankOnly=Balance(bankOnly);
            if(bankOnly!=null){failure="";return bankOnly;}
        }
        string? coverVariant=catalogue.Pieces.Any(p=>p.Id==TerrainCoastObjects.NorthwestCoverVariantId)
            ?TerrainCoastObjects.NorthwestCoverVariantId:null;
        if(catalogue.Pieces.Any(p=>p.Id=="diagonal-gentle-cap"))
        {
            int largest=maximumWidth>=404&&maximumHeight>=202?3:maximumWidth>=242&&maximumHeight>=142?2:1;
            var courseRandom=new Random(seed^0x418C79);
            var directions=GentleDirections.Where(p=>p.Width<=maximumWidth&&p.Height<=maximumHeight).ToArray();
            if(directions.Length>0)
            {
                var preferred=directions[courseRandom.Next(directions.Length)];
                var reachRandom=new Random(seed^0x461895);
                var reaches=Enumerable.Range(0,preferred.Pattern.Length+1+preferred.Pattern.Sum())
                    .Select(_=>reachRandom.Next(2)).ToArray();
                reaches[^1]=1;
                var stretched=ResearchGentleDiagonalSweep(catalogue,seed,maximumWidth,maximumHeight,preferred.Pattern.Length+1,
                    out _,cancellation,northwardPeriods:preferred.Pattern,bodyStraightPeriods:reaches,
                    alternateNorthwestJoinId:coverVariant);
                if(stretched?.PreparedGround is {} variedGround&&RiverShapeQuality.Analyse(variedGround,cancellation).SimpleGentleChannel)
                {failure="";return stretched;}
                var varied=ResearchGentleDiagonalSweep(catalogue,seed,maximumWidth,maximumHeight,preferred.Pattern.Length+1,
                    out _,cancellation,northwardPeriods:preferred.Pattern,alternateNorthwestJoinId:coverVariant);
                if(varied?.PreparedGround is {} water&&RiverShapeQuality.Analyse(water,cancellation).SimpleGentleChannel)
                {failure="";return varied;}
            }
            var counts=largest==3?new[]{2,3}.OrderBy(_=>courseRandom.Next()).ToArray():new[]{largest};
            foreach(int count in counts.Concat(new[]{1}).Distinct())
            {
                var gentle=ResearchGentleDiagonalSweep(catalogue,seed,maximumWidth,maximumHeight,count,out _,cancellation,
                    alternateNorthwestJoinId:coverVariant);
                if(gentle?.PreparedGround is { } native&&RiverShapeQuality.Analyse(native,cancellation).SimpleGentleChannel)
                {failure="";return gentle;}
            }
            failure="No gentle native river fits this area. Increase the generation area to fit both banks and their end pieces.";
            return null;
        }
        bool flexible=catalogue.Pieces.Any(p=>p.Id=="west-north-offset-join"&&p.GroundPhaseMask==15)
            &&catalogue.Pieces.Any(p=>p.Id=="west-south-even-join"&&p.GroundPhaseMask==15)
            &&catalogue.Pieces.Any(p=>p.Id=="west-south-offset-join"&&p.GroundPhaseMask==15);
        var shapes=flexible?(catalogue.Pieces.Any(p=>p.Id=="west-north-return-shift-p2")?new[]{"successive","compound","double-compound","northward","northward-double"}:new[]{"successive","compound","double-compound"}):new[]{"successive"};
        bool nativeCurves=NativeCurves.SelectMany(p=>p.Upper.Concat(p.Lower)).All(step=>catalogue.Pieces.Any(p=>p.Id==step.Id));
        if(flexible&&nativeCurves)shapes=shapes.Concat(new[]{"curved-reach","mixed-curves"}).ToArray();
        failure="No native winding bank outline fits this area.";
        if(flexible&&nativeCurves&&new Random(seed^0x17948C3).Next(3)==0)
        {
            // The entry and return have opposite eighteen-cell offsets.
            // Extend them as a complete matched group, keeping both shores aligned.
            if(maximumWidth>=512&&maximumHeight>=384)
            {
                bool extendedEnd=new Random(seed^0x28173E5).Next(2)==0;
                var longer=ResearchWave(catalogue,seed,maximumWidth,maximumHeight,out failure,cancellation,
                    curvedEnd:extendedEnd,extended:true,roundedEnd:!extendedEnd,shortHead:true);
                if(longer!=null)
                    try
                    {
                        var ground=RiverPieceGroundAssembler.Assemble(catalogue,longer.Chain,seed,cancellation,verifyCovers:true,verifyChannel:true);
                        return new(longer.Chain,longer.Shape){PreparedGround=ground};
                    }
                    catch(System.IO.InvalidDataException error){failure=error.Message;}
            }
            bool curvedEnd=new Random(seed^0x28173E5).Next(2)==0;
            var wave=(!curvedEnd?ResearchWave(catalogue,seed,maximumWidth,maximumHeight,out failure,cancellation,gentleEnd:true,shortHead:true):null)
                ??ResearchWave(catalogue,seed,maximumWidth,maximumHeight,out failure,cancellation,curvedEnd:curvedEnd,roundedEnd:!curvedEnd,shortHead:true)
                ??(curvedEnd?ResearchWave(catalogue,seed,maximumWidth,maximumHeight,out failure,cancellation,gentleEnd:true,shortHead:true)
                    ??ResearchWave(catalogue,seed,maximumWidth,maximumHeight,out failure,cancellation,roundedEnd:true,shortHead:true):null)
                ??ResearchWave(catalogue,seed,maximumWidth,maximumHeight,out failure,cancellation);
            if(wave!=null)
                try
                {
                    var ground=wave.PreparedGround??RiverPieceGroundAssembler.Assemble(catalogue,wave.Chain,seed,cancellation,verifyCovers:true,verifyChannel:true);
                    return new(wave.Chain,wave.Shape){PreparedGround=ground};
                }
                catch(System.IO.InvalidDataException error){failure=error.Message;}
        }
        int first=new Random(seed^0x371AE519).Next(shapes.Length);
        for(int attempt=0;attempt<shapes.Length;attempt++)
        {
            cancellation.ThrowIfCancellationRequested();string shape=shapes[(first+attempt)%shapes.Length];
            var chain=shape is "curved-reach" or "mixed-curves"
                ?ResearchCurved(catalogue,seed,maximumWidth,maximumHeight,out failure,cancellation,shape=="curved-reach"?1:2+new Random(seed^0x619023).Next(2))?.Chain
                :shape.StartsWith("northward",StringComparison.Ordinal)?ResearchNorthward(catalogue,seed,maximumWidth,maximumHeight,out failure,cancellation,bendRepeats:shape=="northward-double"?2:1)?.Chain:shape=="successive"
                ?ResearchSuccessive(catalogue,seed,maximumWidth,maximumHeight,out failure,true,true,cancellation,maximumStraightCells:72)?.Chain
                :ResearchHairpin(catalogue,seed,maximumWidth,maximumHeight,out failure,cancellation,shape!="hairpin",balancedBend:true,maximumStraightCells:72,bendRepeats:shape=="double-compound"?2:1)?.Chain;
            if(chain!=null)
            {
                try
                {
                    var ground=RiverPieceGroundAssembler.Assemble(catalogue,chain,seed,cancellation,verifyCovers:true,verifyChannel:shape is "curved-reach" or "mixed-curves");
                    return new(chain,shape){PreparedGround=ground};
                }
                catch(System.IO.InvalidDataException error){failure=error.Message;continue;}
            }
        }
        // Compact areas may fit only the shorter reversing course.
        var compact=ResearchHairpin(catalogue,seed,maximumWidth,maximumHeight,out failure,cancellation,maximumStraightCells:72)?.Chain;
        if(compact==null)return null;
        try
        {
            var ground=RiverPieceGroundAssembler.Assemble(catalogue,compact,seed,cancellation,verifyCovers:true);
            return new(compact,"hairpin"){PreparedGround=ground};
        }
        catch(System.IO.InvalidDataException error){failure=error.Message;return null;}
    }

    public static Result? ResearchHairpin(RiverCoastPieces catalogue,int seed,int maximumWidth,int maximumHeight,
        out string failure,CancellationToken cancellation=default,bool meander=false,bool balancedBend=false,int maximumStraightCells=0,int bendRepeats=1,bool varyBends=false,
        IReadOnlyList<(string Id,bool Reverse)>? upperBend=null,IReadOnlyList<(string Id,bool Reverse)>? lowerBend=null,IReadOnlyList<BendPair>? bendPairs=null,bool compactEnds=false,
        int minimumBankSpacing=12,int maximumBankSpacing=20,int pairedOffsetX=0,int pairedOffsetY=0)
    {
        var random=new Random(seed);var pieces=catalogue.Pieces.ToDictionary(p=>p.Id);
        var options=new List<(int D,int B,int A,int C,int N,int K,int M,int T)>();
        if(bendPairs!=null&&(bendPairs.Count!=bendRepeats||!meander||!balancedBend||varyBends))
            throw new ArgumentException("Each mixed bend needs one corresponding pair of native shore routes.",nameof(bendPairs));
        var bendLengths=Enumerable.Range(0,bendRepeats).Select(_=>(Horizontal:varyBends?random.Next(2):0,Vertical:varyBends?random.Next(2):0)).ToArray();
        int bendIndex=0;
        // A hairpin reverses the direction of travel. Its two shores use the
        // native 9/12/7-cell periods; their lengths must be solved together.
        for(int d=1;d<=(compactEnds?1:5);d++)for(int b=1;b<=7;b++)for(int a=2;a<=10;a++)
        for(int n=compactEnds?0:2;n<=(compactEnds?0:16);n+=2)for(int k=0;k<=6;k++)
        for(int closingLength=1;closingLength<=(maximumStraightCells>0?3:1);closingLength+=2)
        {
            // Balanced native bend pairs have equal opposite shore displacements.
            // The original unbalanced pair adds twelve cells vertically;
            // compensate in the solved return rather than widening the water.
            // Experimental native repeats can change the summed shore displacement.
            // Solve their explicit offset in the closing reaches; normal routes use zero.
            int horizontal=12*(d+b)-8*a+pairedOffsetX,vertical=9*(n+closingLength)+31-12*k-(meander&&!balancedBend?12*bendRepeats:0)-pairedOffsetY,spacing=12*b-8*a;
            if(spacing<minimumBankSpacing||spacing>maximumBankSpacing||horizontal<16||horizontal%8!=0||vertical<7||vertical%7!=0)continue;
            if(maximumStraightCells>0&&new[]{12*d,12*b,8*a,horizontal,9*n,12*k,vertical}.Max()>maximumStraightCells)continue;
            options.Add((d,b,a,horizontal/8,n,k,vertical/7,closingLength));
        }
        failure="No native hairpin bank outline fits this area.";
        foreach(var at in options.OrderBy(_=>random.Next()))
        {
            cancellation.ThrowIfCancellationRequested();
            var placed=new List<RiverCoastPieces.Placement>{new(pieces["north-straight"],0,0,false)};
            bool ok=Repeat("north-straight",at.D-1)&&Append("north-west-concave-join")&&Repeat("west-bank",at.N)
                &&Append("west-concave-entry")&&Append("west-south-even-join")&&Bends(InnerBend,true)&&Repeat("south-straight",at.A)
                &&Append("south-west-turn-entry")&&Append("west-diagonal-entry")&&Append("west-diagonal-exit")
                &&Append("west-north-offset-join")&&Repeat("north-straight",at.B)&&Bends(OuterBend)&&Append("north-east-turn")
                &&Append("east-turn-entry")&&Repeat("east-bank",at.K)&&Append("east-narrow-entry")
                &&Repeat("east-narrow-straight",at.M)&&Append("east-narrow-cap")&&Append("south-cap-exit")
                &&Repeat("south-straight",at.C)&&Append("south-west-turn-entry")&&Append("west-turn-entry")
                &&Repeat("west-bank",at.T)&&Append("northwest-corner");
            if(!ok)continue;
            var chain=catalogue.ResearchAssemble(placed,out string rejected);
            if(chain==null){failure=rejected;continue;}
            if(!chain.Closed){failure="The hairpin's paired native bank lengths do not close.";continue;}
            if((varyBends||bendPairs!=null||upperBend!=null||lowerBend!=null)&&maximumStraightCells>0)
            {
                (string Id,bool Reverse)? previous=null;int run=0,longest=0;
                foreach(var part in chain.Pieces)
                {
                    var key=(part.Piece.Id,part.Reverse);run=previous==key?run+1:1;previous=key;
                    int period=part.Piece.Id switch { "north-straight"=>12,"south-straight"=>8,"east-bank"=>12,"west-bank"=>9,"east-narrow-straight"=>7,_=>0 };
                    longest=Math.Max(longest,run*period);
                }
                if(longest>maximumStraightCells)continue;
            }
            var bounds=RiverPieceGroundAssembler.Measure(catalogue,chain);
            if(bounds.Width>maximumWidth||bounds.Height>maximumHeight)continue;
            failure="";return new(chain,at.N,at.K,at.M,at.D,at.B,at.A,at.C,-1){ClosingLength=at.T};

            bool Bends(Func<bool> bend,bool reverse=false)
            {
                if(!meander)return true;
                for(int i=0;i<bendRepeats;i++){bendIndex=reverse?bendRepeats-1-i:i;if(!bend())return false;}
                return true;
            }
            bool Repeat(string id,int count){for(int i=0;i<count;i++)if(!Append(id))return false;return true;}
            bool BendRoute(IReadOnlyList<(string Id,bool Reverse)> route)
            {
                foreach(var step in route)if(!Append(step.Id,step.Reverse))return false;return true;
            }
            bool OuterBend()=>bendPairs!=null?BendRoute(bendPairs[bendIndex].Upper):upperBend!=null?BendRoute(upperBend):DefaultOuterBend();
            bool InnerBend()=>bendPairs!=null?BendRoute(bendPairs[bendIndex].Lower):lowerBend!=null?BendRoute(lowerBend):DefaultInnerBend();
            bool DefaultOuterBend()=>Repeat("north-straight",2*bendLengths[bendIndex].Horizontal)&&Append("north-east-turn")&&Append("east-turn-entry")&&Repeat("east-bank",(balancedBend?2:3)+3*bendLengths[bendIndex].Vertical)
                &&Append("east-southeast-turn")&&Append("southeast-turn-entry")&&Append("southeast-turn-exit")&&Append("southeast-north-turn");
            bool DefaultInnerBend()=>Repeat("south-straight",3+3*bendLengths[bendIndex].Horizontal)&&Append("south-west-turn-entry")&&Append("west-diagonal-entry")
                &&Append("west-diagonal-exit")&&Append("native-1391-1392-4-9")&&Append("west-diagonal-exit")
                &&Repeat("west-bank",1+4*bendLengths[bendIndex].Vertical)&&Append("west-concave-entry")&&Append("west-south-offset-join");
            bool Append(string id,bool? reverse=null)
            {
                var previous=placed[^1];var links=catalogue.Links.Where(l=>l.FromId==previous.Piece.Id&&l.FromReverse==previous.Reverse&&l.ToId==id&&(!reverse.HasValue||l.ToReverse==reverse.Value)).ToArray();
                if(links.Length!=1||!pieces.TryGetValue(id,out var next))return false;
                var link=links[0];placed.Add(new(next,previous.X+link.Dx,previous.Y+link.Dy,link.ToReverse));return true;
            }
        }
        return null;
    }

    public static SuccessiveResult? ResearchSuccessive(RiverCoastPieces catalogue,int seed,int maximumWidth,int maximumHeight,
        out string failure,bool pairedBanks=false,bool nativeDiagonalPairs=false,CancellationToken cancellation=default,int maximumStraightCells=0)
    {
        if(nativeDiagonalPairs)pairedBanks=true;
        var random=new Random(seed);var pieces=catalogue.Pieces.ToDictionary(p=>p.Id);
        if(nativeDiagonalPairs&&new[]{"north-east-turn","east-turn-entry","east-southeast-turn","southeast-turn-entry",
            "southeast-turn-exit","southeast-north-turn","east-south-cap-entry","south-cap-exit","south-west-turn-entry",
            "west-diagonal-entry","west-diagonal-exit","native-1391-1392-4-9","west-concave-entry","west-south-offset-join",
            "west-south-even-join","west-turn-entry","northwest-corner","north-straight","south-straight","east-bank","west-bank"}.Any(id=>!pieces.ContainsKey(id)))
        {failure="The verified reusable river direction pieces are not available in this catalogue.";return null;}
        int firstParallelExtension=nativeDiagonalPairs?random.Next(4):0,secondParallelExtension=nativeDiagonalPairs?random.Next(3):0;
        if(firstParallelExtension+secondParallelExtension>4)secondParallelExtension=4-firstParallelExtension;
        var options=new List<(int D,int K1,int K2,int I1,int I2,int W1,int W2,int T)>();
        for(int d=1;d<=(pairedBanks?1:7);d++)for(int k1=1;k1<=(nativeDiagonalPairs?12:pairedBanks?8:6);k1++)for(int k2=1;k2<=(nativeDiagonalPairs?12:pairedBanks?8:6);k2++)
        for(int i1=1;i1<=(pairedBanks?9:5);i1+=2)for(int i2=1;i2<=(pairedBanks?9:5);i2+=2)
        {
            int vertical=4*(k1+k2),horizontal=12*d+64-6*(i1+i2);
            // Both sides must describe a channel rather than a broad pool.
            // Compare corresponding bend displacements before compilation.
            // The return bank is traversed in the opposite direction: its
            // first bend corresponds to the outer bank's second bend.
            int firstRise=3+12*(pairedBanks?k2:k1)-(nativeDiagonalPairs?18*i1+9:9*i1),secondRise=2+12*(pairedBanks?k1:k2)-(nativeDiagonalPairs?18*i2+9:9*i2);
            if(firstRise<6||firstRise>(pairedBanks?32:18)||secondRise<5||secondRise>(pairedBanks?31:17))continue;
            if(vertical%3!=0||horizontal<16||horizontal%8!=0)continue;
            int t=vertical/3-(nativeDiagonalPairs?2*(i1+i2)+2:i1+i2),w=horizontal/8;if(t<2||t>12)continue;
            for(int w1=1;w1<w;w1++)
                if(Math.Abs(30-8*w1-6*i1+12)<=(pairedBanks?12:4))options.Add((d,k1,k2,i1,i2,w1,w-w1,t));
        }
        failure="No successive native bends fit this area.";
        var ordered=options.OrderBy(_=>random.Next()).Take(200).ToArray();
        var extensions=new List<(int First,int Second)>{(firstParallelExtension,secondParallelExtension)};
        if(nativeDiagonalPairs)
            extensions.AddRange(Enumerable.Range(0,4).SelectMany(a=>Enumerable.Range(0,3).Select(b=>(First:a,Second:b)))
                .Where(p=>p.First+p.Second<firstParallelExtension+secondParallelExtension)
                .OrderByDescending(p=>p.First+p.Second).ThenBy(_=>random.Next()));
        foreach(var extension in extensions)
        foreach(var at in ordered)
        {
            cancellation.ThrowIfCancellationRequested();
            var placed=new List<RiverCoastPieces.Placement>{new(pieces["north-straight"],0,0,false)};
            // Extend a reach on both shores together. Three east sections
            // and four west sections each add 36 cells; unlike extending
            // the cap, this varies length without widening the channel.
            int firstVertical=at.K1+3*extension.First,secondVertical=at.K2+3*extension.Second;
            int closingLength=at.T+4*extension.First;
            if(maximumStraightCells>0&&new[]{12*firstVertical,12*secondVertical,9*closingLength,9*(1+4*extension.Second),12*at.D,8*at.W1,8*at.W2}.Max()>maximumStraightCells)continue;
            bool ok=Repeat("north-straight",at.D-1)&&Outer(firstVertical)&&Outer(secondVertical)
                &&Append("north-east-turn")&&Append("east-south-cap-entry")&&Append("south-cap-exit")
                &&Inner(at.W1,at.I1,"west-diagonal-native-ground-01","west-south-offset-join",extension.Second)
                &&Inner(at.W2,at.I2,"west-diagonal-native-ground-11","west-south-even-join",0)
                &&Append("south-west-turn-entry")&&Append("west-turn-entry")&&Repeat("west-bank",closingLength)&&Append("northwest-corner");
            if(!ok)continue;
            var chain=catalogue.ResearchAssemble(placed,out string rejected);
            if(chain==null){failure=rejected;continue;}
            if(!chain.Closed){failure="Successive native-bank integer lengths did not close.";continue;}
            var bounds=RiverPieceGroundAssembler.Measure(catalogue,chain);
            if(bounds.Width>maximumWidth||bounds.Height>maximumHeight)continue;
            failure="";return new(chain,at.D,firstVertical,secondVertical,at.I1,at.I2,at.W1,at.W2,closingLength);

            bool Outer(int count)=>Append("north-east-turn")&&Append("east-turn-entry")&&Repeat("east-bank",count)
                &&Append("east-southeast-turn")&&Append("southeast-turn-entry")&&Append("southeast-turn-exit")&&Append("southeast-north-turn");
            bool Inner(int count,int diagonals,string ground,string corner,int parallelExtension)=>Repeat("south-straight",count)
                &&Append("south-west-turn-entry")&&Append("west-diagonal-entry")&&Diagonal(diagonals)
                &&Append(nativeDiagonalPairs?"west-diagonal-exit":ground)&&(!nativeDiagonalPairs||Repeat("west-bank",1+4*parallelExtension))&&Append("west-concave-entry")&&Append(corner);
            bool Diagonal(int count)
            {
                if(!nativeDiagonalPairs)return Repeat("west-diagonal-covered-join",count);
                // Alternate two measured native bank directions. Their
                // repeated seams need no scenery, unlike 1391-to-1391.
                for(int i=0;i<count;i++)
                    if(!Append("west-diagonal-exit")||!Append("native-1391-1392-4-9"))return false;
                return true;
            }
            bool Repeat(string id,int count)
            {
                for(int i=0;i<count;i++)if(!Append(id))return false;return true;
            }
            bool Append(string id)
            {
                var previous=placed[^1];var links=catalogue.Links.Where(l=>l.FromId==previous.Piece.Id&&l.FromReverse==previous.Reverse&&l.ToId==id).ToArray();
                if(links.Length!=1||!pieces.TryGetValue(id,out var next))return false;
                var link=links[0];placed.Add(new(next,previous.X+link.Dx,previous.Y+link.Dy,link.ToReverse));return true;
            }
        }
        return null;
    }

    public static Result? Research(RiverCoastPieces catalogue,int seed,int maximumWidth,int maximumHeight,
        out string failure,bool diagonal=false,CancellationToken cancellation=default)
    {
        var random=new Random(seed);var pieces=catalogue.Pieces.ToDictionary(p=>p.Id);
        var options=new List<(int D,int B,int A,int C,int N,int K,int M,int O)>();
        // Native horizontal banks repeat at 12/8 cells; vertical banks at
        // 9/12/7 cells. Solve exact displacement, rather than fit strips or
        // copy a saved complete course. Even inner length keeps local native
        // ground at its verified placement phase.
        for(int d=1;d<=5;d++)for(int b=1;b<=7;b++)
        for(int a=2;a<=10;a++)for(int n=2;n<=16;n+=2)for(int k=0;k<=6;k++)
        for(int o=diagonal?1:-1;o<=(diagonal?15:-1);o+=2)
        {
            int shift=diagonal?-6*(n-1)+7*(o+1):0;
            int horizontal=12*(d+b)-8*a+shift,vertical=9*n+31-12*k-(diagonal?7*(o+1):0);
            int channelSpacing=12*b-8*a;
            if(channelSpacing<12||channelSpacing>20||channelSpacing+shift<12||channelSpacing+shift>20
                ||horizontal<16||horizontal%8!=0||vertical<7||vertical%7!=0)continue;
            options.Add((d,b,a,horizontal/8,n,k,vertical/7,o));
        }
        failure="No compatible piece outline fits this area.";
        foreach(var at in options.OrderBy(_=>random.Next()).Take(100))
        {
            cancellation.ThrowIfCancellationRequested();
            var placed=new List<RiverCoastPieces.Placement>{new(pieces["north-straight"],0,0,false)};
            bool ok=Repeat("north-straight",at.D-1)&&Append("north-west-concave-join")
                &&(diagonal?Append("native-1391-1392-4-9")&&Repeat("west-diagonal-covered-join",at.N-2)&&Append("west-diagonal-native-ground-01"):Repeat("west-bank",at.N))
                &&Append("west-concave-entry")&&Append("west-south-concave-join")&&Repeat("south-straight",at.A)
                &&Append("south-west-turn-entry")&&Append("west-diagonal-entry")&&Append("west-diagonal-native-ground-01")
                &&Append("west-north-offset-join")&&Repeat("north-straight",at.B)&&Append("north-east-turn")
                &&Append("east-turn-entry")&&Repeat("east-bank",at.K)
                &&(diagonal?Append("east-diagonal-entry")&&Repeat("east-diagonal",at.O)&&Append("native-1384-1393-5-5"):Append("east-narrow-entry"))
                &&Repeat("east-narrow-straight",at.M)&&Append("east-narrow-cap")&&Append("south-cap-exit")
                &&Repeat("south-straight",at.C)&&Append("south-west-turn-entry")&&Append("west-turn-entry")&&Append("northwest-corner");
            if(!ok)continue;
            var chain=catalogue.ResearchAssemble(placed,out string rejected);
            if(chain==null){failure=rejected;continue;}
            if(!chain.Closed){failure="Native bank displacements did not close.";continue;}
            int width=chain.Cells.Max(c=>c.X)-chain.Cells.Min(c=>c.X)+1;
            int height=chain.Cells.Max(c=>c.Y)-chain.Cells.Min(c=>c.Y)+1;
            if(width>maximumWidth||height>maximumHeight)continue;
            failure="";return new(chain,at.N,at.K,at.M,at.D,at.B,at.A,at.C,at.O);

            bool Repeat(string id,int count)
            {
                for(int i=0;i<count;i++)if(!Append(id))return false;
                return true;
            }
            bool Append(string id)
            {
                var previous=placed[^1];var links=catalogue.Links.Where(l=>l.FromId==previous.Piece.Id&&l.FromReverse==previous.Reverse&&l.ToId==id).ToArray();
                if(links.Length!=1||!pieces.TryGetValue(id,out var next))return false;
                var link=links[0];placed.Add(new(next,previous.X+link.Dx,previous.Y+link.Dy,link.ToReverse));return true;
            }
        }
        return null;
    }
}
