using System.IO;

namespace CrystalStudio;

/// <summary>Research open shoreline assembled from saved individual bank pieces.</summary>
public static class RiverPieceCoastBuilder
{
    public sealed record Result(RiverCoastPieces.Chain Chain,RiverPieceGroundAssembler.Result Ground,int Nodes,int GroundTrials);

    public static Result? ResearchCurved(RiverCoastPieces catalogue,RiverBoundaryComponent edge,int seed,
        int maximumWidth,int maximumHeight,out string failure,CancellationToken cancellation=default,bool exactSize=false,RiverBoundaryComponent? endEdge=null)
    {
        cancellation.ThrowIfCancellationRequested();
        var finish=endEdge??edge;
        if(exactSize&&(maximumHeight&1)!=(finish.Height&1))throw new ArgumentException("The native ending piece must match the requested height phase.");
        if(exactSize&&RiverVerticalCoastBuilder.Build(catalogue,edge,finish,seed,maximumWidth,maximumHeight,cancellation) is {} varied)
        {failure="";return varied;}
        var byId=catalogue.Pieces.ToDictionary(p=>p.Id);
        if(!byId.ContainsKey(edge.Piece.Id)||!byId.ContainsKey(finish.Piece.Id))throw new ArgumentException("Missing verified shoreline edge piece.");
        var random=new Random(seed);int nodes=0,trials=0,passNodes=0,passTrials=0;string rejected="No fitting native curve.";
        var placed=new List<RiverCoastPieces.Placement>{new(edge.Piece,0,0,false)};
        var outgoing=catalogue.Links.GroupBy(l=>(l.FromId,l.FromReverse)).ToDictionary(g=>g.Key,g=>g.ToArray());
        // Native endpoint distance guides the last part of a path toward an edge
        // connector rather than exhausting deep random loops.
        var distance=new Dictionary<(string,bool),int>{[(finish.Piece.Id,false)]=0};
        var queue=new Queue<(string,bool)>();queue.Enqueue((finish.Piece.Id,false));
        var incoming=catalogue.Links.GroupBy(l=>(l.ToId,l.ToReverse)).ToDictionary(g=>g.Key,g=>g.ToArray());
        while(queue.TryDequeue(out var view))
        {
            if(!incoming.TryGetValue(view,out var links))continue;
            foreach(var link in links)
            {
                var previous=(link.FromId,link.FromReverse);
                if(distance.TryAdd(previous,distance[view]+1))queue.Enqueue(previous);
            }
        }
        var interference=new Dictionary<(int,int,int,int,int,int),bool>();
        int requested=7+((seed%4)+4)%4;Result? result=null;
        // Seeded reach lengths and successive native bays keep broad shore shapes
        // variable while avoiding arbitrary tight loops and expensive retries.
        for(int attempt=0;attempt<(exactSize?256:32)&&result==null;attempt++)
        {
            cancellation.ThrowIfCancellationRequested();placed.Clear();placed.Add(new(edge.Piece,0,0,false));
            bool Append(string id,bool reverse=false)
            {
                var previous=placed[^1];var links=outgoing[(previous.Piece.Id,previous.Reverse)]
                    .Where(l=>l.ToId==id&&l.ToReverse==reverse).ToArray();
                if(links.Length!=1)return false;var link=links[0];
                placed.Add(new(byId[id],previous.X+link.Dx,previous.Y+link.Dy,reverse));nodes++;return true;
            }
            bool Repeat(string id,int count,bool reverse=false)
            {for(int i=0;i<count;i++)if(!Append(id,reverse))return false;return true;}
            int bays=exactSize?1+(attempt+((seed%3)+3)%3)%3:0;
            int pattern=attempt/3;
            int Diagonals(int bay)=>exactSize?pattern/(bay==0?1:bay==1?3:9)%3:random.Next(3);
            bool ok=Repeat("west-diagonal",exactSize&&Diagonals(0)==2?2:random.Next(3));
            if(ok&&!exactSize&&random.Next(2)==0)ok=Append("native-1391-1392-4-9")&&Repeat("west-bank",random.Next(3))&&Append("west-diagonal-exit",true);
            if(!exactSize)bays=random.Next(1,4);
            for(int bay=0;bay<bays&&ok;bay++)
            {
                ok=Append("west-diagonal-entry",true)&&Append("native-1387-1390-m4-4",true)
                    &&Repeat("south-diagonal",Diagonals(bay))&&Append("native-1387-1388-7-m7")&&Append("native-1388-1389-7-m3")
                    &&Repeat("south-straight",random.Next(exactSize?3:4))&&Append("west-south-offset-join",true)&&Append("west-concave-entry",true)
                    &&Repeat("west-bank",exactSize?0:random.Next(3))&&Append("west-diagonal-exit",true);
                if(ok&&bay+1<bays)ok=Repeat("west-diagonal",exactSize?2:2+random.Next(2));
            }
            if(ok)ok=Repeat("west-diagonal",exactSize?0:random.Next(3));
            if(ok)
            {
                var previous=placed[^1];var link=outgoing[(previous.Piece.Id,previous.Reverse)].Single(l=>l.ToId==finish.Piece.Id&&!l.ToReverse);
                if(!finish.Piece.GroundAligned(previous.X+link.Dx,previous.Y+link.Dy))ok=Append("west-diagonal");
            }
            if(ok&&Append(finish.Piece.Id))
            {
                var chain=catalogue.ResearchAssemble(placed,out _);
                if(chain!=null&&!chain.Closed&&(exactSize||CoversVisible(chain)))result=Compile(chain);
            }
        }
        placed.Clear();placed.Add(new(edge.Piece,0,0,false));
        for(int attempt=0;attempt<8&&result==null;attempt++)
        {
            random=new Random(unchecked(seed+attempt*7919));passNodes=0;passTrials=0;
            result=Search();
        }
        failure=result==null?rejected:"";return result;

        Result? Compile(RiverCoastPieces.Chain chain)
        {
            var compiled=placed.ToList();var next=compiled[^1];int height=next.Y+finish.Height;
            if(exactSize)
            {
                int extra=maximumHeight-height;
                if(extra<0||extra%18!=0)return null;
                if(extra>0)
                {
                    var insertions=compiled.Select((p,i)=>(p,i)).Where(v=>v.p.Piece.Id=="west-concave-entry"
                        ||v.p.Piece.Id=="west-diagonal"&&(v.i+1==compiled.Count||compiled[v.i+1].Piece.Id!="west-diagonal"))
                        .Select(v=>(Index:v.i+1,Id:v.p.Piece.Id=="west-diagonal"?"west-diagonal":"west-bank")).ToArray();
                    if(insertions.Length==0)return null;
                    var extensions=new int[insertions.Length];
                    int nativeWidth=(chain.Cells.Max(c=>c.X)-(chain.Cells.Min(c=>c.X)-12&~1)+14)&~1;
                    int diagonalBudget=Math.Max(0,(maximumWidth-nativeWidth)/12);
                    var vertical=Enumerable.Range(0,insertions.Length).Where(i=>insertions[i].Id=="west-bank").ToArray();
                    var diagonal=Enumerable.Range(0,insertions.Length).Where(i=>insertions[i].Id=="west-diagonal").ToArray();
                    if(vertical.Length==0)return null;
                    // Mix diagonal reaches with separate bay banks. The map's available
                    // width limits diagonal growth; every added pair preserves ground phase.
                    for(int unit=0;unit<extra/18;unit++)
                    {
                        bool slanted=diagonalBudget>0&&diagonal.Length>0&&random.Next(3)!=0;
                        var choices=slanted?diagonal:vertical;
                        extensions[choices[random.Next(choices.Length)]]+=18;
                        if(slanted)diagonalBudget--;
                    }
                    for(int bay=insertions.Length-1;bay>=0;bay--)
                    {
                        int extension=extensions[bay];if(extension==0)continue;
                        var slot=insertions[bay];int insertion=slot.Index;
                        var reach=new List<RiverCoastPieces.Placement>();var previous=compiled[insertion-1];int moveX=0;
                        for(int i=0;i<extension/9;i++)
                        {
                            var links=outgoing[(previous.Piece.Id,previous.Reverse)].Where(l=>l.ToId==slot.Id&&!l.ToReverse).ToArray();
                            if(links.Length!=1)return null;var link=links[0];
                            previous=new(byId[slot.Id],previous.X+link.Dx,previous.Y+link.Dy,false);reach.Add(previous);moveX+=link.Dx;
                        }
                        // Translate subsequent whole pieces; authored fragments and saved
                        // seam covers keep their original relative positions at each join.
                        for(int i=insertion;i<compiled.Count;i++)compiled[i]=compiled[i] with{X=compiled[i].X+moveX,Y=compiled[i].Y+extension};
                        compiled.InsertRange(insertion,reach);
                    }
                    var fitted=catalogue.ResearchAssemble(compiled,out _);if(fitted==null||!exactSize&&!CoversVisible(fitted))return null;
                    chain=fitted;next=compiled[^1];height=next.Y+finish.Height;
                }
            }
            bool turns=compiled.Any(p=>
            {
                var a=p.Reverse?p.Piece.End:p.Piece.Start;var b=p.Reverse?p.Piece.Start:p.Piece.End;
                double dx=(b.BankX-a.BankX)*1.5,dy=b.BankY-a.BankY,length=Math.Sqrt(dx*dx+dy*dy);
                return length>1&&(dx+dy)/(Math.Sqrt(2)*length)<.75;
            });
            if(turns&&height>=36&&chain.Cells.All(c=>c.Y>=0&&c.Y<height))
            {
                int left=(chain.Cells.Min(c=>c.X)-12)&~1,shift=-left;
                int width=(chain.Cells.Max(c=>c.X)+shift+14)&~1;
                if(width<=maximumWidth&&height<=maximumHeight)
                {
                    if(exactSize)
                    {
                        int positions=(maximumWidth-width)/2+1;
                        shift+=random.Next(positions)*2;width=maximumWidth;
                    }
                    var translated=new RiverCoastPieces.Chain(compiled.Select(p=>p with{X=p.X+shift}).ToArray(),
                        chain.Cells.Select(c=>new StampCell(c.X+shift,c.Y,c.Cell)).ToArray());
                    double start=shift+edge.Piece.Start.ShoreX,end=next.X+shift+finish.Piece.End.ShoreX;
                    (double X,double Y)[] boundary=[(end,height),(width,height),(width,0),(start,0)];
                    trials++;passTrials++;
                    try
                    {
                        var ground=RiverPieceGroundAssembler.AssembleCoast(catalogue,translated,seed,new(0,0,width,height),boundary,cancellation);
                        return new(translated,ground,nodes,trials);
                    }
                    catch(InvalidDataException error){rejected=error.Message;}
                }
            }
            return null;
        }

        bool CoversVisible(RiverCoastPieces.Chain chain)
        {
            var banks=chain.Cells.Where(c=>c.Cell.MiddleImage!=0).ToArray();
            foreach(var cover in chain.Cells.Where(c=>c.Cell.ForegroundImages.Count!=0))foreach(var front in cover.Cell.ForegroundImages)
            {
                var sprite=catalogue.NativeSprites[(front.Library,front.Image&0x7fff)];
                int phase=RiverArtwork.IsFloor(sprite)?1:2;
                foreach(var bank in banks)
                {
                    var art=catalogue.NativeSprites[(bank.Cell.MiddleLibrary,bank.Cell.MiddleImage)];
                    int bankPhase=RiverArtwork.IsFloor(art)?0:2;
                    if(bankPhase<phase||bankPhase==phase&&(bank.Y<cover.Y||bank.Y==cover.Y&&bank.X<=cover.X))continue;
                    var key=((int)bank.Cell.MiddleLibrary,(int)bank.Cell.MiddleImage,(int)front.Library,front.Image&0x7fff,bank.X-cover.X,bank.Y-cover.Y);
                    if(!interference.TryGetValue(key,out bool conflicts))
                    {
                        int dx=(bank.X-cover.X)*48,dy=(bank.Y-cover.Y)*32+(bankPhase<2?0:32-art.Height)-(phase<2?0:32-sprite.Height);
                        int l=Math.Max(0,dx),t=Math.Max(0,dy),r=Math.Min(sprite.Width,dx+art.Width),b=Math.Min(sprite.Height,dy+art.Height);
                        for(int y=t;y<b&&!conflicts;y++)for(int x=l;x<r;x++)
                            if(sprite.Bgra[(y*sprite.Width+x)*4+3]!=0&&art.Bgra[((y-dy)*art.Width+x-dx)*4+3]!=0)
                            {conflicts=true;break;}
                        interference[key]=conflicts;
                    }
                    if(conflicts)return false;
                }
            }
            return true;
        }

        Result? Search()
        {
            cancellation.ThrowIfCancellationRequested();nodes++;if(++passNodes>450||passTrials>=8||placed.Count>24)return null;
            var previous=placed[^1];if(!outgoing.TryGetValue((previous.Piece.Id,previous.Reverse),out var links))return null;
            var choices=links.Where(l=>(l.ToId!=edge.Piece.Id||edge.Piece==finish.Piece)
                    &&(l.ToId!=finish.Piece.Id||!l.ToReverse&&placed.Count>=requested))
                .OrderBy(l=>l.ToId==finish.Piece.Id?-10:random.NextDouble()*8+(l.ToId==previous.Piece.Id?3:0)
                    +(placed.Count>=requested?distance.GetValueOrDefault((l.ToId,l.ToReverse),100)*6:0)).ToArray();
            foreach(var link in choices)
            {
                if(passNodes>450||passTrials>=8)return null;
                var next=new RiverCoastPieces.Placement(byId[link.ToId],previous.X+link.Dx,previous.Y+link.Dy,link.ToReverse);
                var nextEnd=next.Reverse?next.Piece.Start:next.Piece.End;
                var endBank=(nextEnd.BankId,next.X+nextEnd.BankX,next.Y+nextEnd.BankY);
                bool revisits=placed.Any(p=>
                {
                    var port=p.Reverse?p.Piece.Start:p.Piece.End;
                    return (port.BankId,p.X+port.BankX,p.Y+port.BankY)==endBank;
                });
                if(!next.Piece.GroundAligned(next.X,next.Y)||next.Y<-32||revisits
                    ||placed.Any(p=>p.Piece.Id==next.Piece.Id&&p.X==next.X&&p.Y==next.Y))continue;
                placed.Add(next);var chain=catalogue.ResearchAssemble(placed,out _);
                if(chain==null||chain.Closed||!exactSize&&!CoversVisible(chain)||chain.Cells.Any(c=>c.Y<0)
                    ||chain.Cells.Max(c=>c.Y)+1>maximumHeight||chain.Cells.Max(c=>c.X)-chain.Cells.Min(c=>c.X)+18>maximumWidth)
                {placed.RemoveAt(placed.Count-1);continue;}
                if(next.Piece==finish.Piece)
                {
                    var answer=Compile(chain);if(answer!=null)return answer;
                }
                else
                {
                    var answer=Search();if(answer!=null)return answer;
                }
                placed.RemoveAt(placed.Count-1);
            }
            return null;
        }
    }
}
