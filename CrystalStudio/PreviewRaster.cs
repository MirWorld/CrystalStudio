using System.Runtime.CompilerServices;
namespace CrystalStudio;

/// <summary>Composite native sprites at preview resolution without a retained drawing per map cell.</summary>
internal static class PreviewRaster
{
    private sealed record Pixels(byte[] Data,int Width,int Height);
    private static readonly ConditionalWeakTable<RasterImage,Pixels> Cache=new();
    internal static RasterImage Create(IEnumerable<(Sprite Sprite,Rect Bounds)> tiles,Rect area,int width,int height,CancellationToken cancellation)
        => Create(tiles.Select(t => (t.Sprite, t.Bounds, false)), area, width, height, cancellation);

    internal static RasterImage Create(IEnumerable<(Sprite Sprite,Rect Bounds,bool Blend)> tiles,Rect area,int width,int height,CancellationToken cancellation,int padding=4,bool opaque=false)
    {
        cancellation.ThrowIfCancellationRequested();
        double scale=Math.Min((width-padding*2)/area.Width,(height-padding*2)/area.Height);
        double offsetX=(width-area.Width*scale)/2-area.X*scale;
        double offsetY=(height-area.Height*scale)/2-area.Y*scale;
        var output=new byte[checked(width*height*4)];
        if(opaque)for(int i=3;i<output.Length;i+=4)output[i]=255;
        foreach(var (sprite,bounds,blend) in tiles)
        {
            cancellation.ThrowIfCancellationRequested();
            var pixels=Cache.GetValue(sprite.Bitmap,bitmap=>
            {
                var source=bitmap.Format==RasterFormats.Pbgra32?bitmap:new ConvertedRasterImage(bitmap,RasterFormats.Pbgra32,null,0);
                var data=new byte[source.PixelWidth*source.PixelHeight*4];source.CopyPixels(data,source.PixelWidth*4,0);
                return new(data,source.PixelWidth,source.PixelHeight);
            });
            double left=bounds.X*scale+offsetX,top=bounds.Y*scale+offsetY;
            double w=bounds.Width*scale,h=bounds.Height*scale;
            int x0=Math.Max(0,(int)Math.Ceiling(left-.5)),x1=Math.Min(width,(int)Math.Ceiling(left+w-.5));
            int y0=Math.Max(0,(int)Math.Ceiling(top-.5)),y1=Math.Min(height,(int)Math.Ceiling(top+h-.5));
            for(int y=y0;y<y1;y++)
            {
                int sy=Math.Clamp((int)((y+.5-top)*pixels.Height/h),0,pixels.Height-1);
                for(int x=x0;x<x1;x++)
                {
                    int sx=Math.Clamp((int)((x+.5-left)*pixels.Width/w),0,pixels.Width-1);
                    int src=(sy*pixels.Width+sx)*4,dst=(y*width+x)*4,a=pixels.Data[src+3];
                    if(blend)
                    {
                        for(int c=0;c<3;c++)output[dst+c]=(byte)Math.Min(255,output[dst+c]+pixels.Data[src+c]);
                        output[dst+3]=Math.Max(output[dst+3],Math.Max(output[dst],Math.Max(output[dst+1],output[dst+2])));
                    }
                    else if(a==255)pixels.Data.AsSpan(src,4).CopyTo(output.AsSpan(dst,4));
                    else if(a!=0)for(int c=0;c<4;c++)output[dst+c]=(byte)Math.Min(255,pixels.Data[src+c]+(output[dst+c]*(255-a)+127)/255);
                }
            }
        }
        cancellation.ThrowIfCancellationRequested();
        var result=RasterImage.Create(width,height,96,96,RasterFormats.Pbgra32,null,output,width*4);return result;
    }
}
