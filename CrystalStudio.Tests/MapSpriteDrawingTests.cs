using Avalonia;
using Xunit;

namespace CrystalStudio.Tests;

public sealed class MapSpriteDrawingTests
{
    [Fact]
    public void SanxiuSpecialBlendAppliesThreeCellBaselineAndHeaderOffset()
    {
        var sprite = new SpriteHeader(100, 100, 7, 9);

        var crystal = MapSpriteDrawing.Bounds(sprite, 2, 3, 2, 10, true, front: true, blend: true);
        var sanxiu = MapSpriteDrawing.Bounds(sprite, 2, 3, 2201, 10, true, front: true, blend: true,
            format: AssetSourceFormat.Sanxiu);

        Assert.Equal(new Rect(96, 28, 100, 100), crystal);
        Assert.Equal(new Rect(103, 41, 100, 100), sanxiu);
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

    [Fact]
    public void SanxiuSpecialStaticFrontUsesUnityMirYAdjustmentWithoutHeaderOffset()
    {
        var sprite = new SpriteHeader(100, 100, 7, 9);
        var bounds = MapSpriteDrawing.Bounds(sprite, 2, 3, 2026, 10, true, front: true,
            format: AssetSourceFormat.Sanxiu);
        Assert.Equal(new Rect(96, 39, 100, 100), bounds);
    }

    [Fact]
    public void SanxiuSkipsLibrary2200()
    {
        Assert.True(MapSpriteDrawing.SkipFront(2200, AssetSourceFormat.Sanxiu));
        Assert.False(MapSpriteDrawing.SkipFront(2200, AssetSourceFormat.Default));
    }
}
