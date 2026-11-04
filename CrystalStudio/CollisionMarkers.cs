namespace CrystalStudio;

internal static class CollisionMarkers
{
    internal static int Type(int backImage) => (backImage & 0x60000000) >> 29;
    private static readonly Color First = Color.FromArgb(90, 240, 85, 105);
    private static readonly Color Second = Color.FromArgb(90, 65, 165, 245);
    private static readonly Brush FirstBrush = FrozenBrush(First);
    private static readonly Brush SecondBrush = FrozenBrush(Second);

    private static Brush FrozenBrush(Color colour)
    {
        var brush = new ImmutableSolidColorBrush(colour);  return brush;
    }

    internal static void Draw(SceneContext dc, int type, Rect cell)
    {
        if (type == 0) return;
        if (type == 3)
        {
            dc.DrawRectangle(FirstBrush, null, new(cell.X, cell.Y, cell.Width / 2, cell.Height));
            dc.DrawRectangle(SecondBrush, null, new(cell.X + cell.Width / 2, cell.Y, cell.Width / 2, cell.Height));
        }
        else dc.DrawRectangle(type == 1 ? FirstBrush : SecondBrush, null, cell);
    }

    internal static RasterImage Texture(int type)
    {
        Color left = type == 2 ? Second : First, right = type == 1 ? First : Second;
        var bitmap = RasterImage.Create(2, 1, 96, 96, RasterFormats.Bgra32, null,
            new byte[] { left.B, left.G, left.R, left.A, right.B, right.G, right.R, right.A }, 8);
         return bitmap;
    }
}
