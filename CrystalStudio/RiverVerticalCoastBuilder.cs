namespace CrystalStudio;

/// <summary>Seeded open shores with native inland and seaward reaches.</summary>
internal static class RiverVerticalCoastBuilder
{
    private sealed record Reach((string Id,bool Reverse)[] Steps,int X,int Y,int Bays,int Phases);

    internal static RiverPieceCoastBuilder.Result? Build(RiverCoastPieces catalogue,RiverBoundaryComponent start,
        RiverBoundaryComponent finish,int seed,int width,int height,CancellationToken cancellation)
    {
        var pieces=catalogue.Pieces.ToDictionary(p=>p.Id);
        string inward=pieces.ContainsKey(TerrainCoastObjects.NorthwestCoverVariantId)
            ?TerrainCoastObjects.NorthwestCoverVariantId:"north-west-flexible-join";
        var reaches=new List<Reach>();
        void Reach(params (string Id,bool Reverse)[] steps)
        {
            int x=0,y=0;
            foreach(var (id,reverse) in steps)
            {
                var p=pieces[id];var a=reverse?p.End:p.Start;var b=reverse?p.Start:p.End;
                x+=b.BankX-a.BankX;y+=b.BankY-a.BankY;
            }
            int phases=0;
            for(int origin=0;origin<4;origin++)
            {
                int bx=origin&1,by=origin>>1;bool valid=true;
                foreach(var (id,reverse) in steps)
                {
                    var p=pieces[id];var a=reverse?p.End:p.Start;var b=reverse?p.Start:p.End;
                    int px=bx-a.BankX,py=by-a.BankY;
                    if(!p.GroundAligned(px,py)||id==inward&&(px&1)==0&&(py&1)!=0)valid=false;
                    bx=px+b.BankX;by=py+b.BankY;
                }
                if(valid)phases|=1<<origin;
            }
            if(y>0&&phases!=0)reaches.Add(new(steps,x,y,steps.Any(s=>s.Id is "west-concave-entry" or "native-1377-1379-15-6")?1:0,phases));
        }
        for(int n=0;n<=5;n++)
        {
            Reach(Enumerable.Repeat(("west-diagonal",false),n+1).ToArray());
            Reach(new[]{("native-1391-1392-4-9",false)}.Concat(Enumerable.Repeat(("west-bank",false),n))
                .Append(("west-diagonal-exit",true)).ToArray());
        }
        for(int along=0;along<=4;along++)for(int down=1;down<=4;down++)
            Reach(new[]{("native-1391-1392-4-9",false),(inward,true)}
                .Concat(Enumerable.Repeat(("north-straight",true),along))
                .Append(("west-north-offset-join",true))
                .Concat(Enumerable.Repeat(("west-bank",false),down))
                .Append(("west-diagonal-exit",true)).ToArray());
        for(int diagonal=0;diagonal<=2;diagonal++)for(int along=0;along<=4;along++)
            Reach(new[]{("west-diagonal-entry",true),("native-1387-1390-m4-4",true)}
                .Concat(Enumerable.Repeat(("south-diagonal",false),diagonal))
                .Concat(new[]{("native-1387-1388-7-m7",false),("native-1388-1389-7-m3",false)})
                .Concat(Enumerable.Repeat(("south-straight",false),along))
                .Concat(new[]{("west-south-offset-join",true),("west-concave-entry",true),("west-diagonal-exit",true)}).ToArray());
        // A complete original rounded return can face into a broad bay. Leave
        // its full height ahead of the return so it never folds across the
        // preceding bank. Reversing traversal does not transform its artwork.
        for(int bend=0;bend<2;bend++)for(int lead=9;lead<=12;lead++)for(int tail=1;tail<=3;tail++)
            Reach(new[]{("native-1391-1392-4-9",false)}
                .Concat(Enumerable.Repeat(("west-bank",false),lead)).Append((inward,true))
                .Concat(RiverPieceRiverBuilder.CoastBends[bend].Upper.Reverse().Select(s=>(s.Id,!s.Reverse)))
                .Append(("west-north-offset-join",true))
                .Concat(Enumerable.Repeat(("west-bank",false),tail)).Append(("west-diagonal-exit",true)).ToArray());

        int needed=height-(start.Piece.End.BankY-finish.Piece.Start.BankY+finish.Height);
        if(needed<36)return null;
        // Solve height and ground phase before assembling pixels. Bays with a
        // non-nine-cell height are used only as needed to meet the actual edge.
        const int impossible=10000;
        var cost=new int[needed+1,2];
        for(int y=0;y<=needed;y++)for(int phase=0;phase<2;phase++)cost[y,phase]=impossible;
        cost[0,0]=0;
        for(int y=1;y<=needed;y++)
        {
            cancellation.ThrowIfCancellationRequested();
            foreach(var reach in reaches)if(reach.Y<=y)
                for(int phase=0;phase<2;phase++)
                    if((reach.Phases&(1<<(((start.Piece.End.BankX&1)^phase)|(((start.Piece.End.BankY+needed-y)&1)<<1))))!=0)
                        cost[y,phase]=Math.Min(cost[y,phase],cost[y-reach.Y,phase^(reach.X&1)]+reach.Bays);
        }
        if(cost[needed,0]>=impossible)return null;
        var links=catalogue.Links.ToDictionary(l=>(l.FromId,l.FromReverse,l.ToId,l.ToReverse));
        var random=new Random(seed^0x6A281D);
        int nodes=0;
        for(int attempt=0;attempt<10;attempt++)
        {
            cancellation.ThrowIfCancellationRequested();
            var placed=new List<RiverCoastPieces.Placement>{new(start.Piece,0,0,false)};
            int remaining=needed,phase=0,x=0,y=0,left=0,right=0;
            double amplitude=Math.Min(width*.34,height*.28);
            var guide=new double[random.Next(4,7)];
            for(int i=1;i<guide.Length;i++)guide[i]=(random.NextDouble()*2-1)*amplitude;
            int lastDirection=0,sameDirection=0,diagonalRun=0;
            bool valid=true;
            while(remaining>0&&valid)
            {
                cancellation.ThrowIfCancellationRequested();
                var choices=new List<(Reach Reach,double Score)>();
                foreach(var reach in reaches)
                {
                    if(reach.Y>remaining||cost[remaining-reach.Y,phase^(reach.X&1)]+reach.Bays!=cost[remaining,phase]||!FitsReach(reach))continue;
                    bool diagonal=reach.Steps.All(s=>s.Id=="west-diagonal");
                    if(diagonal&&diagonalRun+reach.Steps.Length>5)continue;
                    int nx=x+reach.X;
                    if(Math.Max(right,nx)-Math.Min(left,nx)>width-64)continue;
                    double t=(y+reach.Y)/(double)needed*(guide.Length-1);
                    int at=Math.Min(guide.Length-2,(int)t);double blend=t-at;
                    double target=guide[at]*(1-blend)+guide[at+1]*blend;
                    int direction=Math.Sign(reach.X);
                    double score=Math.Abs(nx-target)*.22+random.NextDouble()*7
                        +(direction==lastDirection?sameDirection*4:0)+5.0/reach.Steps.Length;
                    choices.Add((reach,score));
                }
                if(choices.Count==0){valid=false;break;}
                var chosen=choices.MinBy(c=>c.Score).Reach;
                foreach(var (id,reverse) in chosen.Steps)if(!Append(id,reverse)){valid=false;break;}
                diagonalRun=chosen.Steps.All(s=>s.Id=="west-diagonal")?diagonalRun+chosen.Steps.Length:0;
                x+=chosen.X;y+=chosen.Y;remaining-=chosen.Y;phase^=chosen.X&1;
                left=Math.Min(left,x);right=Math.Max(right,x);
                int nextDirection=Math.Sign(chosen.X);sameDirection=nextDirection==lastDirection?sameDirection+1:0;lastDirection=nextDirection;
            }
            if(!valid||!Append(finish.Piece.Id,false))continue;
            var chain=catalogue.ResearchAssemble(placed,out _);
            if(chain==null||chain.Closed||chain.Cells.Any(c=>c.Y<0||c.Y>=height))continue;
            int min=chain.Cells.Min(c=>c.X),max=chain.Cells.Max(c=>c.X);
            int span=max-min+28;
            if(span>width)continue;
            int centre=(int)(width*(.35+random.NextDouble()*.3));
            int shift=Math.Clamp(centre-(min+max)/2,12-min,width-14-max)&~1;
            var translated=new RiverCoastPieces.Chain(placed.Select(p=>p with{X=p.X+shift}).ToArray(),
                chain.Cells.Select(c=>new StampCell(c.X+shift,c.Y,c.Cell)).ToArray());
            var last=translated.Pieces[^1];
            (double X,double Y)[] boundary=[(last.X+finish.Piece.End.ShoreX,height),(width,height),(width,0),(shift+start.Piece.Start.ShoreX,0)];
            try
            {
                var ground=RiverPieceGroundAssembler.AssembleCoast(catalogue,translated,seed,new(0,0,width,height),boundary,cancellation,sealNativePockets:true);
                return new(translated,ground,nodes,attempt+1);
            }
            catch(System.IO.InvalidDataException){ }

            bool FitsReach(Reach reach)
            {
                var previous=placed[^1];
                foreach(var (id,reverse) in reach.Steps)
                {
                    if(!links.TryGetValue((previous.Piece.Id,previous.Reverse,id,reverse),out var link))return false;
                    var piece=pieces[id];int px=previous.X+link.Dx,py=previous.Y+link.Dy;
                    if(!piece.GroundAligned(px,py))return false;
                    // This original northwest cover at even X / odd Y traps
                    // small walkable pockets. Choose another native phase
                    // before rasterizing the full map instead of repairing it.
                    if(id==inward&&(px&1)==0&&(py&1)!=0)return false;
                    previous=new(piece,px,py,reverse);
                }
                return true;
            }
            bool Append(string id,bool reverse)
            {
                nodes++;var previous=placed[^1];
                if(!links.TryGetValue((previous.Piece.Id,previous.Reverse,id,reverse),out var link))return false;
                var piece=pieces[id];int px=previous.X+link.Dx,py=previous.Y+link.Dy;
                if(!piece.GroundAligned(px,py))return false;
                placed.Add(new(piece,px,py,reverse));return true;
            }
        }
        return null;
    }
}

