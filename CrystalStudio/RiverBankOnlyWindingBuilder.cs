using System.IO;

namespace CrystalStudio;

/// <summary>Seeded river outlines from source-validated small native bank pieces.</summary>
public static class RiverBankOnlyWindingBuilder
{
    public static RiverPieceRiverBuilder.WindingResult? Build(RiverCoastPieces catalogue,int seed,int maximumWidth,int maximumHeight,
        out string failure,CancellationToken cancellation=default,int bendRepeats=1,IReadOnlyList<int>? northwardPeriods=null,int bankLeadPeriods=3,
        string? oppositeDiagonalId=null,Action<RiverPieceGroundAssembler.Result,RiverShapeQuality.Report>? inspectAssembly=null,int diagonalPeriods=7,
        IReadOnlyList<int>? diagonalPeriodPattern=null,bool risingFirst=false,IReadOnlyList<bool>? riseBeforeGroup=null,bool verifyFinalGround=true,int minimumReachPeriods=0,
        IReadOnlyList<int>? reachPeriodPattern=null)
    {
        failure="The source-validated bank-only diagonal join is unavailable.";
        if(bendRepeats is <1 or >4)throw new ArgumentOutOfRangeException(nameof(bendRepeats));
        if(diagonalPeriods is <3 or >9||(diagonalPeriods&1)==0)throw new ArgumentOutOfRangeException(nameof(diagonalPeriods));
        if(diagonalPeriodPattern!=null&&(diagonalPeriodPattern.Count!=bendRepeats||diagonalPeriodPattern.Any(n=>n is <3 or >9||(n&1)==0)))
            throw new ArgumentException("Each diagonal group needs an odd native repeat count from three to nine.",nameof(diagonalPeriodPattern));
        if(bankLeadPeriods is <1 or >5)throw new ArgumentOutOfRangeException(nameof(bankLeadPeriods));
        if(riseBeforeGroup!=null&&riseBeforeGroup.Count!=bendRepeats)throw new ArgumentException("Each bend group needs a direction order.",nameof(riseBeforeGroup));
        if(northwardPeriods!=null&&(northwardPeriods.Count!=bendRepeats||northwardPeriods.Any(n=>n is <0 or >5)
            ||bendRepeats+northwardPeriods.Sum()+1>8))
            throw new ArgumentException("Matched upstream reaches must fit within eight paired bends.",nameof(northwardPeriods));
        if(!catalogue.Pieces.Any(p=>p.Id=="west-diagonal-parallel-8-9"&&p.NativeFrontBankCommands.Count==20)
            ||!catalogue.Pieces.Any(p=>p.Id=="diagonal-gentle-cap"))return null;
        var random=new Random(seed^0x71526A);
        var counts=diagonalPeriodPattern??Enumerable.Repeat(diagonalPeriods,bendRepeats).ToArray();
        var positions=Enumerable.Range(0,counts[0]).OrderBy(_=>random.Next()).Take((counts[0]+1)/2).ToHashSet();
        bool matchedReturn=oppositeDiagonalId!=null&&catalogue.Pieces.Any(p=>p.Id=="west-vertical-parallel-0-14");
        (string Id,bool Reverse)[] East(int count)=>[("native-1377-1379-15-6",false),("native-1379-1382-10-2",false),("native-1382-1384-4-7",false),
            ..Enumerable.Repeat(("east-diagonal",false),count),("native-1384-1393-5-5",false),("native-1383-1393-m4-m13",true),
            ("southeast-turn-entry",false),("southeast-turn-exit",false),("southeast-north-turn",false)];
        (string Id,bool Reverse)[] West(int count,IReadOnlySet<int> at)=>[..Enumerable.Repeat(("south-straight",true),bankLeadPeriods),
            ("native-1388-1389-7-m3",true),("native-1388-1390-m3-1",false),("west-diagonal-entry",false),
            ..Enumerable.Range(0,count).Select(i=>(at.Contains(i)?"west-diagonal-parallel-8-9":"west-diagonal",true)),
            ("west-diagonal-exit",false),..(matchedReturn?Enumerable.Repeat(("west-vertical-parallel-0-14",true),1):[]),
            ("west-concave-entry",false),("west-south-offset-join",false),
            ..Enumerable.Repeat(("south-straight",true),6-bankLeadPeriods)];
        // Odd repeat counts with (count+1)/2 wider joins retain the same
        // horizontal displacement. Solve the changed vertical displacement
        // in the closing reaches, rather than moving or scaling artwork.
        int offsetX=0,offsetY=-2*(counts.Sum()-7*bendRepeats);
        if(oppositeDiagonalId!=null)
        {
            var native=catalogue.Pieces.Single(p=>p.Id=="east-diagonal");var replacement=catalogue.Pieces.Single(p=>p.Id==oppositeDiagonalId);
            offsetX+=((replacement.End.BankX-replacement.Start.BankX)-(native.End.BankX-native.Start.BankX))*counts.Sum();
            offsetY+=((replacement.End.BankY-replacement.Start.BankY)-(native.End.BankY-native.Start.BankY))*counts.Sum();
            if(matchedReturn)offsetY-=14*bendRepeats;
        }
        var northward=new RiverPieceRiverBuilder.BendPair(
            [("north-west-flexible-join",false),("west-bank",true),("west-north-return-shift-p2",false),
             ("north-west-flexible-join",false),("west-bank",true),("west-north-return-shift-p2",false),("north-straight",false)],
            [("native-1388-1389-7-m3",true),("native-1387-1388-7-m7",true),("south-diagonal",true),("south-diagonal",true),
             ("native-1387-1388-m6-6",false),("native-1388-1389-m6-0",false)]);
        var final=new RiverPieceRiverBuilder.BendPair(East(7),
            [..Enumerable.Repeat(("south-straight",true),4),("native-1388-1389-7-m3",true),("native-1388-1390-m3-1",false),
             ("west-diagonal-entry",false),..Enumerable.Repeat(("west-diagonal",true),7),("west-diagonal-exit",false),
             ("west-concave-entry",false),("west-south-offset-join",false),..Enumerable.Repeat(("south-straight",true),3)]);
        var body=new List<RiverPieceRiverBuilder.BendPair>();var orders=new List<string>();
        for(int group=0;group<bendRepeats;group++)
        {
            var groupRandom=new Random(unchecked((seed^0x6BA49C)+group*104729));
            int count=counts[group];
            var at=group==0?positions:Enumerable.Range(0,count).OrderBy(_=>groupRandom.Next()).Take((count+1)/2).ToHashSet();
            var east=East(count);
            var matchedEast=oppositeDiagonalId==null?east:east.Select(step=>step.Id=="east-diagonal"?(oppositeDiagonalId,step.Reverse):step).ToArray();
            bool riseFirst=riseBeforeGroup?[group]??risingFirst;
            if(!riseFirst)body.Add(new(matchedEast,West(count,at)));
            for(int n=0;n<(northwardPeriods?[group]??1);n++)body.Add(northward);
            if(riseFirst)body.Add(new(matchedEast,West(count,at)));
            orders.Add(string.Join('-',at.Order()));
        }
        body.Add(final);
        if(reachPeriodPattern!=null&&(reachPeriodPattern.Count!=body.Count||reachPeriodPattern.Any(n=>n is <0 or >3)||reachPeriodPattern[^1]==0))
            throw new ArgumentException("Each bend needs zero to three matched reaches and a nonzero terminal reach.",nameof(reachPeriodPattern));
        int[] reaches=[..Enumerable.Range(0,body.Count-1).Select(_=>Math.Max(minimumReachPeriods,random.Next(2))),1];
        if(diagonalPeriodPattern!=null)
        {
            int index=0;
            for(int group=0;group<bendRepeats;group++)
            {
                int northwardCount=northwardPeriods?[group]??1;
                if(counts[group]<7)
                    for(int i=index;i<=index+northwardCount;i++)reaches[i]=Math.Max(reaches[i],counts[group]==3?2:1);
                index+=1+northwardCount;
            }
        }
        if(oppositeDiagonalId!=null)for(int i=0;i<reaches.Length-1;i++)reaches[i]++;
        // Keep rising sections continuous rather than inserting a horizontal
        // reach after every small native turn.
        for(int i=0;i<body.Count-1;i++)
            if(ReferenceEquals(body[i],northward)&&ReferenceEquals(body[i+1],northward))reaches[i]=0;
        // Explicit spacing lets a course alternate short runs and long reaches,
        // including between rising bends. Both shores gain the same 24 cells.
        if(reachPeriodPattern!=null)reaches=reachPeriodPattern.ToArray();
        for(int trial=0;trial<8;trial++)
        {
            cancellation.ThrowIfCancellationRequested();
            int candidateSeed=unchecked(seed+trial*7919);
            var wave=RiverPieceRiverBuilder.ResearchWave(catalogue,candidateSeed,maximumWidth+160,maximumHeight+128,
                out failure,cancellation,shortHead:true,gentleEnd:true,bodyPairs:body,
                bankSpacing:oppositeDiagonalId==null||matchedReturn?(12,20):(24,36),verifyNativeAssembly:false,bodyStraightPeriods:reaches,pairedOffsetX:offsetX,pairedOffsetY:offsetY);
            if(wave==null)continue;
            var chain=RiverPieceRiverBuilder.ResearchDiagonalTerminal(catalogue,wave.Chain,out failure,[("diagonal-gentle-cap",false)]);
            if(chain==null)continue;
            var bounds=RiverPieceGroundAssembler.Measure(catalogue,chain);
            if(bounds.Width>maximumWidth||bounds.Height>maximumHeight||(long)bounds.Width*bounds.Height*96>32_000_000)
            {failure="The complete bank-only river and its artwork do not fit this area.";continue;}
            if(!verifyFinalGround)return new(chain,"native-limb-course");
            try
            {
                var ground=RiverPieceGroundAssembler.Assemble(catalogue,chain,seed,cancellation,verifyCovers:true,verifyChannel:true);
                var quality=RiverShapeQuality.Analyse(ground,cancellation);inspectAssembly?.Invoke(ground,quality);
                if(!quality.SimpleGentleChannel)
                {failure=$"The bank-only river does not form a gradual connected channel ({quality.Ends} ends, {quality.Junctions} junctions, {quality.Elbows.Count} abrupt turns).";continue;}
                failure="";
                string order=string.Join("_",orders)+(bendRepeats==1&&northwardPeriods==null?"":$"-bends-{body.Count}-up-{string.Join('-',northwardPeriods??Enumerable.Repeat(1,bendRepeats).ToArray())}");
                return new(chain,$"bank-only-wave-{order}-reaches-{string.Join('-',reaches)}-gentle-diagonal-end"
                    +(risingFirst?"-rising-first":"")
                    +(riseBeforeGroup==null?"":"-rise-order-"+string.Join('-',riseBeforeGroup.Select(b=>b?1:0)))
                    +(bankLeadPeriods==3?"":$"-bank-lead-{bankLeadPeriods}")
                    +(oppositeDiagonalId==null?"":$"-opposite-{oppositeDiagonalId}")
                    +(diagonalPeriodPattern!=null?$"-diagonal-periods-{string.Join('-',counts)}":diagonalPeriods==7?"":$"-diagonal-periods-{diagonalPeriods}")
                    +(wave.Shape.EndsWith("-short-head",StringComparison.Ordinal)?"-short-head":"")){PreparedGround=ground};
            }
            catch(InvalidDataException error){failure=error.Message;}
        }
        return null;
    }
}
