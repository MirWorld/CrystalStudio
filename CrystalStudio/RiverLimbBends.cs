namespace CrystalStudio;

/// <summary>Matched original bank turns for long limbs, without stretching straight banks.</summary>
internal static class RiverLimbBends
{
    internal sealed record Course(string Name,IReadOnlyList<(string Id,bool Reverse)> Steps);
    internal static int Repetitions(RiverCoastPieces catalogue,RiverCoastPieces.Chain chain,Course course)
    {
        var existing=chain.Pieces.Where(p=>p.BranchDepth>0).GroupBy(p=>p.BranchId)
            .Select(g=>g.Select(p=>(p.Piece.Id,p.Reverse)).ToArray()).ToArray();
        int direction=Direction(catalogue,course.Steps);
        // Different lengths of the same diagonal still look like a comb.
        // Prefer an unused outward bearing before comparing exact recipes.
        return existing.Count(steps=>Direction(catalogue,steps)==direction)*10
            +existing.Count(steps=>steps.SequenceEqual(course.Steps));
    }
    internal static int Direction(RiverCoastPieces catalogue,IEnumerable<(string Id,bool Reverse)> steps)
    {
        double x=0,y=0,furthest=0,dx=0,dy=0;var path=new List<(double X,double Y)>{(0,0)};
        var pieces=catalogue.Pieces.ToDictionary(p=>p.Id);
        foreach(var step in steps)
        {
            var piece=pieces[step.Id];var start=step.Reverse?piece.End:piece.Start;var end=step.Reverse?piece.Start:piece.End;
            x+=(end.BankX-start.BankX)*1.5;y+=end.BankY-start.BankY;
            path.Add((x,y));
        }
        // The route replaces a horizontal bank span. Its mouth is midway
        // between the two attachment ports, not at the first port.
        foreach(var point in path)
        {
            double px=point.X-x/2,py=point.Y-y/2,distance=px*px+py*py;
            if(distance>furthest){furthest=distance;dx=px;dy=py;}
        }
        return ((int)Math.Round(Math.Atan2(-dy,dx)/(Math.PI/8))+16)%16;
    }
    internal static IReadOnlyList<Course> Courses(RiverCoastPieces catalogue,int seed,int width,int height,int depth,CancellationToken cancellation)
    {
        var routes=new List<Course>();var random=new Random(seed^0x751837);
        int maximum=Math.Clamp(Math.Min(width/160,height/100)/(depth+1),1,3);
        foreach(int groups in new[]{random.Next(1,maximum+1),1,maximum}.Distinct())
        {
            var limb=RiverBankOnlyWindingBuilder.Build(catalogue,seed,width,height,out _,cancellation,
                bendRepeats:groups,northwardPeriods:Enumerable.Range(0,groups).Select(_=>random.Next(0,Math.Min(2,(7-groups)/groups)+1)).ToArray(),
                riseBeforeGroup:Enumerable.Range(0,groups).Select(_=>random.Next(2)==0).ToArray(),
                verifyFinalGround:false);
            if(limb==null||limb.Chain.Pieces[^1].Piece.Id!="diagonal-gentle-cap")continue;
            var body=limb.Chain.Pieces.SkipLast(1).Select(p=>(p.Piece.Id,p.Reverse));
            routes.Add(new($"course-{groups}",[("diagonal-fork-concave",false),
                ..Enumerable.Repeat(("west-diagonal",true),11),..body,
                ..Enumerable.Repeat(("east-diagonal",false),3),
                ("native-1384-1393-5-5",false),("east-narrow-straight",false),("native-1384-1393-m2-m9",true),
                ..Enumerable.Repeat(("east-diagonal",false),3),
                ("native-1384-1393-5-5",false),("east-narrow-straight",false),("native-1384-1393-m2-m9",true),
                ..Enumerable.Repeat(("east-diagonal",false),4),
                ("native-1384-1393-5-5",false),("east-narrow-straight",false),("native-1384-1393-m2-m9",true),("diagonal-fork-return",false)]));
        }
        foreach(var (vertical,diagonal) in new[]{(0,0),(0,1),(1,1),(0,2)})
            routes.Add(new($"limb-v{vertical}-d{diagonal}",[("diagonal-fork-concave",false),
                ..Enumerable.Repeat(("west-diagonal",true),4+7*diagonal),("west-diagonal-exit",false),
                ..Enumerable.Repeat(("west-bank",true),4*vertical),
                ("west-north-offset-join",false),("native-1377-1379-15-6",false),
                ("native-1379-1382-10-2",false),("east-turn-entry",false),
                ..Enumerable.Repeat(("east-bank",false),3*vertical),("east-diagonal-entry",false),
                ..Diagonal(4,diagonal),("diagonal-fork-return",false)]));
        foreach(var (vertical,diagonal) in new[]{(3,1),(6,1)})
            routes.Add(new($"limb-turn-v{vertical}-d{diagonal}",[("diagonal-fork-concave",false),
                ..Enumerable.Repeat(("west-diagonal",true),4+7*diagonal),("west-diagonal-exit",false),
                ..Enumerable.Repeat(("west-bank",true),4*(vertical-1)),..RiverTerminalCourses.WestJog(1),
                ..Enumerable.Repeat(("west-bank",true),4),("west-north-offset-join",false),
                ("native-1377-1379-15-6",false),("native-1379-1382-10-2",false),("east-turn-entry",false),
                ..Enumerable.Repeat(("east-bank",false),3),..RiverTerminalCourses.EastJog(1),
                ..Enumerable.Repeat(("east-bank",false),3*(vertical-1)),("east-diagonal-entry",false),
                ..Diagonal(4,diagonal),("diagonal-fork-return",false)]));
        return routes.OrderBy(_=>random.Next()).ToArray();
    }
    internal static IReadOnlyList<Course> BottomCourses(RiverCoastPieces catalogue,int seed,int width,int height,int depth,CancellationToken cancellation)
    {
        var random=new Random(RiverSeed.Derive(seed,21));
        return (from vertical in new[]{0,1,2,3} from diagonal in Enumerable.Range(0,3)
            select new Course($"limb-v{vertical}-d{diagonal}",
            [("tributary-south-join",false),..Enumerable.Repeat(("east-narrow-straight",false),9*vertical),
            ("native-1384-1393-m2-m9",true),
            ..Diagonal(3,diagonal),("diagonal-gentle-cap",false),
            ..Enumerable.Repeat(("west-diagonal",true),4+7*diagonal),("west-diagonal-exit",false),
            ..Enumerable.Repeat(("west-bank",true),7*vertical),
            ("west-concave-entry",false),("west-south-even-join",false)])).OrderBy(_=>random.Next()).ToArray();
    }

    private static IEnumerable<(string Id,bool Reverse)> Diagonal(int initial,int extensions)
    {
        foreach(var step in Enumerable.Repeat(("east-diagonal",false),initial))yield return step;
        // Seven west-bank diagonals span (42,63). Three alternating diagonal
        // and narrow-bank transitions match them without a bulge at the end.
        for(int i=0;i<extensions;i++)
        for(int j=0;j<3;j++)
        {
            yield return ("east-diagonal",false);
            yield return ("native-1384-1393-5-5",false);
            yield return ("native-1384-1393-m2-m9",true);
        }
    }
}
