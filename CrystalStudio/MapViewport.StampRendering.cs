namespace CrystalStudio;

public sealed partial class MapViewport
{
    private IReadOnlyList<StampCell>? retainedStampCells;
    private AssetStore? retainedStampAssets;
    private bool retainedStampDoors;
    private readonly Dictionary<(int X,int Y),(SceneGroup SceneDrawing,bool Ready)> stampDrawings=[];
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<IReadOnlyList<StampCell>,Geometry> stampOutlines=new();
    private IReadOnlyList<StampCell>? stampPlacementCells;
    private (MapDocument Document,long Revision,int Count,int X,int Y,Guid? Moving)? stampPlacementKey;
    private StampPlacementResult? stampPlacement;
    private (StampPlacementResult Placement,Rect Visible,double Zoom)? stampBlockersKey;
    private SceneGroup? stampBlockers;

    private void ResetStampDrawing()
    {
        retainedStampCells=null;retainedStampAssets=null;stampDrawings.Clear();
        stampPlacementCells=null;stampPlacementKey=null;stampPlacement=null;stampBlockersKey=null;stampBlockers=null;
    }
    private void RefreshStampDrawing()
    {
        foreach(var key in stampDrawings.Where(pair=>!pair.Value.Ready).Select(pair=>pair.Key).ToArray())stampDrawings.Remove(key);
    }
    private static void DrawStampOutline(SceneContext dc,IReadOnlyList<StampCell> cells,int x,int y,ScenePen pen)
    {
        var geometry=stampOutlines.GetValue(cells,source=>
        {
            if(source.Count==0){var empty=new GeometryGroup();return empty;}
            int left=source.Min(c=>c.X),top=source.Min(c=>c.Y),right=source.Max(c=>c.X)+1,bottom=source.Max(c=>c.Y)+1;
            if((long)(right-left)*(bottom-top)==source.Count)
            {
                var rectangle=new RectangleGeometry(new(left*48.0,top*32.0,(right-left)*48.0,(bottom-top)*32.0));
                return rectangle;
            }
            var group=new GeometryGroup();
            foreach(var cell in source)group.Children.Add(new RectangleGeometry(new(cell.X*48.0,cell.Y*32.0,48,32)));
            return group;
        });
        dc.PushTransform(new SceneTranslation(x*48.0,y*32.0));dc.DrawGeometry(null,pen,geometry);dc.Pop();
    }

    // Stamp sources are stable while selected, like the retained sprite drawing.
    // Map edits, undo/redo and ownership changes all advance PickupRevision.
    private StampPlacementResult GetStampPreviewPlacement(IReadOnlyList<StampCell> cells,int x,int y,Guid? moving=null)
    {
        var key=(Document,Document.PickupRevision,cells.Count,x,y,moving);
        if(!ReferenceEquals(cells,stampPlacementCells)||stampPlacementKey!=key)
        {
            stampPlacement=Tool == EditorTool.TownStamp && Town != null && ReferenceEquals(cells,Town.Cells)
                ? TownPlacement.Check(Document,Town,x,y) : Document.CheckStampPlacement(cells,x,y,moving);
            stampPlacementCells=cells;stampPlacementKey=key;
            stampBlockers=null;stampBlockersKey=null;
        }
        return stampPlacement!;
    }

    private void DrawStampPreview(SceneContext dc,IReadOnlyList<StampCell> cells,int x,int y,Guid? moving=null)
    {
        var placement=GetStampPreviewPlacement(cells,x,y,moving);
        dc.PushOpacity(placement.Allowed ? 0.55 : 0.18);
        DrawRetainedStamp(dc,cells,x,y);dc.Pop();
        DrawStampOutline(dc,cells,x,y,cursor);
        if(placement.BlockedCells.Count==0)return;

        // Direct offscreen drawing can happen before the viewport is arranged.
        var visible=ActualWidth>0&&ActualHeight>0
            ? new Rect(-origin.X/Zoom,-origin.Y/Zoom,ActualWidth/Zoom,ActualHeight/Zoom) : default(Rect);
        if(!EditorGeometry.IsEmpty(visible))visible = visible.Inflate(new Thickness(2/Math.Max(Zoom,0.4),2/Math.Max(Zoom,0.4)));
        var key=(placement,visible,Zoom);
        if(stampBlockersKey!=key)
        {
            // One geometry per warning colour replaces thousands of individual
            // rectangle/line commands. Keep full diagnostics, but draw visible cells only.
            var occupied=new StreamGeometry();var boundary=new StreamGeometry();
            using(var occupiedContext=occupied.Open())using(var boundaryContext=boundary.Open())
            foreach(var blocked in placement.BlockedCells)
            {
                var area=new Rect(blocked.X*48.0,blocked.Y*32.0,48,32);
                if(!EditorGeometry.IsEmpty(visible)&&!area.Intersects(visible))continue;
                bool outside=blocked.Reason==StampBlockReason.MapBoundary;
                var context=outside?boundaryContext:occupiedContext;
                context.BeginFigure(area.TopLeft, true);
                context.LineTo(area.TopRight);context.LineTo(area.BottomRight);context.LineTo(area.BottomLeft);context.EndFigure(true);
                context.BeginFigure(area.TopLeft+new Point(7,7),false);
                context.LineTo(area.BottomRight-new Point(7,7));context.EndFigure(false);
                if(!outside)
                {
                    context.BeginFigure(area.TopRight+new Point(-7,7),false);
                    context.LineTo(area.BottomLeft+new Point(7,-7));context.EndFigure(false);
                }
            }
            
            var drawing=new SceneGroup();
            using(var context=drawing.Open())
            {
                DrawBlockers(occupied,Color.FromRgb(247,66,85));
                DrawBlockers(boundary,Color.FromRgb(245,174,52));
                void DrawBlockers(Geometry geometry,Color color)
                {
                    if(EditorGeometry.IsEmpty(geometry.Bounds))return;
                    context.DrawGeometry(new ImmutableSolidColorBrush(Color.FromArgb(95,color.R,color.G,color.B)),
                        new ScenePen(new ImmutableSolidColorBrush(color),2/Math.Max(Zoom,0.4)),geometry);
                }
            }
            stampBlockers=drawing;stampBlockersKey=key;
        }
        dc.DrawDrawing(stampBlockers);
    }

    // Ground tiles depend on cell parity. Retain each of the four variants so
    // moving an object translates sprite commands rather than decoding effects again.
    private void DrawRetainedStamp(SceneContext dc,IReadOnlyList<StampCell> cells,int x,int y)
    {
        if(!ReferenceEquals(cells,retainedStampCells)||!ReferenceEquals(Assets,retainedStampAssets)||DoorsOpen!=retainedStampDoors)
        {
            retainedStampCells=cells;retainedStampAssets=Assets;retainedStampDoors=DoorsOpen;stampDrawings.Clear();
        }
        var parity=(X:x&1,Y:y&1);
        if(!stampDrawings.TryGetValue(parity,out var cached))
        {
            var drawing=new SceneGroup();bool ready=true;
            using(var context=drawing.Open())
            {
            foreach(var s in cells)
            {
                int sx=parity.X+s.X,sy=parity.Y+s.Y;
                // Check before drawing: a decode completing during DrawCell must
                // not mark a drawing with a missing sprite as permanently ready.
                if(Assets is AssetStore assets)
                {
                    if(sx%2==0&&sy%2==0&&!assets.IsResolved(s.Cell.BackLibrary,(s.Cell.BackImage&0x1FFFFFFF)-1))ready=false;
                    if(!assets.IsResolved(s.Cell.MiddleLibrary,s.Cell.MiddleImage-1))ready=false;
                    foreach(var front in s.Cell.ForegroundImages)if(!assets.IsResolved(front.Library,MapPreview.FrontImage(s.Cell,front,DoorsOpen)))ready=false;
                }
            }
            // Assembled stamps use the map's layer and row order. A later ground
            // tile must never cover an earlier building in a large town preview.
            foreach(var s in cells)
                if((parity.X+s.X)%2==0&&(parity.Y+s.Y)%2==0)DrawSprite(context,parity.X+s.X,parity.Y+s.Y,s.Cell.BackLibrary,(s.Cell.BackImage&0x1FFFFFFF)-1,false);
            foreach(var s in cells.OrderBy(c=>c.Y).ThenBy(c=>c.X))
                DrawSprite(context,parity.X+s.X,parity.Y+s.Y,s.Cell.MiddleLibrary,s.Cell.MiddleImage-1,true);
            foreach(var s in cells.OrderBy(c=>c.Y).ThenBy(c=>c.X))
                foreach(var front in s.Cell.ForegroundImages)DrawSprite(context,parity.X+s.X,parity.Y+s.Y,front.Library,MapPreview.FrontImage(s.Cell,front,DoorsOpen),true,true,MapSpriteDrawing.FrontBlend(front.Frames));
            }
            cached=(drawing,ready);stampDrawings[parity]=cached;
        }
        dc.PushTransform(new SceneTranslation((x-parity.X)*48.0,(y-parity.Y)*32.0));
        dc.DrawDrawing(cached.SceneDrawing);dc.Pop();
    }
}
