namespace CrystalStudio;

/// <summary>Paired native approaches and ends; all periods move both banks equally.</summary>
internal static class RiverTerminalCourses
{
    internal sealed record Course(string Name,int EastPeriods,int WestPeriods,IReadOnlyList<(string Id,bool Reverse)> Steps);
    internal static readonly Course Short=new("short",0,0,
        [("native-1384-1393-5-5",false),..Enumerable.Repeat(("east-narrow-straight",false),3),
         ("native-1385-1393-1-m9",true),("native-1385-1386-m1-9",false),("south-cap-exit",false),
         ("south-west-turn-entry",false),("west-diagonal-entry",false)]);
    internal static readonly Course Diagonal=new("diagonal",6,6,
        [("east-diagonal",false),("native-1384-1385-4-7",false),("east-narrow-entry",false),
         ("native-1385-1393-1-m9",true),("native-1385-1386-m1-9",false),("native-1386-1387-m7-7",false),
         ("native-1387-1388-m6-6",false),("native-1387-1388-7-m7",true),("native-1387-1390-m4-4",false),
         ("west-turn-entry",false),("native-1391-1392-4-9",true)]);

    internal static Course Descending(IReadOnlyList<int> vertical,IReadOnlyList<int> jogs)
    {
        var east=new List<(string Id,bool Reverse)>{("native-1384-1385-4-7",false),("east-bank",false)};
        var west=new List<(string Id,bool Reverse)>();
        for(int i=0;i<vertical.Count;i++)
        {
            east.AddRange(Enumerable.Repeat(("east-bank",false),3*vertical[i]));
            if(i<jogs.Count)east.AddRange(EastJog(jogs[i]));
        }
        for(int i=vertical.Count-1;i>=0;i--)
        {
            west.AddRange(Enumerable.Repeat(("west-bank",true),4*vertical[i]));
            if(i>0)west.AddRange(WestJog(jogs[i-1]));
        }
        return new(jogs.Count==0&&vertical.Sum()==0?"rounded":$"descending-{string.Join('-',vertical)}-offsets-{string.Join('-',jogs)}",2,1,
            [..east,("native-1385-1386-m1-9",false),("native-1386-1387-m7-7",false),("south-diagonal",true),
             ("native-1387-1390-m4-4",false),("west-turn-entry",false),..west,("native-1391-1392-4-9",true)]);
    }

    // Each offset moves both banks 42 cells sideways and either 90 or 99
    // vertically. Traversal of the west bank runs in the opposite direction.
    internal static IEnumerable<(string Id,bool Reverse)> EastJog(int mode)
    {
        yield return ("east-diagonal-entry",false);
        for(int i=0;i<5;i++)
            if(mode==1&&i<3)
            {yield return ("native-1384-1393-5-5",false);yield return ("native-1384-1393-m2-m9",true);}
            else yield return ("east-diagonal",false);
        yield return ("native-1384-1385-4-7",false);
        for(int i=0;i<(mode==0?3:2);i++)yield return ("east-bank",false);
    }
    internal static IEnumerable<(string Id,bool Reverse)> WestJog(int mode)
    {
        yield return ("native-1391-1392-4-9",true);
        for(int i=0;i<6;i++)yield return ("west-diagonal",true);
        yield return ("west-diagonal-exit",false);
        for(int i=0;i<(mode==0?2:3);i++)yield return ("west-bank",true);
    }

    internal static RiverPieceRiverBuilder.WindingResult? Vertical(RiverCoastPieces catalogue,int seed,int width,int height,
        out string failure,CancellationToken cancellation)
    {
        failure="No complete vertical native course fits this area.";
        var random=new Random(RiverSeed.Derive(seed,41));
        for(int attempt=0;attempt<6;attempt++)
        {
            cancellation.ThrowIfCancellationRequested();
            int rises=attempt<4?random.Next(2):0;
            int sourceWidth=Math.Min(276,width<320?width-56:width);
            var source=RiverBankOnlyWindingBuilder.Build(catalogue,RiverSeed.Derive(seed,42+attempt),sourceWidth,Math.Min(height,240),
                out _,cancellation,northwardPeriods:[rises],bankLeadPeriods:3+random.Next(2),verifyFinalGround:false,
                riseBeforeGroup:[random.Next(2)==0],reachPeriodPattern:rises==0?[random.Next(2),1]:[0,0,1]);
            if(source==null)continue;
            var bounds=RiverPieceGroundAssembler.Measure(catalogue,source.Chain);
            int jogCount=Math.Min(random.Next(1,3),Math.Max(0,(height-bounds.Height-150)/99));
            jogCount=Math.Min(jogCount,Math.Max(0,(width-bounds.Width-14)/42));
            var jogs=Enumerable.Range(0,jogCount).Select(_=>random.Next(2)).ToArray();
            int maximum=Math.Min(3*(jogCount+1),(height-bounds.Height-20-jogs.Sum(j=>j==0?90:99))/36);
            if(maximum<1)continue;
            int periods=random.Next(Math.Max(1,maximum-3),maximum+1);
            var vertical=new int[jogCount+1];
            for(int i=0;i<periods;i++)
            {
                var available=Enumerable.Range(0,vertical.Length).Where(at=>vertical[at]<3).ToArray();
                vertical[available[random.Next(available.Length)]]++;
            }
            var result=RiverUniformWidthBuilder.BuildCourse(catalogue,source,seed,width,height,Descending(vertical,jogs),out failure,cancellation);
            if(result?.PreparedGround is {} ground&&ground.Height>ground.Width*1.5)return result;
            if(result!=null)failure="The available vertical reaches do not produce a predominantly vertical course.";
        }
        return null;
    }
}
