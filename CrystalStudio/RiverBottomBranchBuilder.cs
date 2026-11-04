namespace CrystalStudio;

/// <summary>Grow a south-side tributary from complete existing bank pieces.</summary>
internal static class RiverBottomBranchBuilder
{
    internal static RiverPieceRiverBuilder.WindingResult? Build(RiverCoastPieces catalogue,
        RiverCoastPieces.Chain source,int seed,int maximumWidth,int maximumHeight,out string failure,
        CancellationToken cancellation=default,Action<RiverPieceGroundAssembler.Result,RiverShapeQuality.Report>? inspect=null,int parentDepth=0,int parentBranchId=0,
        string? preferredCourse=null)
    {
        failure="No complete bottom-side tributary fits this course.";
        var original=source.Pieces;var pieces=catalogue.Pieces.ToDictionary(p=>p.Id);
        var random=new Random(seed^0x6712A);var sites=new List<(int Lower,int Upper)>();
        int existing=original.Count(p=>p.Piece.Id is "diagonal-fork-concave" or "tributary-south-join");
        for(int i=1;i<original.Count;i++)
        {
            var lower=original[i];
            if(lower.BranchId!=parentBranchId||lower.BranchDepth!=parentDepth||lower.Piece.Id!="south-straight"||!lower.Reverse||original[i-1].Piece.Id=="south-straight")continue;
            int upper=Enumerable.Range(i+1,original.Count-i-1).Where(j=>original[j].BranchId==parentBranchId&&original[j].Piece.Id=="north-straight"&&!original[j].Reverse
                &&lower.Y-original[j].Y is >=20 and <=40&&Math.Abs(lower.X-original[j].X)<40)
                .OrderBy(j=>Math.Abs(lower.X-original[j].X)).FirstOrDefault(-1);
            if(upper>=0)sites.Add((i,upper));
        }
        if(sites.Count==0)return null;
        var routes=RiverLimbBends.BottomCourses(catalogue,seed,maximumWidth,maximumHeight,parentDepth,cancellation);
        var ordered=sites.OrderBy(_=>random.Next()).ToArray();int attempts=0;
        var routeGroundAttempts=new Dictionary<string,int>();string lastGroundFailure=failure;
        foreach(var route in routes.Where(r=>preferredCourse==null||r.Name==preferredCourse)
            .OrderBy(r=>RiverLimbBends.Repetitions(catalogue,source,r)))
        foreach(int padding in new[]{0,3,6})
        foreach(var site in ordered)
        {
            if(routeGroundAttempts.GetValueOrDefault(route.Name)>=2)continue;
            int run=original.Skip(site.Lower).TakeWhile(p=>p.Piece.Id=="south-straight"&&p.Reverse).Count();
            int extra=Math.Max(0,(3-run+2)/3*3)+padding;
            var extended=new List<(string Id,bool Reverse,int Depth,int Branch)>();
            for(int i=0;i<original.Count;i++)
            {
                if(i==site.Lower)extended.AddRange(Enumerable.Repeat(("south-straight",true,parentDepth,parentBranchId),extra));
                if(i==site.Upper)extended.AddRange(Enumerable.Repeat(("north-straight",false,parentDepth,parentBranchId),extra/3*2));
                extended.Add((original[i].Piece.Id,original[i].Reverse,original[i].BranchDepth,original[i].BranchId));
            }
            foreach(int offset in Enumerable.Range(0,run+extra-2).OrderBy(_=>random.Next()).Take(3))
            {
                cancellation.ThrowIfCancellationRequested();
                var steps=new List<(string Id,bool Reverse,int Depth,int Branch)>(extended);int start=site.Lower+offset;
                steps.RemoveRange(start,3);
                steps.InsertRange(start,route.Steps.Select(p=>(p.Id,p.Reverse,parentDepth+1,original.Max(p=>p.BranchId)+1)));
                var placed=new List<RiverCoastPieces.Placement>{original[0]};bool valid=true;
                foreach(var step in steps.Skip(1))
                {
                    var previous=placed[^1];var link=catalogue.FindConnection(previous.Piece.Id,previous.Reverse,step.Id,step.Reverse);
                    if(link==null||!pieces.TryGetValue(step.Id,out var piece)){valid=false;break;}
                    placed.Add(new(piece,previous.X+link.Dx,previous.Y+link.Dy,step.Reverse){BranchDepth=step.Depth,BranchId=step.Branch});
                }
                if(!valid)continue;
                var chain=catalogue.ResearchAssemble(placed,out failure);if(chain is not {Closed:true})continue;
                var bounds=RiverPieceGroundAssembler.Measure(catalogue,chain);
                if(bounds.Width>maximumWidth||bounds.Height>maximumHeight||(long)bounds.Width*bounds.Height*96>32_000_000)continue;
                if(++attempts>8){failure=lastGroundFailure;return null;}
                routeGroundAttempts[route.Name]=routeGroundAttempts.GetValueOrDefault(route.Name)+1;
                var result=RiverBranchAssembly.Build(catalogue,chain,seed,existing+1,
                    $"bottom-fork-reach-{site.Lower}-offset-{offset}-depth-{parentDepth+1}-{route.Name}",out failure,cancellation,inspect);
                if(result!=null)return result;
                lastGroundFailure=failure;
                if(routeGroundAttempts[route.Name]>=2)break;
            }
        }
        failure=lastGroundFailure;
        return null;
    }
}
