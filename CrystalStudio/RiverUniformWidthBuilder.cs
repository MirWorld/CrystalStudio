namespace CrystalStudio;

/// <summary>Balance straight and diagonal shore spacing using complete native bank joins.</summary>
public static class RiverUniformWidthBuilder
{
    public static RiverPieceRiverBuilder.WindingResult? Build(RiverCoastPieces catalogue,
        RiverPieceRiverBuilder.WindingResult source,int seed,int maximumWidth,int maximumHeight,out string failure,
        CancellationToken cancellation=default)
    {
        var random=new Random(RiverSeed.Derive(seed,40));
        RiverTerminalCourses.Course[] courses=[RiverTerminalCourses.Short,RiverTerminalCourses.Descending([0],[]),RiverTerminalCourses.Diagonal];
        failure="No complete native terminal fits this course.";
        foreach(var course in courses.OrderBy(_=>random.Next()))
        {
            var result=BuildCourse(catalogue,source,seed,maximumWidth,maximumHeight,course,out failure,cancellation);
            if(result!=null)return result;
        }
        return null;
    }

    internal static RiverPieceRiverBuilder.WindingResult? BuildCourse(RiverCoastPieces catalogue,
        RiverPieceRiverBuilder.WindingResult source,int seed,int maximumWidth,int maximumHeight,RiverTerminalCourses.Course terminal,
        out string failure,CancellationToken cancellation=default)
    {
        failure="The wider native head and terminal do not fit this course.";
        var original=source.Chain.Pieces;var steps=new List<(string Id,bool Reverse)>();bool narrow=false;
        if(original.Count==0||source.BranchCount!=0)return null;
        int firstWest=original.ToList().FindIndex(p=>p.Piece.Id=="west-diagonal"&&p.Reverse);
        int lastEast=original.ToList().FindLastIndex(p=>p.Piece.Id=="east-diagonal"&&!p.Reverse);
        for(int i=0;i<original.Count;i++)
        {
            cancellation.ThrowIfCancellationRequested();
            var part=original[i];string id=part.Piece.Id;
            // Move a matched eight-cell reach from one end of the descending
            // bank to the other. Both bend endpoints stay fixed.
            if(id=="south-straight"&&part.Reverse&&i+3<original.Count
                &&original[i+1].Piece.Id=="native-1388-1389-7-m3"
                &&original[i+2].Piece.Id=="native-1388-1390-m3-1"&&original[i+3].Piece.Id=="west-diagonal-entry")
            {narrow=true;continue;}
            if(id=="west-north-offset-join"&&!part.Reverse&&i>0&&original[i-1].Piece.Id=="west-diagonal-exit")
                steps.Add(("west-bank",true));
            if(id=="diagonal-gentle-cap")
            {
                // Each terminal balances the wider head and its own paired
                // approach lengths, rather than forcing every river into a hook.
                steps.AddRange(terminal.Steps);
                continue;
            }
            steps.Add((id,part.Reverse));
            if(i==firstWest)steps.AddRange(Enumerable.Repeat(("west-diagonal",true),terminal.WestPeriods));
            if(i==lastEast)steps.AddRange(Enumerable.Repeat(("east-diagonal",false),terminal.EastPeriods));
            if(narrow&&id=="west-south-offset-join")
            {steps.Add(("south-straight",true));narrow=false;}
        }
        var pieces=catalogue.Pieces.ToDictionary(p=>p.Id);var placed=new List<RiverCoastPieces.Placement>{original[0]};
        foreach(var step in steps.Skip(1))
        {
            var previous=placed[^1];var link=catalogue.FindConnection(previous.Piece.Id,previous.Reverse,step.Id,step.Reverse);
            if(link==null){failure=$"No native width connection: {previous.Piece.Id} to {step.Id}.";return null;}
            placed.Add(new(pieces[step.Id],previous.X+link.Dx,previous.Y+link.Dy,step.Reverse));
        }
        var chain=catalogue.ResearchAssemble(placed,out failure);
        if(chain is not {Closed:true})return null;
        var bounds=RiverPieceGroundAssembler.Measure(catalogue,chain);
        if(bounds.Width>maximumWidth||bounds.Height>maximumHeight||(long)bounds.Width*bounds.Height*96>32_000_000)
        {failure="The complete native banks do not fit this area.";return null;}
        try
        {
            var ground=RiverPieceGroundAssembler.Assemble(catalogue,chain,seed,cancellation,verifyCovers:true,verifyChannel:true);
            var quality=RiverShapeQuality.Analyse(ground,cancellation);
            if(!quality.SimpleGentleChannel){failure=$"Width-balanced course: {quality.Ends} ends, {quality.Junctions} junctions, {quality.Elbows.Count} elbows.";return null;}
            failure="";
            return new(chain,source.Shape+"-balanced-width-end-"+terminal.Name){PreparedGround=ground};
        }
        catch(System.IO.InvalidDataException error){failure=error.Message;return null;}
    }
}
