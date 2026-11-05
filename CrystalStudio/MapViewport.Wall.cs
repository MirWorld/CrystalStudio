namespace CrystalStudio;

public sealed partial class MapViewport
{
    private WallFamily? wallFamily;
    public WallFamily? WallFamily
    {
        get => wallFamily;
        set { if (wallFamily != value) CancelWallDraft(); wallFamily = value; }
    }
    private bool eraseWall;
    public bool EraseWall
    {
        get => eraseWall;
        set { if (eraseWall != value) CancelWallDraft(); eraseWall = value; }
    }
    public string? WallCatalogueError { get; set; }
    private (MapDocument Document, long Revision, WallFamily Family, int X, int Y, bool Erase, (int X,int Y)? Start)? wallPlanKey;
    private WallPlan? wallPlan;
    private (WallFamily Family, AssetStore? Assets)? wallAvailabilityKey;
    private string? wallAvailability;
    private (int X, int Y)? lastWall;
    private readonly HashSet<(int X, int Y)> pendingWall = [];
    private readonly List<((int X, int Y) Point, bool Added)> wallStrokePath = [];
    private bool pendingWallErase;
    private long pendingWallRevision;
    private (int X, int Y)? wallRunOrigin, wallRunEnd;
    private long wallRunRevision;
    private bool wallRunErase;
    private WallPlan? previewedWallPlan;
    private AssetStore? previewedWallAssets;
    private int previewedWallLayers;
    private bool previewedWallDoors;
    private int previewedWallAssetRevision;
    private long previewedWallAnimation;
    private Brush? previewedWallPaper;
    private SceneGroup? wallResultPreview;
    private bool WallErasing => EraseWall || InputState.IsKeyDown(Key.LeftShift) || InputState.IsKeyDown(Key.RightShift);

    private bool TouchesWallDraft((int X, int Y) point) => pendingWall.Contains(point) ||
        WallFamily is { } family && family.Directions.Any(d => pendingWall.Contains((point.X+d.X, point.Y+d.Y)));

    private void CancelWallDraft(bool notify = false)
    {
        bool hadDraft = pendingWall.Count > 0;
        pendingWall.Clear(); wallStrokePath.Clear(); lastWall = null; wallPlanKey = null;
        wallRunOrigin = wallRunEnd = null;
        if (notify && hadDraft) Message?.Invoke(LocalText.T("Unfinished wall cancelled · Completed walls kept"));
    }

    private void BeginWallStroke(int x, int y)
    {
        if (WallFamily is { PaintsAreas: true } area)
        {
            var pressed=SnapWallArea(area,x,y);
            if (pendingWallRevision!=Document.PickupRevision || pendingWallErase!=WallErasing ||
                !pendingWall.Any(p=>
                {
                    var (u,v)=area.LatticeCoordinates(p.X-pressed.X,p.Y-pressed.Y);
                    return Math.Abs(u)<=area.StrokeGridPieces && Math.Abs(v)<=area.StrokeGridPieces;
                })) CancelWallDraft();
            lastWall=null;
            return;
        }
        if (WallFamily is { StrokeGridPieces: > 1 } narrow)
        {
            var pressed = narrow.Snap(x,y);
            // A press on an existing section starts there, including the side
            // of a corridor. It must not jump back to the last stroke's tip.
            bool onWall = IsPaintedWall(narrow,pressed);
            if (onWall || !CanContinueWallRun(narrow,x,y,WallErasing)) CancelWallDraft();
            if (onWall)
            {
                wallRunOrigin = wallRunEnd = pressed;
                wallRunRevision = Document.PickupRevision; wallRunErase = WallErasing;
            }
            lastWall = null;
            return;
        }
        var point = WallFamily?.Snap(x,y) ?? (x,y);
        // A nearby click continues the visible draft. A separate placement
        // starts independently and never interpolates across the mouse release.
        if (pendingWallRevision != Document.PickupRevision || pendingWallErase != WallErasing || !TouchesWallDraft(point))
            CancelWallDraft();
        lastWall = null; wallStrokePath.Clear();
    }

    public WallPlan? PlanWall(int x, int y, bool erase = false)
    {
        if (WallFamily is not { } family) return null;
        if (family.PaintsAreas)
        {
            var end=SnapWallArea(family,x,y);
            var areaKey=(Document,Document.PickupRevision,family,end.X,end.Y,erase,lastWall);
            if (wallPlanKey!=areaKey)
            {
                var pending=pendingWallRevision==Document.PickupRevision && pendingWallErase==erase
                    ? pendingWall : [];
                wallPlan=WallBrush.PlanStroke(Document,family,pending.Concat(WallAreaPoints(family,end)),erase);
                wallPlanKey=areaKey;
            }
            return wallPlan;
        }
        if (family.StrokeGridPieces > 1)
        {
            var request = WallRunRequest(family,x,y,erase);
            var runKey = (Document,Document.PickupRevision,family,request.End.X,request.End.Y,erase,
                request.Continues ? wallRunEnd : null);
            if (wallPlanKey != runKey)
            { wallPlan = WallBrush.PlanStroke(Document,family,pendingWall.Concat(request.Points),erase); wallPlanKey = runKey; }
            return wallPlan;
        }
        var point = family.Snap(x, y);
        if (pendingWall.Count > 0 && erase == pendingWallErase && pendingWallRevision == Document.PickupRevision &&
            (dragging || TouchesWallDraft(point)))
            return WallBrush.PlanStroke(Document, family, pendingWall.Append(point), erase);
        var key = (Document, Document.PickupRevision, family, point.X, point.Y, erase, ((int X,int Y)?)null);
        if (wallPlanKey != key)
        { wallPlan = WallBrush.Plan(Document, family, point.X, point.Y, erase); wallPlanKey = key; }
        return wallPlan;
    }

    public bool PaintWall(int x, int y, bool erase = false)
    {
        if (!AllowEdit()) return false;
        if (WallFamily is not { } family) { CancelWallDraft(); Message?.Invoke((WallCatalogueError ?? LocalText.T("Choose a wall type in Wall settings (W)"))); return false; }
        var availabilityKey = (family, Assets);
        if (wallAvailabilityKey != availabilityKey)
        { wallAvailability = family.Availability(Assets); wallAvailabilityKey = availabilityKey; }
        if (!erase && wallAvailability != null) { CancelWallDraft(); Message?.Invoke((wallAvailability)); return false; }
        var plan = PlanWall(x, y, erase)!;
        // A rejected attempt is discarded, including its interpolation origin.
        // It must not become an invisible draft combined with later placements.
        if (!plan.Allowed) { CancelWallDraft(); Message?.Invoke((plan.Error!)); return false; }
        if (!plan.HasChanges) return true;
        bool applied = Document.ApplyWallPlan(plan, History, InvalidateCell);
        if (!applied) CancelWallDraft();
        if (applied) Message?.Invoke(((erase ? (LocalText.T("Wall section removed · Connections updated")) : (LocalText.Format("{0} · Connections updated", family.DisplayName)))));
        return applied;
    }

    private void PaintWallStroke(int x, int y)
    {
        if (!AllowEdit()) return;
        if (!Document.Contains(x,y)) { lastWall=null; return; }
        if (WallFamily is not { } family) { PaintWall(x, y, WallErasing); return; }
        if (family.PaintsAreas)
        {
            var nextArea=SnapWallArea(family,x,y);
            if (pendingWallErase!=WallErasing || pendingWallRevision!=Document.PickupRevision)
            { pendingWall.Clear(); lastWall=null; }
            if (lastWall==nextArea) return;
            pendingWall.UnionWith(WallAreaPoints(family,nextArea));
            pendingWallErase=WallErasing;
            pendingWallRevision=Document.PickupRevision;
            wallPlanKey=null;
            if (!PaintWall(x,y,WallErasing)) return;
            pendingWall.Clear();
            lastWall=nextArea;
            pendingWallRevision=Document.PickupRevision;
            wallPlanKey=null;
            return;
        }
        if (family.StrokeGridPieces > 1)
        {
            var request = WallRunRequest(family,x,y,WallErasing);
            if (lastWall == request.End) return;
            pendingWall.UnionWith(request.Points);
            pendingWallErase = WallErasing;
            wallPlanKey = null;
            if (!PaintWall(x,y,WallErasing)) return;
            pendingWall.Clear();
            if (!request.Continues) wallRunOrigin = request.End;
            wallRunEnd = request.End;
            wallRunRevision = Document.PickupRevision;
            wallRunErase = WallErasing;
            lastWall = request.End;
            pendingWallRevision = Document.PickupRevision;
            wallPlanKey = null;
            return;
        }
        var next = family.Snap(x, y);
        if (lastWall == next) return;
        if (family.FitsNativeCorners)
        {
            if (pendingWall.Count > 0 && pendingWallErase != WallErasing) CancelWallDraft();
            foreach (var point in lastWall is { } previous ? WallBrush.Line(family, previous, next).Skip(1) : [next])
            {
                // Retracing an unplaced tail removes that tail, instead of
                // keeping an accidental short bend in every later plan.
                if (wallStrokePath.Count >= 2 && wallStrokePath[^2].Point == point)
                {
                    var tail = wallStrokePath[^1];
                    if (tail.Added) pendingWall.Remove(tail.Point);
                    wallStrokePath.RemoveAt(wallStrokePath.Count-1);
                }
                else wallStrokePath.Add((point,pendingWall.Add(point)));
            }
            lastWall = next;
            pendingWallErase = WallErasing;
            pendingWallRevision = Document.PickupRevision;
            if (!PaintWall(next.X, next.Y, WallErasing)) return;
            pendingWall.Clear(); wallStrokePath.Clear(); wallStrokePath.Add((next,false));
            pendingWallRevision = Document.PickupRevision;
            wallPlanKey = null;
            return;
        }
        if (lastWall is { } start)
        {
            foreach (var point in WallBrush.Line(family, start, next).Skip(1))
                if (!PaintWall(point.X, point.Y, WallErasing)) { lastWall = null; return; }
        }
        else if (!PaintWall(next.X, next.Y, WallErasing)) return;
        lastWall = next;
    }

    private void DrawWallBrush(SceneContext dc)
    {
        if (WallFamily is not { } family) return;
        var point = family.PaintsAreas ? SnapWallArea(family,hover.X,hover.Y) :
            family.StrokeGridPieces > 1 ? WallRunRequest(family,hover.X,hover.Y,WallErasing).End : family.Snap(hover.X, hover.Y);
        // The current stroke endpoint has already been committed and is in
        // the map scene. Do not plan that same edit again just to draw a cursor.
        var plan = dragging && lastWall==point && pendingWall.Count==0 ? null : PlanWall(hover.X, hover.Y, WallErasing);
        if (plan is { Allowed:false }) return;
        var shape = new StreamGeometry();
        using (var path = shape.Open())
        {
            var centre=family.GridPoint(family.PaintSizePieces-1,family.PaintSizePieces-1);
            var vertices=new[] {4,1,8,2}.Select(bit=>family.VertexNeighbours.Single(v=>v.Bit==bit))
                .Select(v=>new Point((point.X+centre.X/2.0+v.X*family.PaintSizePieces/2.0)*48,
                    (point.Y+centre.Y/2.0+v.Y*family.PaintSizePieces/2.0)*32)).ToArray();
            path.BeginFigure(vertices[0], false);
            foreach (var vertex in vertices.Skip(1)) path.LineTo(vertex); path.EndFigure(true);
        }
        if (plan is { HasChanges:true } && VisibleLayers[2]) DrawWallResultPreview(plan);
        dc.DrawGeometry(null, cursor, shape);
    }

    private bool HasWallRun(bool erase) => wallRunOrigin != null && wallRunEnd != null &&
        wallRunRevision == Document.PickupRevision && wallRunErase == erase;

    private static (int X,int Y) SnapWallArea(WallFamily family,int x,int y)
    {
        var centre=family.GridPoint(family.PaintSizePieces-1,family.PaintSizePieces-1);
        var (u,v)=family.LatticeCoordinates(x-centre.X/2,y-centre.Y/2);
        int spacing=family.StrokeGridPieces;
        return family.GridPoint(spacing*(int)Math.Round(u/spacing,MidpointRounding.AwayFromZero),
            spacing*(int)Math.Round(v/spacing,MidpointRounding.AwayFromZero));
    }

    private IEnumerable<(int X,int Y)> WallAreaPoints(WallFamily family,(int X,int Y) end)
    {
        var centers=new HashSet<(int X,int Y)> {end};
        if (lastWall is { } previous)
            foreach (var corner in WallBrush.Line(family,previous,end,family.StrokeGridPieces).Skip(1))
            {
                centers.UnionWith(WallBrush.Line(family,previous,corner));
                previous=corner;
            }
        foreach (var center in centers)
        for (int u=0;u<family.PaintSizePieces;u++)
        for (int v=0;v<family.PaintSizePieces;v++)
        {
            var offset=family.GridPoint(u,v);
            yield return (center.X+offset.X,center.Y+offset.Y);
        }
    }

    private (int X,int Y) SnapWallRun(WallFamily family,int x,int y)
    {
        var origin = wallRunOrigin!.Value;
        var (u,v) = family.LatticeCoordinates(x-origin.X,y-origin.Y);
        int size = family.StrokeGridPieces;
        var offset = family.GridPoint(size*(int)Math.Round(u/size,MidpointRounding.AwayFromZero),
            size*(int)Math.Round(v/size,MidpointRounding.AwayFromZero));
        return (origin.X+offset.X,origin.Y+offset.Y);
    }

    private bool CanContinueWallRun(WallFamily family,int x,int y,bool erase)
    {
        if (!HasWallRun(erase)) return false;
        var next = SnapWallRun(family,x,y);
        var last = wallRunEnd!.Value;
        var (u,v) = family.LatticeCoordinates(next.X-last.X,next.Y-last.Y);
        // Adjacent clicks continue the visible narrow corridor. Distant clicks
        // start a new shoulder pair; only held strokes interpolate farther.
        return Math.Abs(u)+Math.Abs(v) <= family.StrokeGridPieces;
    }

    private ((int X,int Y) End,List<(int X,int Y)> Points,bool Continues) WallRunRequest(WallFamily family,int x,int y,bool erase)
    {
        bool continues = HasWallRun(erase) && (dragging || CanContinueWallRun(family,x,y,erase));
        var hovered = family.Snap(x,y);
        var end = continues && !IsPaintedWall(family,hovered) ? SnapWallRun(family,x,y) : hovered;
        if (!continues) return (end,[end],false);
        var points = new List<(int X,int Y)>();
        var previous = wallRunEnd!.Value;
        points.Add(previous);
        var delta=family.LatticeCoordinates(end.X-previous.X,end.Y-previous.Y);
        if ((int)Math.Round(delta.U)%family.StrokeGridPieces!=0 || (int)Math.Round(delta.V)%family.StrokeGridPieces!=0)
        {
            // Separate strokes may have different starting phases. Joining a
            // visible section uses its actual anchor, not the old stroke grid.
            var offset = Math.Abs(delta.U)>=Math.Abs(delta.V)
                ? family.GridPoint((int)Math.Round(delta.U),0) : family.GridPoint(0,(int)Math.Round(delta.V));
            var bend=(X:previous.X+offset.X,Y:previous.Y+offset.Y);
            points.AddRange(WallBrush.Line(family,previous,bend).Skip(1));
            points.AddRange(WallBrush.Line(family,bend,end).Skip(1));
            return (end,points,true);
        }
        foreach (var corner in WallBrush.Line(family,previous,end,family.StrokeGridPieces).Skip(1))
        {
            points.AddRange(WallBrush.Line(family,previous,corner).Skip(1));
            previous = corner;
        }
        return (end,points,true);
    }

    private bool IsPaintedWall(WallFamily family,(int X,int Y) point) => Document.PlacedStamps.Any(s=>
        s.X==point.X && s.Y==point.Y && s.Wall?.FamilyId==family.Id && s.Wall.Interior==family.DrawInterior &&
        s.Wall.PlacementStep==family.Step);

    private void DrawWallResultPreview(WallPlan plan)
    {
        if (previewedWallPlan!=plan || previewedWallAssets!=Assets ||
            previewedWallLayers!=RenderLayerSettings || previewedWallDoors!=DoorsOpen ||
            previewedWallAssetRevision!=detailAssetRevision || previewedWallAnimation!=animationFrame || previewedWallPaper!=paper)
        {
            // Repaint only the changed sprite area with the planned cells. A
            // translucent copy over the current wall shows both old shoulders
            // and new shoulders, including joins that will not exist on click.
            var area=default(Rect);
            foreach(var (point,after) in plan.Cells)
            {
                var before=Document.Get(point.X,point.Y);
                if((before.Foreground == after.Foreground))continue;
                foreach(var cell in new[]{before,after})
                foreach(var front in cell.ForegroundImages)
                {
                    int index=MapPreview.FrontImage(cell,front,DoorsOpen);
                    if(index>=0 && !MapSpriteDrawing.SkipFront(front.Library, Document.SourceFormat) && Assets?.Get(front.Library,index) is Sprite sprite)
                        area = EditorGeometry.Union(area, MapSpriteDrawing.Bounds(sprite,point.X,point.Y,front.Library,index,true,true,MapSpriteDrawing.FrontBlend(front.Frames),Document.SourceFormat));
                }
            }
            var drawing=new SceneGroup();
            using(var context=drawing.Open())
            if(!EditorGeometry.IsEmpty(area))
            {
                context.PushClip(new RectangleGeometry(area));
                var scene=new SceneGroup();
                scene.Children.Add(new SceneRectangle(paper,null,area));
                // Read only the preview's sprite area, in drawing order. A
                // local hover must not enumerate/sort every cell on the map.
                var cells=new List<(int X,int Y,Cell Cell)>();
                int x0=Math.Max(0,(int)Math.Ceiling(area.Left/48)-35),x1=Math.Min(Document.Width-1,(int)Math.Floor(area.Right/48)+35);
                int y0=Math.Max(0,(int)Math.Ceiling(area.Top/32)-2),y1=Math.Min(Document.Height-1,(int)Math.Floor(area.Bottom/32)+35);
                for (int y=y0;y<=y1;y++)
                for (int x=x0;x<=x1;x++)
                {
                    var cell=plan.Cells.GetValueOrDefault((x,y)) ?? Document.Get(x,y);
                    if (!cell.Empty) cells.Add((x,y,cell));
                }
                for(int layer=0;layer<3;layer++)
                {
                    if(!VisibleLayers[layer])continue;
                    foreach(var cell in cells)
                    {
                        if(layer==0 && cell.X%2==0 && cell.Y%2==0)
                            Paint(cell.X,cell.Y,cell.Cell.BackLibrary,(cell.Cell.BackImage&0x1fffffff)-1,false,false);
                        if(layer==1)
                        {
                            int frames=MapSpriteDrawing.MiddleFrames(cell.Cell.MiddleFrames);
                            Paint(cell.X,cell.Y,cell.Cell.MiddleLibrary,cell.Cell.MiddleImage-1+
                                (frames>0?animationFrame/(1+cell.Cell.MiddleTick)%frames:0),true,false,MapSpriteDrawing.MiddleBlend(cell.Cell.MiddleFrames));
                        }
                        if(layer==2)foreach(var front in cell.Cell.ForegroundImages)
                        {
                            int index=MapPreview.FrontImage(cell.Cell,front,DoorsOpen),frames=front.Frames&0x7f;
                            if(index>=0)Paint(cell.X,cell.Y,front.Library,index+
                                (frames>0?animationFrame/(1+front.Tick)%frames:0),true,true,MapSpriteDrawing.FrontBlend(front.Frames));
                        }
                    }
                }
                context.DrawDrawing(scene);context.Pop();
                void Paint(int x,int y,int library,int index,bool anchored,bool front,bool blend=false)
                {
                    if(index<0 || MapSpriteDrawing.SkipFront(library, Document.SourceFormat) || Assets?.Get(library,index) is not Sprite sprite)return;
                    var bounds=MapSpriteDrawing.Bounds(sprite,x,y,library,index,anchored,front,blend,Document.SourceFormat);
                    if(bounds.Intersects(area))MapSpriteDrawing.Append(scene,sprite,bounds,blend,detailEffects);
                }
            }
            wallResultPreview=drawing;
            previewedWallPlan=plan;previewedWallAssets=Assets;
            previewedWallLayers=RenderLayerSettings;previewedWallDoors=DoorsOpen;
            previewedWallAssetRevision=detailAssetRevision;previewedWallAnimation=animationFrame;previewedWallPaper=paper;
        }
        using var dc=wallPreviewScene.RenderOpen();
        dc.PushTransform(new SceneTranslation(origin.X,origin.Y));dc.PushTransform(new SceneScale(Zoom,Zoom));
        if(wallResultPreview!=null)dc.DrawDrawing(wallResultPreview);
        dc.Pop();dc.Pop();
    }
}
