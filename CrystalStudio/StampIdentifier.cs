using System.Diagnostics;
using System.IO;
namespace CrystalStudio;

public sealed record StampScanProgress(string Phase,int Completed,int Total,int Matches);
public sealed record StampScanResult(List<PlacedStamp> Matches,int Files,int SkippedFiles,int TemplatesWithoutImages,
    int ForegroundImages=0,int MatchedForegroundImages=0,int TemplatesMissingImages=0)
{
    internal Dictionary<Guid,string> TemplatePaths { get; init; } = [];
}

public static class StampIdentifier
{
    private readonly record struct ImageKey(byte Layer,short Library,int Image);
    private readonly record struct Location(int X,int Y);
    private readonly record struct Claim(int Cell,ImageKey Image,Front? Foreground=null);

    public static StampScanResult Scan(MapDocument map,string folder,CancellationToken cancellation=default,IProgress<StampScanProgress>? progress=null)
        => Scan(map, folder, cancellation, progress, null, null);

    internal sealed record Catalogue(List<(StampEntry Entry,int Size,byte[]? Data)> Entries,int Skipped,bool ForegroundOnly=false);
    internal static Catalogue LoadCatalogue(string folder,CancellationToken cancellation)
    {
        var ordered = new List<(StampEntry Entry,int Size,byte[]? Data)>(); int budget=32*1024*1024,skipped=0;
        foreach(var entry in StampCatalog.Read(folder))
        {
            cancellation.ThrowIfCancellationRequested();
            try { var header=ReadSize(entry.FilePath,ref budget); ordered.Add((entry,header.Size,header.Data)); }
            catch(Exception ex) when(ex is IOException or InvalidDataException or UnauthorizedAccessException) { skipped++; }
        }
        return new(ordered.OrderByDescending(e=>e.Size).ThenBy(e=>e.Entry.RelativePath,StringComparer.OrdinalIgnoreCase).ToList(),skipped);
    }

    internal static StampScanResult Scan(MapDocument map,string folder,CancellationToken cancellation,IProgress<StampScanProgress>? progress,
        Catalogue? catalogue,IReadOnlyList<PlacedStamp>? reservations)
    {
        if(!Directory.Exists(folder))throw new DirectoryNotFoundException("The stamp folder does not exist.");
        var timer=Stopwatch.StartNew();long lastReport=-200;var found=new List<PlacedStamp>();int skipped=0,noImages=0,missing=0,foreground=0,matchedForeground=0;
        void Report(string phase,int completed,int total,bool force=false){if(force||timer.ElapsedMilliseconds-lastReport>=100){lastReport=timer.ElapsedMilliseconds;progress?.Report(new(phase,completed,total,found.Count));}}
        Report("Indexing map images",0,1,true);
        var imageIndex=new Dictionary<ImageKey,List<Location>>();var claims=new Dictionary<Claim,int>();
        void Index(ImageKey key,Location point)
        {
            if(!imageIndex.TryGetValue(key,out var points))imageIndex[key]=points=[];
            // Keep indexed positions unique across the image layers of a cell.
            if(points.Count==0||points[^1]!=point)points.Add(point);
        }
        void Reserve(Claim claim){claims[claim]=claims.GetValueOrDefault(claim)+1;}
        foreach(var stamp in reservations ?? map.PlacedStamps)foreach(var delta in stamp.Changes)
        {
            int k=(stamp.Y+delta.Y)*map.Width+stamp.X+delta.X;
            if(delta.OwnedFronts is { } owned)
            { foreach(var f in owned)Reserve(new(k,FrontKey(f),f)); }
            else if(delta.After.Foreground is { } front && front!=delta.Before.Foreground)
                Reserve(new(k,FrontKey(front),front));
            if((delta.After.BackImage&0x1FFFFFFF)>0&&((delta.Before.BackImage&0x1FFFFFFF)!=(delta.After.BackImage&0x1FFFFFFF)||delta.Before.BackLibrary!=delta.After.BackLibrary))Reserve(new(k,new(0,delta.After.BackLibrary,delta.After.BackImage&0x1FFFFFFF)));
            if(delta.After.MiddleImage>0&&(delta.Before.MiddleImage!=delta.After.MiddleImage||delta.Before.MiddleLibrary!=delta.After.MiddleLibrary))Reserve(new(k,new(1,delta.After.MiddleLibrary,delta.After.MiddleImage)));
        }
        foreach(var pair in map.Cells)
        {
            cancellation.ThrowIfCancellationRequested();var point=new Location(pair.Key%map.Width,pair.Key/map.Width);foreground+=pair.Value.ForegroundImages.Count;
            var cell=pair.Value;
            if((cell.BackImage&0x1FFFFFFF)>0)Index(new(0,cell.BackLibrary,cell.BackImage&0x1FFFFFFF),point);
            if(cell.MiddleImage>0)Index(new(1,cell.MiddleLibrary,cell.MiddleImage),point);
            foreach(var f in cell.ForegroundImages)if((f.Image&0x7FFF)>0)Index(FrontKey(f),point);
        }
        var paths=new Dictionary<Guid,string>();
        catalogue ??= LoadCatalogue(folder,cancellation);
        var ordered=catalogue.Entries;skipped+=catalogue.Skipped;int files=ordered.Count+catalogue.Skipped;
        // Sorting by footprint still happens before matching. Retain small files
        // from the header pass to avoid opening the catalogue twice, with a fixed
        // memory budget even for unusually large user catalogues.
        for(int i=0;i<ordered.Count;i++)
        {
            Report("Matching stamps",i,ordered.Count);cancellation.ThrowIfCancellationRequested();var entry=ordered[i].Entry;List<StampCell> cells;
            try
            {
                if(ordered[i].Data is {} data)
                {
                    using var stream=new MemoryStream(data,writable:false);
                    cells=CellCodec.LoadStamp(stream,cancellation);
                }
                else cells=CellCodec.LoadStamp(entry.FilePath,cancellation);
                foreach(var cell in cells){if (cell.Cell.Foreground is { } f && (f.Image&0x7FFF)==0) cell.Cell.Foreground = null;if(cell.Cell.ForegroundImages.Any(f=>(f.Image&0x8000)!=0))cell.Cell.BackImage|=0x40000000;}
                cells.RemoveAll(c=>c.Cell.Empty);
                var coordinates=new HashSet<(int,int)>();
                if(cells.Count==0||cells.Any(c=>!coordinates.Add((c.X,c.Y))))throw new InvalidDataException("Empty stamp or repeated cell coordinates.");
            }
            catch(Exception ex) when(ex is IOException or InvalidDataException or UnauthorizedAccessException){skipped++;continue;}
            bool foregroundOnly=cells.Any(c=>c.Cell.ForegroundImages.Count>0);
            if(catalogue.ForegroundOnly&&!foregroundOnly){noImages++;continue;}
            StampCell? anchor=null;List<Location>? candidates=null;bool missingImages=false;
            var checks=new List<(StampCell Cell,int Frequency)>();
            foreach(var cell in cells)
            {
                int frequency=int.MaxValue;
                void Feature(ImageKey key)
                {
                    if(!imageIndex.TryGetValue(key,out var points)){missingImages=true;return;}
                    frequency=Math.Min(frequency,points.Count);
                    if(candidates==null||points.Count<candidates.Count){anchor=cell;candidates=points;}
                }
                if(!foregroundOnly)
                {
                    if((cell.Cell.BackImage&0x1FFFFFFF)>0)Feature(new(0,cell.Cell.BackLibrary,cell.Cell.BackImage&0x1FFFFFFF));
                    if(cell.Cell.MiddleImage>0)Feature(new(1,cell.Cell.MiddleLibrary,cell.Cell.MiddleImage));
                }
                foreach(var f in cell.Cell.ForegroundImages)Feature(FrontKey(f));
                if(missingImages)break;
                if(frequency!=int.MaxValue)checks.Add((cell,frequency));
            }
            if(missingImages){missing++;continue;}
            if(anchor==null){noImages++;continue;}
            // Only visual cells constrain a match. Check the rarest ones first;
            // retain original cell order when capturing the resulting ownership.
            var matchCells=checks.OrderBy(c=>c.Frequency).Select(c=>c.Cell).ToList();
            foreach(var point in candidates!)
            {
                cancellation.ThrowIfCancellationRequested();long x=(long)point.X-anchor.X,y=(long)point.Y-anchor.Y;
                // Indexed positions are unique, so translating by one anchor
                // cannot produce duplicate candidate origins.
                if(x<int.MinValue||x>int.MaxValue||y<int.MinValue||y>int.MaxValue)continue;
                if(!Matches(map,matchCells,(int)x,(int)y,claims,foregroundOnly,cancellation))continue;
                var deltas=new List<StampDelta>();var source=new List<StampCell>();
                foreach(var cell in cells)
                {
                    cancellation.ThrowIfCancellationRequested();long sx=x+cell.X,sy=y+cell.Y;if(sx<0||sy<0||sx>=map.Width||sy>=map.Height)continue;
                    int k=(int)sy*map.Width+(int)sx;var actual=map.Get((int)sx,(int)sy).Copy();
                    var matched=SelectFronts(actual,cell.Cell,k,claims)!;
                    var owned=matched.Select(m=>m.Value).ToList();var indices=matched.Select(m=>m.Index).ToList();
                    var stampCell=CaptureSource(actual,cell.Cell,owned,foregroundOnly);if(stampCell.Empty)continue;
                    var before=BeforeIdentification(actual,stampCell,indices);
                    source.Add(new(cell.X,cell.Y,stampCell));deltas.Add(new(cell.X,cell.Y,before,actual,owned,indices));
                    foreach(var f in owned)Reserve(new(k,FrontKey(f),f));matchedForeground+=owned.Count;
                    if((stampCell.BackImage&0x1FFFFFFF)>0)Reserve(new(k,new(0,stampCell.BackLibrary,stampCell.BackImage&0x1FFFFFFF)));
                    if(stampCell.MiddleImage>0)Reserve(new(k,new(1,stampCell.MiddleLibrary,stampCell.MiddleImage)));
                }
                var id=Guid.NewGuid();found.Add(new(id,entry.Name,(int)x,(int)y,source,deltas));paths[id]=entry.FilePath;
                if(found.Count+(reservations??map.PlacedStamps).Count>100000)throw new InvalidDataException("More than 100,000 stamp instances found; scan a smaller map.");
                Report("Matching stamps",i+1,ordered.Count);
            }
        }
        cancellation.ThrowIfCancellationRequested();Report("Complete",files,files,true);
        return new(found,files,skipped,noImages,foreground,matchedForeground,missing) { TemplatePaths=paths };
    }
    private static (int Size,byte[]? Data) ReadSize(string path,ref int bufferBudget)
    {
        using var r=new BinaryReader(File.OpenRead(path));
        if(r.BaseStream.Length>=3&&r.ReadByte()=='C'&&r.ReadByte()=='#'){if(r.ReadByte()>2)throw new InvalidDataException("Unsupported object version.");}else r.BaseStream.Position=0;
        int count=r.ReadInt32();if(count<1||count>1000000)throw new InvalidDataException("Invalid cell count.");
        byte[]? data=null;
        if(r.BaseStream.Length<=Math.Min(bufferBudget,1024*1024))
        {
            data=new byte[(int)r.BaseStream.Length];r.BaseStream.Position=0;r.BaseStream.ReadExactly(data);bufferBudget-=data.Length;
        }
        return(count,data);
    }
    private static ImageKey FrontKey(Front f)=>new(2,f.Library,f.Image&0x7FFF);
    private static bool HasFronts(Cell actual,Cell expected,int k,Dictionary<Claim,int> claims)
    {
        if (expected.Foreground is not { } image) return true;
        return actual.Foreground is { } front && FrontKey(front) == FrontKey(image) && claims.GetValueOrDefault(new(k,FrontKey(front),front)) == 0;
    }
    private static List<(int Index,Front Value)>? SelectFronts(Cell actual,Cell expected,int k,Dictionary<Claim,int> claims)
    {
        if (expected.Foreground is null) return [];
        return HasFronts(actual,expected,k,claims) ? [(0,actual.Foreground!)] : null;
    }
    private static bool Matches(MapDocument map,List<StampCell> cells,int x,int y,Dictionary<Claim,int> claims,bool foregroundOnly,CancellationToken cancellation)
    {
        foreach(var cell in cells)
        {
            cancellation.ThrowIfCancellationRequested();long sx=(long)x+cell.X,sy=(long)y+cell.Y;
            bool visual=cell.Cell.ForegroundImages.Count>0||(!foregroundOnly&&((cell.Cell.BackImage&0x1FFFFFFF)>0||cell.Cell.MiddleImage>0));
            if(sx<0||sy<0||sx>=map.Width||sy>=map.Height){if(visual)return false;continue;}
            int k=(int)sy*map.Width+(int)sx;
            var actual=map.Get((int)sx,(int)sy);var expected=cell.Cell;
            if(!foregroundOnly)
            {
                int back=expected.BackImage&0x1FFFFFFF;
                if(back>0&&(actual.BackLibrary!=expected.BackLibrary||(actual.BackImage&0x1FFFFFFF)!=back||claims.ContainsKey(new(k,new(0,expected.BackLibrary,back)))))return false;
                if(expected.MiddleImage>0&&(actual.MiddleLibrary!=expected.MiddleLibrary||actual.MiddleImage!=expected.MiddleImage||claims.ContainsKey(new(k,new(1,expected.MiddleLibrary,expected.MiddleImage)))))return false;
            }
            if(!HasFronts(actual,expected,k,claims))return false;
        }
        return true;
    }
    private static Cell CaptureSource(Cell actual,Cell expected,List<Front> owned,bool foregroundOnly)
    {
        var c=expected.Copy();c.Foreground = owned.FirstOrDefault();c.BackImage=(expected.BackImage&actual.BackImage&0x60000000);
        if(!foregroundOnly&&(expected.BackImage&0x1FFFFFFF)>0){c.BackLibrary=actual.BackLibrary;c.BackImage|=actual.BackImage&0x1FFFFFFF;}
        if(foregroundOnly){c.MiddleImage=0;c.MiddleFrames=0;c.MiddleTick=0;c.TileFrames=0;c.TileAnimation=0;c.TileOffset=0;}
        else{if(expected.MiddleImage>0){c.MiddleLibrary=actual.MiddleLibrary;c.MiddleImage=actual.MiddleImage;}if(expected.MiddleImage>0||expected.MiddleFrames>0){c.MiddleFrames=actual.MiddleFrames;c.MiddleTick=actual.MiddleTick;}if(expected.TileFrames>0){c.TileFrames=actual.TileFrames;c.TileAnimation=actual.TileAnimation;c.TileOffset=actual.TileOffset;}}
        if(expected.Door!=0){c.Door=actual.Door;c.DoorOffset=actual.DoorOffset;}if(expected.Light!=0)c.Light=actual.Light;
        return c;
    }
    private static Cell BeforeIdentification(Cell actual,Cell stamp,List<int> indices)
    {
        var before=actual.Copy();
        if((stamp.BackImage&0x1FFFFFFF)>0){before.BackImage&=0x60000000;before.BackLibrary=0;}before.BackImage&=~(stamp.BackImage&0x60000000);
        if(stamp.MiddleImage>0){before.MiddleImage=0;before.MiddleLibrary=1;before.MiddleFrames=0;before.MiddleTick=0;}
        if (indices.Count != 0) before.Foreground = null;
        if(stamp.Door!=0){before.Door=0;before.DoorOffset=0;}if(stamp.Light!=0)before.Light=0;
        if(stamp.TileFrames!=0){before.TileFrames=0;before.TileAnimation=0;before.TileOffset=0;}if(stamp.MiddleFrames!=0){before.MiddleFrames=0;before.MiddleTick=0;}
        return before;
    }
}
