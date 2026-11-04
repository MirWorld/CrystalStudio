using SkiaSharp;

namespace CrystalStudio.Rendering;

// Preserve painter order while submitting adjacent quads from one atlas together.
// Independent X/Y vertex positions retain the original sprite dimensions.
internal sealed class SceneAtlasBatch(RasterImage atlas, (Rect Source, Rect Destination)[] sprites) : SceneDrawing
{
    private SKVertices[]? batches;
    public override Rect Bounds { get; } = sprites.Aggregate(default(Rect), (bounds, item) => EditorGeometry.Union(bounds, item.Destination));
    public override void Render(Avalonia.Media.DrawingContext context)
    {
        foreach (var sprite in sprites) context.DrawImage(atlas, sprite.Source, sprite.Destination);
    }
    public override void Raster(SKCanvas canvas)
    {
        if (batches == null)
        {
            var result = new List<SKVertices>();
            // Four vertices per quad, with ushort indices.
            foreach (var chunk in sprites.Chunk(8192))
            {
                var positions = new SKPoint[chunk.Length * 4];
                var coordinates = new SKPoint[positions.Length];
                var indices = new ushort[chunk.Length * 6];
                for (int i = 0; i < chunk.Length; i++)
                {
                    var (s, d) = chunk[i]; int v = i * 4, k = i * 6;
                    positions[v] = new((float)d.Left, (float)d.Top); positions[v + 1] = new((float)d.Right, (float)d.Top);
                    positions[v + 2] = new((float)d.Right, (float)d.Bottom); positions[v + 3] = new((float)d.Left, (float)d.Bottom);
                    coordinates[v] = new((float)s.Left, (float)s.Top); coordinates[v + 1] = new((float)s.Right, (float)s.Top);
                    coordinates[v + 2] = new((float)s.Right, (float)s.Bottom); coordinates[v + 3] = new((float)s.Left, (float)s.Bottom);
                    indices[k] = (ushort)v; indices[k + 1] = (ushort)(v + 1); indices[k + 2] = (ushort)(v + 2);
                    indices[k + 3] = (ushort)v; indices[k + 4] = (ushort)(v + 2); indices[k + 5] = (ushort)(v + 3);
                }
                result.Add(SKVertices.CreateCopy(SKVertexMode.Triangles, positions, coordinates, null, indices));
            }
            batches = result.ToArray();
        }
        using var shader = atlas.SkiaImage.ToShader(SKShaderTileMode.Clamp, SKShaderTileMode.Clamp);
        using var paint = new SKPaint { Shader = shader, IsAntialias = false, FilterQuality = SKFilterQuality.None };
        foreach (var batch in batches) canvas.DrawVertices(batch, SKBlendMode.Modulate, paint);
    }
}
