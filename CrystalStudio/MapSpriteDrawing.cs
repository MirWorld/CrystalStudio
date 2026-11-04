namespace CrystalStudio;

internal static class MapSpriteDrawing
{
    internal static bool FrontBlend(byte frames) => (frames & 0x80) != 0;
    internal static int MiddleFrames(byte frames) => frames == 255 ? 0 :
        (frames & 15) > 0 ? frames & 15 : frames;
    internal static bool MiddleBlend(byte frames) => frames != 255 && (frames & 15) is 8 or 10;

    internal static Rect Bounds(Sprite sprite, int x, int y, int library, int image,
        bool anchored, bool front = false, bool blend = false)
    {
        double px = x * 48.0, py = y * 32.0;
        bool tile = (sprite.Width == 48 && sprite.Height == 32) || (sprite.Width == 96 && sprite.Height == 64);
        if (front && blend)
        {
            if (library is 14 or 27 || library is > 99 and < 199)
            { px += sprite.X; py += 32 - 96 + sprite.Y; }
            else
            {
                py += 32 - sprite.Height;
                if (image is >= 2723 and <= 2732) { px += sprite.X; py += sprite.Y; }
            }
        }
        else if (front && library == 28 && (sprite.X != 0 || sprite.Y != 0))
        { px += sprite.X; py += sprite.Y; }
        else if (anchored && (!tile || blend)) py += 32 - sprite.Height;
        return new(px, py, sprite.Width, sprite.Height);
    }

    // Keep the original additive compositing contract. Capture only the effect's rectangle
    // from the preceding scene and add premultiplied RGB, just like SourceAlpha/One.
    // The resulting patch stays in painter order, so later objects still occlude it.
    internal static RasterImage Additive(Sprite sprite, Rect bounds, SceneDrawing background)
    {
        var behind=new SceneGroup();CollectIntersecting(background,bounds,behind.Children);
        return Composite(sprite,bounds,behind);
    }

    private static RasterImage Composite(Sprite sprite,Rect bounds,SceneDrawing background,bool rasterize=false)
    {
        int width = sprite.Bitmap.PixelWidth, height = sprite.Bitmap.PixelHeight;
        byte[] destination = new byte[width * height * 4], source = new byte[destination.Length];
        if(!rasterize||!TryRasterBackground(background,bounds,width,height,destination))
        {
            var visual = new SceneVisual();
            
            using (var dc = visual.RenderOpen())
            {
                dc.PushTransform(new SceneScale(width / bounds.Width, height / bounds.Height));
                dc.PushTransform(new SceneTranslation(-bounds.X, -bounds.Y));
                dc.DrawDrawing(background);
            }
            var target = new RasterTarget(width, height, 96, 96, RasterFormats.Pbgra32);
            target.Render(visual);
            target.CopyPixels(destination, width * 4, 0);
        }
        var premultiplied = sprite.Bitmap.Format == RasterFormats.Pbgra32 ? sprite.Bitmap :
            new ConvertedRasterImage(sprite.Bitmap, RasterFormats.Pbgra32, null, 0);
        premultiplied.CopyPixels(source, width * 4, 0);
        for (int i = 0; i < source.Length; i += 4)
        {
            for (int c = 0; c < 3; c++) destination[i + c] = (byte)Math.Min(255, destination[i + c] + source[i + c]);
            destination[i + 3] = Math.Max(destination[i + 3], Math.Max(destination[i], Math.Max(destination[i + 1], destination[i + 2])));
        }
        var bitmap = RasterImage.Create(width, height, 96, 96, RasterFormats.Pbgra32, null, destination, width * 4);
         return bitmap;
    }

    // Most native map artwork uses opaque/transparent pixels at integer positions.
    // Copy those pixels directly to avoid allocating a raster surface for each
    // animated light. Use Skia for scaled or partially transparent artwork.
    internal static bool TryRasterBackground(SceneDrawing background,Rect bounds,int width,int height,byte[] pixels)
    {
        static bool Integral(Rect r)=>r.X==Math.Floor(r.X)&&r.Y==Math.Floor(r.Y)&&
            r.Width==Math.Floor(r.Width)&&r.Height==Math.Floor(r.Height);
        if(background is not SceneGroup group||bounds.Width!=width||bounds.Height!=height||!Integral(bounds))return false;
        if(group.Opacity!=1||group.OpacityMask!=null||group.ClipGeometry!=null||group.GuidelineSet!=null||
            (group.Transform!=null&&!group.Transform.Value.IsIdentity))return false;
        foreach(var drawing in group.Children)
        {
            Rect area;
            RasterImage? bitmap=null;Color colour=default;
            if(drawing is SceneImage {ImageSource:RasterImage image} picture&&
                picture.Rect.Width==image.PixelWidth&&picture.Rect.Height==image.PixelHeight&&
                (image.Format==RasterFormats.Bgra32||image.Format==RasterFormats.Pbgra32))
            {area=picture.Rect;bitmap=image;}
            else if(drawing is SceneRectangle {Pen:null,Brush:ISolidColorBrush brush} rectangle &&
                brush.Opacity==1&&brush.Color.A==255)
            {area=rectangle.Rect;colour=brush.Color;}
            else return false;
            if(!Integral(area))return false;
            var clip=EditorGeometry.Intersect(area,bounds);if(EditorGeometry.IsEmpty(clip)||clip.Width==0||clip.Height==0)continue;
            int x=(int)(clip.X-bounds.X),y=(int)(clip.Y-bounds.Y),w=(int)clip.Width,h=(int)clip.Height;
            if(bitmap==null)
            {
                for(int row=0;row<h;row++)for(int column=0;column<w;column++)
                {int offset=((y+row)*width+x+column)*4;pixels[offset]=colour.B;pixels[offset+1]=colour.G;pixels[offset+2]=colour.R;pixels[offset+3]=255;}
                continue;
            }
            byte[] buffer=System.Buffers.ArrayPool<byte>.Shared.Rent(w*h*4);
            try
            {
                bitmap.CopyPixels(new PixelRect((int)(clip.X-area.X),(int)(clip.Y-area.Y),w,h),buffer,w*4,0);
                for(int row=0;row<h;row++)for(int column=0;column<w;column++)
                {
                    int source=(row*w+column)*4;byte alpha=buffer[source+3];
                    if(alpha==0)continue;
                    if(alpha!=255)return false;
                    int target=((y+row)*width+x+column)*4;
                    pixels[target]=buffer[source];pixels[target+1]=buffer[source+1];pixels[target+2]=buffer[source+2];pixels[target+3]=255;
                }
            }
            finally{System.Buffers.ArrayPool<byte>.Shared.Return(buffer);}
        }
        return true;
    }

    // Effect patches only need the drawings beneath their rectangle. Traversing
    // the entire dense map through RasterTarget for every effect is costly.
    private static void CollectIntersecting(SceneDrawing drawing,Rect bounds,SceneCollection drawings)
    {
        if(!drawing.Bounds.Intersects(bounds))return;
        if(drawing is SceneGroup group&&group.Opacity==1&&group.OpacityMask==null&&group.ClipGeometry==null&&
            (group.Transform==null||group.Transform.Value.IsIdentity)&&group.GuidelineSet==null)
        {
            foreach(var child in group.Children)CollectIntersecting(child,bounds,drawings);
        }
        else drawings.Add(drawing);
    }

    // A detail scene is assembled in painter order. Index each appended drawing
    // once so a small animated light does not rescan the entire growing scene.
    // Children must remain unchanged after insertion into this append-only scene.
    internal sealed class BackgroundIndex(SceneGroup scene)
    {
        private const int RowHeight=128;
        private readonly List<(SceneDrawing SceneDrawing,Rect Bounds)> drawings=[];
        private readonly Dictionary<int,List<int>> rows=[];
        private readonly List<int> spanning=[];

        internal SceneGroup Behind(Rect bounds)
        {
            for(int i=drawings.Count;i<scene.Children.Count;i++)
            {
                var drawing=scene.Children[i];var area=drawing.Bounds;
                drawings.Add((drawing,area));
                if(EditorGeometry.IsEmpty(area))continue;
                int top=(int)Math.Floor(area.Top/RowHeight),bottom=(int)Math.Floor(area.Bottom/RowHeight);
                // The map paper can cover millions of cells; keep it out of buckets.
                if((long)bottom-top>64){spanning.Add(i);continue;}
                for(int y=top;y<=bottom;y++)
                {
                    if(!rows.TryGetValue(y,out var row))rows[y]=row=[];
                    row.Add(i);
                }
            }
            var candidates=new HashSet<int>(spanning);
            int first=(int)Math.Floor(bounds.Top/RowHeight),last=(int)Math.Floor(bounds.Bottom/RowHeight);
            for(int y=first;y<=last;y++)
                if(rows.TryGetValue(y,out var row))candidates.UnionWith(row);
            var behind=new SceneGroup();
            foreach(int i in candidates.Order())
                if(drawings[i].Bounds.Intersects(bounds))
                    CollectIntersecting(drawings[i].SceneDrawing,bounds,behind.Children);
            return behind;
        }
    }

    internal sealed class AdditiveCache
    {
        private readonly record struct Input(object Source,Rect Bounds,Brush? Brush=null,ScenePen? ScenePen=null);
        private sealed record Entry((Sprite Sprite,Rect Bounds) Key,Input[] Inputs,RasterImage Bitmap);
        private readonly Dictionary<(Sprite,Rect),LinkedListNode<Entry>> entries=[];
        private readonly LinkedList<Entry> recent=[];
        private long bytes;
        internal int Captures {get;private set;}
        internal void Clear(){entries.Clear();recent.Clear();bytes=0;}
        internal RasterImage Get(Sprite sprite,Rect bounds,SceneDrawing background,BackgroundIndex? index=null)
        {
            var behind=index?.Behind(bounds)??new SceneGroup();
            if(index==null)CollectIntersecting(background,bounds,behind.Children);
            var inputs=behind.Children.Select(d=>d switch
            {
                SceneImage image=>new Input(image.ImageSource,image.Rect),
                SceneRectangle rectangle=>new Input(rectangle.Rect,rectangle.Bounds,rectangle.Brush,rectangle.Pen),
                _=>new Input(d,d.Bounds)
            }).ToArray();
            var key=(sprite,bounds);
            if(entries.TryGetValue(key,out var node))
            {
                if(inputs.AsSpan().SequenceEqual(node.Value.Inputs))
                {recent.Remove(node);recent.AddLast(node);return node.Value.Bitmap;}
                Remove(node);
            }
            var bitmap=Composite(sprite,bounds,behind,rasterize:true);Captures++;
            long size=(long)bitmap.PixelWidth*bitmap.PixelHeight*4;
            // Bound retained effect patches independently of the sprite atlas cache.
            const long limit=32L*1024*1024;
            if(size<=limit)
            {
                while(bytes+size>limit&&recent.First is {} first)Remove(first);
                node=recent.AddLast(new Entry(key,inputs,bitmap));entries[key]=node;bytes+=size;
            }
            return bitmap;
        }
        private void Remove(LinkedListNode<Entry> node)
        {entries.Remove(node.Value.Key);recent.Remove(node);bytes-=(long)node.Value.Bitmap.PixelWidth*node.Value.Bitmap.PixelHeight*4;}
    }

    internal static void Append(SceneGroup scene, Sprite sprite, Rect bounds, bool blend,AdditiveCache? cache=null,BackgroundIndex? index=null)
    {
        var bitmap = blend ? cache?.Get(sprite,bounds,scene,index)??Additive(sprite, bounds, scene) : sprite.Bitmap;
        scene.Children.Add(new SceneImage(bitmap, bounds));
    }
}
