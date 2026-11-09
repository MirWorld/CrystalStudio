using System.IO;

namespace CrystalStudio;

internal sealed record ExtractionInput(string Source, MapDocument? Snapshot = null, Rect? Selection = null);
internal sealed record ExtractionProgress(string Phase,int Maps,int Candidates,int Occurrences,string? Source=null);
internal sealed record ExtractionSession(string Id,List<ExtractionCandidate> Candidates,List<string> Sources,List<string> Warnings);

internal sealed class ObjectExtractionService
{
    private readonly string indexPath;
    private readonly AssetSourceFormat format;
    private readonly Func<int,int,SpriteHeader?> header;
    private readonly Dictionary<(int,int),SpriteHeader?> headers=[];
    private readonly Dictionary<(int,int),HashSet<string>> associations=[];
    private readonly Dictionary<string,(string Path,CategorizedStamp Category)> exact=[];
    private readonly Dictionary<string,CategorizedStamp> byPath=new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> warnings=[];
    private StampCategoryIndex index=null!;
    private StampIdentifier.Catalogue templates=null!;
    public ObjectExtractionService(string indexPath,Func<int,int,SpriteHeader?> header){this.indexPath=indexPath;this.header=header;}
    public ObjectExtractionService(string indexPath,AssetStore assets):this(indexPath,(lib,image)=>
        assets.Libraries.TryGetValue(lib,out var library)?library.ReadHeader(image):null){format=assets.Format;}
    public Task<ExtractionSession> ScanAsync(IEnumerable<ExtractionInput> inputs,CancellationToken token,IProgress<ExtractionProgress>? progress=null)
        =>Task.Run(()=>Scan(inputs,token,progress),token);
    private void Prepare(CancellationToken token,IProgress<ExtractionProgress>? progress)
    {
        progress?.Report(new("Reading stamp catalogue",0,0,0));
        index=StampCategories.Read(indexPath);templates=StampIdentifier.LoadCatalogue(index.ObjectRoot,token) with{ForegroundOnly=true};
        foreach(var entry in index.Objects)
        {
            token.ThrowIfCancellationRequested();string path=StampCategories.Resolve(index.ObjectRoot,entry.File);byPath[path]=entry;
            try
            {
                var cells=CellCodec.LoadStamp(path,token);exact.TryAdd(ExtractionContent.Fingerprint(cells),(path,entry));
                if(string.IsNullOrWhiteSpace(entry.Type))continue;
                foreach(var front in cells.Select(c=>c.Cell.Foreground).OfType<Front>().Distinct())
                {
                    var key=((int)front.Library,front.Image&0x7FFF);
                    if(!associations.TryGetValue(key,out var types))associations[key]=types=new(StringComparer.OrdinalIgnoreCase);
                    types.Add(entry.Type);
                }
            }
            catch(Exception ex) when(ex is IOException or InvalidDataException or UnauthorizedAccessException)
            {warnings.Add(path+": "+ex.Message);}
        }
    }
    private SpriteHeader? Header(int lib,int image)
    {
        if(headers.TryGetValue((lib,image),out var value))return value;
        try{value=header(lib,image);}
        catch(Exception ex) when(ex is IOException or InvalidDataException or UnauthorizedAccessException){value=null;}
        headers[(lib,image)]=value;
        if(value==null&&warnings.Count<1000)warnings.Add($"Missing artwork: {lib}/{image+1}");
        return value;
    }
    private ExtractionSession Scan(IEnumerable<ExtractionInput> inputs,CancellationToken token,IProgress<ExtractionProgress>? progress)
    {
        Prepare(token,progress);var unique=new Dictionary<string,ExtractionCandidate>();var sources=new List<string>();int occurrences=0;
        foreach(var input in inputs)
        {
            token.ThrowIfCancellationRequested();progress?.Report(new("Reading map",sources.Count,unique.Count,occurrences,input.Source));
            MapDocument map;
            try{map=input.Snapshot??MapFormatPlugins.Default.Open(input.Source,format);token.ThrowIfCancellationRequested();}
            catch(Exception ex) when(ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or IndexOutOfRangeException)
            {warnings.Add(input.Source+": "+ex.Message);continue;}
            sources.Add(input.Source);
            var contextIndex=new ContextIndex(map,Header,token);
            var claimed=new HashSet<int>();var valid=new List<PlacedStamp>();
            // Metadata is evidence only while all of its foreground anchors still agree.
            foreach(var stamp in map.PlacedStamps)
            {
                token.ThrowIfCancellationRequested();
                var fronts=stamp.Cells.Where(c=>c.Cell.HasForeground).ToList();
                if(fronts.Count==0||fronts.Any(c=>map.Get(stamp.X+c.X,stamp.Y+c.Y).Foreground!=c.Cell.Foreground)||
                    fronts.Any(c=>claimed.Contains((stamp.Y+c.Y)*map.Width+stamp.X+c.X)))continue;
                var cells=Capture(map,stamp);if(cells.Count==0)continue;
                valid.Add(stamp);Add(cells,stamp.X,stamp.Y,ExtractionKind.Metadata,stamp.Name,null,"Confirmed placement metadata.");
            }
            var updates=new InlineProgress<StampScanProgress>(p=>progress?.Report(new(p.Phase,sources.Count,unique.Count,occurrences,input.Source)));
            var matches=StampIdentifier.Scan(map,index.ObjectRoot,token,updates,templates,valid);
            foreach(var stamp in matches.Matches)
            {
                token.ThrowIfCancellationRequested();var cells=stamp.Cells.Select(c=>new StampCell(c.X,c.Y,ForegroundCell(c.Cell,c.Cell.BackImage))).Where(c=>!c.Cell.Empty).ToList();
                if(!cells.Any(c=>c.Cell.HasForeground))continue;
                Add(cells,stamp.X,stamp.Y,ExtractionKind.Template,stamp.Name,matches.TemplatePaths.GetValueOrDefault(stamp.Id),"Matched an existing object template.");
            }
            progress?.Report(new("Discovering foreground fragments",sources.Count,unique.Count,occurrences,input.Source));
            var fragments=map.Cells.Where(p=>p.Value.HasForeground&&!claimed.Contains(p.Key)).OrderBy(p=>p.Key).Select(p=>
            {
                var f=p.Value.Foreground!;int x=p.Key%map.Width,y=p.Key/map.Width;var info=Header(f.Library,(f.Image&0x7FFF)-1);
                Rect? bounds=info is {} h?MapSpriteDrawing.Bounds(h,x,y,f.Library,(f.Image&0x7FFF)-1,true,true,MapSpriteDrawing.FrontBlend(f.Frames)):null;
                return new Fragment(x,y,f,bounds);
            }).ToList();
            foreach(var group in Group(fragments,token))
            {
                var cells=group.Points.Select(f=>new StampCell(f.X,f.Y,ForegroundCell(map.Get(f.X,f.Y),map.Get(f.X,f.Y).BackImage))).ToList();
                string reason=group.Limited?"Automatic size limit exceeded; individual fragment requires review.":
                    group.Points.Any(p=>p.Bounds==null)?"Artwork missing; grouping requires review.":"Grouped by sprite bounds, library and image sequence; review object boundaries.";
                Add(cells,0,0,ExtractionKind.Discovered,LocalText.Format("Object {0}",unique.Count+1),null,reason);
            }
            progress?.Report(new("Map complete",sources.Count,unique.Count,occurrences,input.Source));

            void Add(List<StampCell> original,int x,int y,ExtractionKind kind,string name,string? templatePath,string reason)
            {
                foreach(var c in original.Where(c=>c.Cell.HasForeground))claimed.Add((y+c.Y)*map.Width+x+c.X);
                var absolute=original.Select(c=>new StampCell(c.X+x,c.Y+y,c.Cell.Copy())).ToList();
                if(!absolute.Any(c=>c.Cell.HasForeground))return;
                bool crosses=false;
                if(input.Selection is {} selection)
                {
                    if(!absolute.Any(c=>selection.Contains(new Point(c.X+.5,c.Y+.5))))return;
                    crosses=absolute.Any(c=>!selection.Contains(new Point(c.X+.5,c.Y+.5)));
                }
                var cells=ExtractionContent.Normalize(absolute);int ox=absolute.Min(c=>c.X)-cells.Min(c=>c.X),oy=absolute.Min(c=>c.Y)-cells.Min(c=>c.Y);
                var key=ExtractionContent.Fingerprint(cells);var occurrence=new ExtractionOccurrence(input.Source,ox,oy,crosses);occurrences++;
                // Include all actual collision as toggleable evidence, including anchor flags.
                var suggestions=new List<StampCell>();int left=absolute.Min(c=>c.X)-2,top=absolute.Min(c=>c.Y)-2;
                int right=absolute.Max(c=>c.X)+2,bottom=absolute.Max(c=>c.Y)+2;
                if((long)(right-left+1)*(bottom-top+1)<=10000)
                    for(int sy=Math.Max(0,top);sy<=Math.Min(map.Height-1,bottom);sy++)for(int sx=Math.Max(0,left);sx<=Math.Min(map.Width-1,right);sx++)
                    {
                        int flags=map.Get(sx,sy).BackImage&ExtractionContent.CollisionMask;
                        if(flags!=0)suggestions.Add(new(sx-ox,sy-oy,new(){BackImage=flags}));
                    }
                if(unique.TryGetValue(key,out var prior))
                {
                    var evidence=suggestions.ToDictionary(c=>(c.X,c.Y),c=>c.Cell.BackImage);
                    prior.Occurrences.Add(occurrence);
                    unique[key]=prior with {
                        SuggestedCollisions=prior.SuggestedCollisions.Select(c=>new StampCell(c.X,c.Y,new(){BackImage=c.Cell.BackImage&evidence.GetValueOrDefault((c.X,c.Y))})).Where(c=>!c.Cell.Empty).ToList()};return;
                }
                string category="Unclassified";
                if(templatePath!=null&&byPath.TryGetValue(templatePath,out var entry)){category=entry.Type??category;if(kind==ExtractionKind.Template)name=entry.Name;}
                else if(exact.TryGetValue(key,out var known)){templatePath=known.Path;category=known.Category.Type??category;if(kind==ExtractionKind.Discovered)name=known.Category.Name;}
                else
                {
                    var fronts=cells.Select(c=>c.Cell.Foreground).OfType<Front>().ToList();
                    var votes=new Dictionary<string,int>(StringComparer.OrdinalIgnoreCase);
                    foreach(var f in fronts)if(associations.TryGetValue((f.Library,f.Image&0x7FFF),out var types))
                        foreach(var type in types)votes[type]=votes.GetValueOrDefault(type)+1;
                    var ordered=votes.OrderByDescending(p=>p.Value).ToList();
                    if(ordered.Count>0&&ordered[0].Value>=fronts.Count*.8&&(ordered.Count==1||ordered[0].Value>ordered[1].Value))
                    {category=ordered[0].Key;reason+="\nCategory supported by at least 80% of fragments.";}
                }
                // Keep one bounded context patch per unique payload, rather than entire maps.
                var patch=contextIndex.Capture(absolute,ox,oy,token);
                unique.Add(key,new(Guid.NewGuid(),name,category,reason,kind,cells,suggestions,[occurrence],patch.Cells,templatePath,ContextArea:patch.Area));
                if(unique.Count>100000)throw new InvalidDataException("More than 100,000 unique candidates; scan a smaller input.");
            }
        }
        token.ThrowIfCancellationRequested();return new(DateTime.UtcNow.ToString("yyyyMMddTHHmmssfff")+"-"+Guid.NewGuid().ToString("N")[..8],unique.Values.ToList(),sources,warnings.Distinct().ToList());
    }
    private static Cell ForegroundCell(Cell actual,int flags)=>new()
    {Foreground=actual.Foreground,BackImage=flags&ExtractionContent.CollisionMask,Door=actual.HasForeground?actual.Door:(byte)0,
        DoorOffset=actual.HasForeground?actual.DoorOffset:(byte)0,Light=actual.HasForeground?actual.Light:(byte)0};
    private static List<StampCell> Capture(MapDocument map,PlacedStamp stamp)
    {
        var cells=new List<StampCell>();
        foreach(var s in stamp.Cells)
        {
            var actual=map.Get(stamp.X+s.X,stamp.Y+s.Y);var cell=ForegroundCell(actual,actual.BackImage&s.Cell.BackImage);
            if(!s.Cell.HasForeground)cell.Foreground=null;
            // Foreground-only extraction does not inherit lighting or doors from unrelated cells.
            if(s.Cell.Door==0){cell.Door=0;cell.DoorOffset=0;}if(s.Cell.Light==0)cell.Light=0;
            if(!cell.Empty)cells.Add(new(s.X,s.Y,cell));
        }
        return cells;
    }
    private sealed record Fragment(int X,int Y,Front Front,Rect? Bounds);
    private sealed class ContextIndex
    {
        private readonly MapDocument map;
        private readonly Dictionary<(int,int),List<(StampCell Cell,Rect Bounds)>> buckets=[];
        private readonly Func<int,int,SpriteHeader?> header;
        public ContextIndex(MapDocument map,Func<int,int,SpriteHeader?> header,CancellationToken token)
        {
            this.map=map;this.header=header;
            foreach(var pair in map.Cells)
            {
                token.ThrowIfCancellationRequested();if(!pair.Value.HasForeground)continue;
                var cell=new StampCell(pair.Key%map.Width,pair.Key/map.Width,pair.Value);var bounds=Bounds(cell);
                foreach(var key in Keys(bounds))
                {if(!buckets.TryGetValue(key,out var list))buckets[key]=list=[];list.Add((cell,bounds));}
            }
        }
        private Rect Bounds(StampCell c)
        {
            if(c.Cell.Foreground is {} f&&header(f.Library,(f.Image&0x7FFF)-1) is {} h)
                return MapSpriteDrawing.Bounds(h,c.X,c.Y,f.Library,(f.Image&0x7FFF)-1,true,true,MapSpriteDrawing.FrontBlend(f.Frames));
            return new(c.X*48,c.Y*32,48,32);
        }
        private static IEnumerable<(int,int)> Keys(Rect b)
        {
            for(int y=(int)Math.Floor(b.Top/384);y<=(int)Math.Floor(b.Bottom/384);y++)
                for(int x=(int)Math.Floor(b.Left/384);x<=(int)Math.Floor(b.Right/384);x++)yield return(x,y);
        }
        public (List<StampCell> Cells,Rect Area) Capture(List<StampCell> source,int ox,int oy,CancellationToken token)
        {
            Rect area=default;foreach(var c in source)area=EditorGeometry.Union(area,Bounds(c));
            area=area.Inflate(new Thickness(96,64));
            // Bound context memory independently of the full source map dimensions.
            if(area.Width>48*64||area.Height>32*64)area=new(area.Center.X-Math.Min(area.Width,48*64)/2,
                area.Center.Y-Math.Min(area.Height,32*64)/2,Math.Min(area.Width,48*64),Math.Min(area.Height,32*64));
            var cells=new Dictionary<(int,int),Cell>();
            int left=Math.Max(0,(int)Math.Floor(area.Left/48)),top=Math.Max(0,(int)Math.Floor(area.Top/32));
            int right=Math.Min(map.Width-1,(int)Math.Ceiling(area.Right/48)),bottom=Math.Min(map.Height-1,(int)Math.Ceiling(area.Bottom/32));
            for(int y=top;y<=bottom;y++)for(int x=left;x<=right;x++)
            {
                token.ThrowIfCancellationRequested();var c=map.Get(x,y).Copy();c.Foreground=null;
                if(!c.Empty)cells[(x,y)]=c;
            }
            foreach(var key in Keys(area))if(buckets.TryGetValue(key,out var list))foreach(var item in list)
            {
                token.ThrowIfCancellationRequested();if(!item.Bounds.Intersects(area))continue;
                var point=(item.Cell.X,item.Cell.Y);
                if(!cells.TryGetValue(point,out var cell))cells[point]=cell=new();
                cell.Foreground=item.Cell.Cell.Foreground;
            }
            return(cells.Select(p=>new StampCell(p.Key.Item1-ox,p.Key.Item2-oy,p.Value)).ToList(),area.Translate(new Vector(-ox*48,-oy*32)));
        }
    }
    private sealed record FragmentGroup(List<Fragment> Points,bool Limited=false);
    private static List<FragmentGroup> Group(List<Fragment> items,CancellationToken token)
    {
        int n=items.Count;var parent=Enumerable.Range(0,n).ToArray();var groups=items.Select((f,i)=>new HashSet<int>{i}).ToArray();
        var images=items.Select(f=>new HashSet<int>{f.Front.Image&0x7FFF}).ToArray();var limited=new bool[n];
        int Root(int i){while(parent[i]!=i){parent[i]=parent[parent[i]];i=parent[i];}return i;}
        var spatial=new Dictionary<(int,int,int),List<int>>();
        for(int i=0;i<n;i++)
        {
            token.ThrowIfCancellationRequested();var f=items[i];if(f.Bounds is not {} b)continue;
            var neighbours=new HashSet<int>();
            for(int by=(int)Math.Floor((b.Top-1)/192);by<=(int)Math.Floor((b.Bottom+1)/192);by++)
                for(int bx=(int)Math.Floor((b.Left-1)/192);bx<=(int)Math.Floor((b.Right+1)/192);bx++)
                {
                    var key=(bx,by,(int)f.Front.Library);if(!spatial.TryGetValue(key,out var list))spatial[key]=list=[];
                    foreach(int j in list)if(Math.Abs((f.Front.Image&0x7FFF)-(items[j].Front.Image&0x7FFF))<=32)neighbours.Add(j);
                    list.Add(i);
                }
            foreach(int j in neighbours.Order())
            {
                var a=f.Bounds.Value;var other=items[j].Bounds!.Value;
                if(a.Left>other.Right+1||other.Left>a.Right+1||a.Top>other.Bottom+1||other.Top>a.Bottom+1)continue;
                int ra=Root(i),rb=Root(j);if(ra==rb||images[ra].Overlaps(images[rb]))continue;
                // A single sequence is deliberately conservative around repeated walls and trees.
                if(Math.Max(images[ra].Max(),images[rb].Max())-Math.Min(images[ra].Min(),images[rb].Min())>32)continue;
                if(groups[ra].Count<groups[rb].Count)(ra,rb)=(rb,ra);
                parent[rb]=ra;groups[ra].UnionWith(groups[rb]);images[ra].UnionWith(images[rb]);limited[ra]|=limited[rb];groups[rb].Clear();images[rb].Clear();
                var members=groups[ra].Select(k=>items[k]);
                limited[ra]|=groups[ra].Count>512||members.Max(v=>v.X)-members.Min(v=>v.X)>=64||members.Max(v=>v.Y)-members.Min(v=>v.Y)>=64;
            }
        }
        var result=new List<FragmentGroup>();
        for(int i=0;i<n;i++)if(Root(i)==i)
        {
            if(limited[i])foreach(int j in groups[i])result.Add(new([items[j]],true));
            else result.Add(new(groups[i].Order().Select(j=>items[j]).ToList()));
        }
        return result;
    }
    private sealed class InlineProgress<T>(Action<T> action):IProgress<T>{public void Report(T value)=>action(value);}
}
