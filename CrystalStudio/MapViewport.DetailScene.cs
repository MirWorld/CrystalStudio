namespace CrystalStudio;

public sealed partial class MapViewport
{
    private SceneGroup? detailScene;
    private Rect detailCoverage;
    private int detailAssetRevision,detailSceneBuilds;
    private DetailState detailState;
    private readonly Dictionary<(int X,int Y),bool> middleBlendChunks=[];
    private readonly Dictionary<(int X,int Y),bool> frontBlendRows=[];
    private readonly record struct DetailState(MapDocument Document,long Revision,AssetStore? Assets,int AssetRevision,
        int EditRevision,int Frame,int Layers,bool DoorsOpen,Brush Paper);

    private void ResetDetailScene(){detailScene=null;detailCoverage=default(Rect);detailEffects.Clear();middleBlendChunks.Clear();frontBlendRows.Clear();}
    private bool HasMiddleBlend(int cx,int cy)
    {
        var key=(cx,cy);if(middleBlendChunks.TryGetValue(key,out bool blend))return blend;
        for(int y=cy*16;y<Math.Min(Document.Height,cy*16+16)&&!blend;y++)
            for(int x=cx*16;x<Math.Min(Document.Width,cx*16+16);x++)
                if(Document.Get(x,y) is { MiddleImage: > 0, MiddleLibrary: >= 0 } cell && MapSpriteDrawing.MiddleBlend(cell.MiddleFrames)){blend=true;break;}
        return middleBlendChunks[key]=blend;
    }
    private bool HasFrontBlend(int cx,int y)
    {
        var key=(cx,y);if(frontBlendRows.TryGetValue(key,out bool blend))return blend;
        for(int x=cx*16;x<Math.Min(Document.Width,cx*16+16)&&!blend;x++)
            if(Document.Get(x,y).Foreground is { Library: >= 0 } front && (front.Image&0x7FFF)!=0 && MapSpriteDrawing.FrontBlend(front.Frames)){blend=true;break;}
        return frontBlendRows[key]=blend;
    }
    private void AdvanceAnimation()
    {
        if(Zoom<.25||animatedChunks.Count==0&&animatedRows.Count==0)return;
        animationFrame++;
        foreach(var key in animatedChunks)chunks.Remove(key);
        foreach(var key in animatedRows)frontRows.Remove(key);
        animatedChunks.Clear();animatedRows.Clear();sceneDirty=true;InvalidateVisual();
    }
    private bool ReuseDetailScene()
    {
        var visible=new Rect(-origin.X/Zoom,-origin.Y/Zoom,ActualWidth/Zoom,ActualHeight/Zoom);
        int layers=(VisibleLayers[0]?1:0)|(VisibleLayers[1]?2:0)|(VisibleLayers[2]?4:0);
        var next=new DetailState(Document,Document.PickupRevision,Assets,detailAssetRevision,editRevision,animationFrame,layers,DoorsOpen,paper);
        if(detailScene!=null&&next==detailState&&detailCoverage.Contains(visible))return true;
        // Blend classification depends on cells, not the camera or animation frame.
        // Bound these inexpensive indexes and discard them on any document edit.
        if(next.Document!=detailState.Document||next.Revision!=detailState.Revision||middleBlendChunks.Count>2048||frontBlendRows.Count>8192)
        {middleBlendChunks.Clear();frontBlendRows.Clear();}
        if(!ReferenceEquals(detailState.Assets,Assets))detailEffects.Clear();
        // A small world-space margin keeps camera motion independent of sprite
        // composition until the viewport reaches newly visible map content.
        visible = visible.Inflate(new Thickness(6*48,4*32));detailCoverage=visible;detailState=next;detailScene=null;return false;
    }
}
