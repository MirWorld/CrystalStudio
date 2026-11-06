namespace CrystalStudio;

/// <summary>
/// 地图渲染锚点策略。不同地图格式的素材语义不同，各走各的处理流程；UI 操作（笔刷、
/// 选择、缩放、面板）完全共用，只在“这一格画在哪”这一层分叉。
///
/// <para><b>Crystal</b>：素材头里的 X/Y 参与特判（客户端对特定库号、图片号做了偏移修正），
/// 48×32 与 96×64 视为整块瓦片不参与底边对齐，其余尺寸按 py += 32 - Height 底边对齐。</para>
///
/// <para><b>Mir2 经典</b>：地图编辑器（EdMain.pas / o_WIL.pas）只用格子坐标算锚点，
/// WIL 记录里的 px/py 在绘制时完全不读。背景 DrawZoom(xx*48, yy*32) 与中景
/// DrawZoomEx(..., leftzero=TRUE) 都是左上角；前景 DrawZoomEx(xx*48, (yy+1)*32, ...,
/// leftzero=FALSE) 走 SpliteBitmap(X, Y - Height)，即底边贴住 (yy+1)*32。</para>
/// </summary>
internal abstract class MapSpriteLayout
{
    internal static readonly MapSpriteLayout Crystal = new CrystalSpriteLayout();
    internal static readonly MapSpriteLayout Mir2Classic = new Mir2ClassicSpriteLayout();

    /// <summary>按文档记住的源格式选择流程；Crystal 地图返回 Crystal 流程。</summary>
    internal static MapSpriteLayout For(MapDocument? document) =>
        document?.SourceMir2Kind is null ? Crystal : Mir2Classic;

    internal abstract string Name { get; }

    internal abstract Rect Bounds(SpriteHeader sprite, int x, int y, int library, int image,
        bool anchored, bool front, bool blend);

    /// <summary>中景动画帧数；0 或 1 表示不播放。</summary>
    internal abstract int MiddleFrames(byte frames);

    /// <summary>中景是否走加色合成。</summary>
    internal abstract bool MiddleBlend(byte frames);

    /// <summary>前景是否走加色合成。两种格式都是 AniFrame 最高位。</summary>
    internal bool FrontBlend(byte frames) => (frames & 0x80) != 0;

    /// <summary>前景动画帧数，两种格式都是 AniFrame 低 7 位。</summary>
    internal int FrontFrames(byte frames) => frames & 0x7F;
}

/// <summary>Crystal 客户端流程：保留素材头 X/Y 特判与整块瓦片判定。</summary>
internal sealed class CrystalSpriteLayout : MapSpriteLayout
{
    internal override string Name => "Crystal";

    internal override Rect Bounds(SpriteHeader sprite, int x, int y, int library, int image,
        bool anchored, bool front, bool blend)
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

    internal override int MiddleFrames(byte frames) => frames == 255 ? 0 :
        (frames & 15) > 0 ? frames & 15 : frames;

    internal override bool MiddleBlend(byte frames) => frames != 255 && (frames & 15) is 8 or 10;
}

/// <summary>
/// Mir2 经典流程：锚点只由格子坐标决定，素材记录里的 px/py 一律忽略。
/// 中景动画直接读 AniFrame 低 7 位，没有 Crystal 那套位标志语义。
/// </summary>
internal sealed class Mir2ClassicSpriteLayout : MapSpriteLayout
{
    internal override string Name => "Mir2 经典";

    internal override Rect Bounds(SpriteHeader sprite, int x, int y, int library, int image,
        bool anchored, bool front, bool blend)
    {
        // 加色前景是唯一读 WIL px/py 的分支。PlayScene.pas 里：
        //   mmm := m + ay - 68;
        //   DrawBlend(m_ObjSurface, n + ax - 2, mmm, DSurface, 1);
        // 其中 n = x*UNITX、m = y*UNITY、ax/ay 就是素材记录的 px/py。
        if (front && blend)
            return new(x * 48.0 + sprite.X - 2, y * 32.0 + sprite.Y - 68, sprite.Width, sprite.Height);
        // EdMain.pas: dx := Trunc(x * UNITX * Zoom)；前景 dy := Trunc((y + 1) * UNITY * Zoom)
        // 再由 DrawZoomEx 的 leftzero=FALSE 分支减去图像高度。
        double px = x * 48.0;
        double py = front ? (y + 1) * 32.0 - sprite.Height : y * 32.0;
        return new(px, py, sprite.Width, sprite.Height);
    }

    internal override int MiddleFrames(byte frames) => frames & 0x7F;

    internal override bool MiddleBlend(byte frames) => false;
}
