using Avalonia;
using Xunit;

namespace CrystalStudio.Tests;

public sealed class MapSpriteDrawingTests
{
    [Fact]
    public void SanxiuFrontAppliesUnityMirImageHeaderOffset()
    {
        var sprite = new SpriteHeader(100, 100, 7, 9);

        var crystal = MapSpriteDrawing.Bounds(sprite, 2, 3, 2, 10, true, front: true);
        var sanxiu = MapSpriteDrawing.Bounds(sprite, 2, 3, 2, 10, true, front: true,
            format: AssetSourceFormat.Sanxiu);

        Assert.Equal(new Rect(96, 28, 100, 100), crystal);
        Assert.Equal(new Rect(103, 37, 100, 100), sanxiu);
    }

    [Fact]
    public void SanxiuDoesNotChangeGroundPlacement()
    {
        var sprite = new SpriteHeader(48, 32, 7, 9);

        var crystal = MapSpriteDrawing.Bounds(sprite, 2, 3, 0, 10, false);
        var sanxiu = MapSpriteDrawing.Bounds(sprite, 2, 3, 0, 10, false,
            format: AssetSourceFormat.Sanxiu);

        Assert.Equal(crystal, sanxiu);
    }
}
