namespace CrystalStudio;

/// <summary>Research south-facing shores from small untransformed bank pieces.</summary>
public static class RiverHorizontalCoastBuilder
{
    public static RiverPieceCoastBuilder.Result? Research(RiverCoastPieces catalogue,RiverHorizontalBoundaryComponent edge,
        IReadOnlyList<RiverPieceRiverBuilder.BendPair> bends,int seed,int width,int height,out string failure,
        CancellationToken cancellation=default,int minimumLandHeight=0,int minimumStraightPieces=0)
    {
        failure="No compatible horizontal native shoreline fits this map.";
        var pieces=catalogue.Pieces.ToDictionary(p=>p.Id);
        var links=catalogue.Links.GroupBy(l=>(l.FromId,l.FromReverse)).ToDictionary(g=>g.Key,g=>g.ToArray());
        var random=new Random(seed);int nodes=0,trials=0;
        var options=new List<(int A,int B,int C,int N)>();
        // Native curves displace the same endpoint bank by 64, 71 and 42
        // columns. Solve the remaining 12-cell repeats against both edges.
        int maximumBends=width>=512?4:2,maximumReturns=width>=512?4:3,maximumReaches=width>=512?24:12;
        for(int a=0;a<=maximumBends;a++)for(int b=0;b<=2;b++)for(int c=0;c<=maximumReturns;c++)
        {
            int remaining=width-36-64*a-71*b-42*c;
            if(a+b+c>0&&remaining>=0&&remaining%12==0&&remaining/12<=maximumReaches&&remaining/12>=minimumStraightPieces)
                options.Add((a,b,c,remaining/12));
        }
        foreach(var option in options.OrderBy(_=>random.Next()))
        for(int trial=0;trial<6;trial++)
        {
            cancellation.ThrowIfCancellationRequested();
            var placed=new List<RiverCoastPieces.Placement>{new(edge.Piece,0,0,false)};
            var routes=Enumerable.Repeat(bends[0].Upper,option.A).Concat(Enumerable.Repeat(bends[1].Upper,option.B))
                .Concat(Enumerable.Repeat(bends[2].Upper,option.C)).OrderBy(_=>random.Next()).ToList();
            var reaches=new int[routes.Count+1];
            reaches[random.Next(reaches.Length)]=minimumStraightPieces;
            for(int i=minimumStraightPieces;i<option.N;i++)reaches[random.Next(reaches.Length)]++;
            if(reaches.Any(n=>n>6))continue;
            bool valid=true;
            for(int i=0;i<reaches.Length&&valid;i++)
            {
                for(int n=0;n<reaches[i]&&valid;n++)valid=Append("north-straight",false);
                if(i<routes.Count)foreach(var step in routes[i])
                    if(!Append(step.Id,step.Reverse)){valid=false;break;}
            }
            if(!valid||!Append(edge.Piece.Id,false)||placed[^1].X!=width-edge.Width)continue;
            var chain=catalogue.ResearchAssemble(placed,out string rejected);
            if(chain==null||chain.Closed){failure=rejected;continue;}
            if(chain.Cells.Any(c=>c.X<0||c.X>=width))continue;
            var bounds=RiverPieceGroundAssembler.Measure(catalogue,chain);
            int top=bounds.Y+4,bottom=bounds.Y+bounds.Height-4;
            int span=bottom-top;
            if(span+32+minimumLandHeight>height)continue;
            int shift=(16+minimumLandHeight-top+random.Next((height-span-32-minimumLandHeight)/2+1)*2)&~1;
            var translated=new RiverCoastPieces.Chain(placed.Select(p=>p with{Y=p.Y+shift}).ToArray(),
                chain.Cells.Select(c=>new StampCell(c.X,c.Y+shift,c.Cell)).ToArray());
            double startY=shift+edge.Piece.Start.ShoreY,endY=placed[^1].Y+shift+edge.Piece.End.ShoreY;
            (double X,double Y)[] boundary=[(width,endY),(width,height),(0,height),(0,startY)];
            try
            {
                trials++;
                var ground=RiverPieceGroundAssembler.AssembleCoast(catalogue,translated,seed,new(0,0,width,height),boundary,cancellation);
                failure="";return new(translated,ground,nodes,trials);
            }
            catch(System.IO.InvalidDataException error){failure=error.Message;}

            bool Append(string id,bool reverse)
            {
                var previous=placed[^1];nodes++;
                var matching=links[(previous.Piece.Id,previous.Reverse)].Where(l=>l.ToId==id&&l.ToReverse==reverse).ToArray();
                if(matching.Length!=1)return false;
                var link=matching[0];var piece=pieces[id];int x=previous.X+link.Dx,y=previous.Y+link.Dy;
                if(!piece.GroundAligned(x,y))return false;
                placed.Add(new(piece,x,y,reverse));return true;
            }
        }
        return null;
    }
}
