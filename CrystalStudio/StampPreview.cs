using System.IO;
namespace CrystalStudio;

public static class StampPreview
{
    public static RasterImage? Create(IReadOnlyList<StampCell> cells, AssetStore assets, int width = 160, int height = 112, CancellationToken cancellation = default, Rect? area = null, bool waitForAssets = false, bool fastRaster = false)
    {
        if(width < 16 || height < 16 || width > 4096 || height > 4096) throw new ArgumentOutOfRangeException(nameof(width));
        var commands = new List<(int Layer,int X,int Y,int Library,int Image,bool Blend)>();
        foreach (var s in cells)
        {
            cancellation.ThrowIfCancellationRequested();
            if ((s.Cell.BackImage & 0x1FFFFFFF) > 0) commands.Add((0,s.X,s.Y,s.Cell.BackLibrary,(s.Cell.BackImage & 0x1FFFFFFF)-1,false));
            if (s.Cell.MiddleImage > 0) commands.Add((1,s.X,s.Y,s.Cell.MiddleLibrary,s.Cell.MiddleImage-1,MapSpriteDrawing.MiddleBlend(s.Cell.MiddleFrames)));
            foreach(var f in s.Cell.ForegroundImages) if((f.Image & 0x7FFF)>0) commands.Add((2,s.X,s.Y,f.Library,(f.Image & 0x7FFF)-1,MapSpriteDrawing.FrontBlend(f.Frames)));
        }
        // Resolve each unique sprite once and hold it through rendering. Repeated
        // placements must not repeatedly query the cache or rebuild a huge scene
        // while the asynchronous decoder is still filling it.
        var sprites=new Dictionary<(int Library,int Image),Sprite>();
        var pending=commands.Select(c=>(c.Library,c.Image)).ToHashSet();
        var timeout=System.Diagnostics.Stopwatch.StartNew();
        while(pending.Count>0)
        {
            cancellation.ThrowIfCancellationRequested();
            foreach(var key in pending.ToArray())
            {
                if(assets.Get(key.Library,key.Image) is {} sprite){sprites.Add(key,sprite);pending.Remove(key);}
                else if(assets.IsResolved(key.Library,key.Image))
                {
                    // The decoder may have finished between Get and IsResolved.
                    if(assets.Get(key.Library,key.Image) is {} loaded)sprites.Add(key,loaded);
                    pending.Remove(key);
                }
            }
            if(pending.Count==0)break;
            if(!waitForAssets)return null;
            if(timeout.Elapsed>TimeSpan.FromSeconds(30))throw new InvalidDataException("Preview artwork did not load.");
            if(cancellation.WaitHandle.WaitOne(15))cancellation.ThrowIfCancellationRequested();
        }
        Rect bounds=area??default(Rect);
        var drawings=new List<(int Layer,int X,int Y,Sprite Sprite,Rect Bounds,bool Blend)>();
        // Include the letterbox/padding in the visible region, preserving the
        // existing crop exactly, including trees anchored outside the detail area.
        Rect visible=default(Rect);
        if(area is {} a&&a.Width>0&&a.Height>0)
        {
            double scale=Math.Min((width-8)/a.Width,(height-8)/a.Height);
            visible=new(a.X-(width/scale-a.Width)/2,a.Y-(height/scale-a.Height)/2,width/scale,height/scale);
        }
        foreach(var c in commands)
        {
            cancellation.ThrowIfCancellationRequested();
            if(!sprites.TryGetValue((c.Library,c.Image),out var sprite))continue;
            var rect=MapSpriteDrawing.Bounds(sprite,c.X,c.Y,c.Library,c.Image,c.Layer>0,c.Layer==2,c.Blend);
            if(!EditorGeometry.IsEmpty(visible)&&!visible.Intersects(rect))continue;
            drawings.Add((c.Layer,c.X,c.Y,sprite,rect,c.Blend));
            if(area==null)bounds = EditorGeometry.Union(bounds, rect);
        }
        var ordered=drawings.OrderBy(c=>c.Layer).ThenBy(c=>c.Y).ThenBy(c=>c.X);
        bool blend=drawings.Any(c=>c.Blend);
        if(fastRaster&&!blend&&!EditorGeometry.IsEmpty(bounds)&&bounds.Width>0&&bounds.Height>0)
            return PreviewRaster.Create(ordered.Select(c=>(c.Sprite,c.Bounds)),bounds,width,height,cancellation);
        var visual=new SceneVisual();
        using(var dc=visual.RenderOpen())
        {
            if(!EditorGeometry.IsEmpty(bounds)&&bounds.Width>0&&bounds.Height>0)
            {
                double scale=Math.Min((width-8)/bounds.Width,(height-8)/bounds.Height);
                dc.PushTransform(new SceneTranslation((width-bounds.Width*scale)/2,(height-bounds.Height*scale)/2));
                dc.PushTransform(new SceneScale(scale,scale));dc.PushTransform(new SceneTranslation(-bounds.X,-bounds.Y));
                if(blend)
                {
                    // Additive effects need the preceding scene as their background.
                    var group=new SceneGroup();
                    foreach(var c in ordered){cancellation.ThrowIfCancellationRequested();MapSpriteDrawing.Append(group,c.Sprite,c.Bounds,c.Blend);}
                    dc.DrawDrawing(group);
                }
                else foreach(var c in ordered){cancellation.ThrowIfCancellationRequested();dc.DrawImage(c.Sprite.Bitmap,c.Bounds);}
                dc.Pop();dc.Pop();dc.Pop();
            }
        }
        cancellation.ThrowIfCancellationRequested();
        var bitmap=new RasterTarget(width,height,96,96,RasterFormats.Pbgra32);bitmap.Render(visual);return bitmap;
    }
}
