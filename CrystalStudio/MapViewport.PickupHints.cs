namespace CrystalStudio;

public sealed partial class MapViewport
{
    private List<(Guid Id,Point Marker)> pickupHintEntries=[];
    private long pickupHintRevision=-1;
    private SceneGroup? pickupHints;
    private RasterImage? pickupMarker;
    private Brush? pickupMarkerAccent;
    private (long Revision,double Zoom,Point Origin,Size Size,Brush Accent,Guid? Picked)? pickupHintsKey;
    private void ResetPickupHints(){pickupHints=null;pickupHintsKey=null;pickupHintEntries.Clear();pickupHintRevision=-1;}

    private void DrawPickupHints(SceneContext dc)
    {
        if(pickupHintRevision!=Document.PickupRevision)
        {
            pickupHintEntries.Clear();
            foreach(var stamp in Document.PlacedStamps)
            {
                if(stamp.Wall!=null||!Document.IsStampAvailable(stamp))continue;
                int minX=int.MaxValue,minY=int.MaxValue,maxX=int.MinValue,maxY=int.MinValue;
                foreach(var cell in stamp.Cells){minX=Math.Min(minX,cell.X);minY=Math.Min(minY,cell.Y);maxX=Math.Max(maxX,cell.X);maxY=Math.Max(maxY,cell.Y);}
                double centreX=(minX+maxX)/2.0,centreY=(minY+maxY)/2.0;
                // Keep the marker on an actual pickable cell, preferring cells with an object image.
                var marker=stamp.Cells.MinBy(c=>(c.Cell.HasForeground?0:1_000_000)+Math.Pow(c.X-centreX,2)+Math.Pow(c.Y-centreY,2))!;
                pickupHintEntries.Add((stamp.Id,new((stamp.X+marker.X+0.5)*48,(stamp.Y+marker.Y+0.5)*32)));
            }
            pickupHintRevision=Document.PickupRevision;pickupHintsKey=null;
        }
        var accent=ThemeService.Brush("Accent");
        if(pickupMarker==null||!ReferenceEquals(pickupMarkerAccent,accent))
        {
            var color=(accent as ImmutableSolidColorBrush)?.Color??Colors.DodgerBlue;
            var visual=new SceneVisual();
            using(var marker=visual.RenderOpen())marker.DrawEllipse(new ImmutableSolidColorBrush(Color.FromArgb(191,color.R,color.G,color.B)),
                new ScenePen(new ImmutableSolidColorBrush(Color.FromArgb(191,0,0,0)),1),new(4,4),2.5,2.5);
            var bitmap=new RasterTarget(8,8,96,96,RasterFormats.Pbgra32);bitmap.Render(visual);
            pickupMarker=bitmap;pickupMarkerAccent=accent;
        }
        var key=(Document.PickupRevision,Zoom,origin,new Size(ActualWidth,ActualHeight),accent,PickedStampId);
        if(pickupHintsKey!=key)
        {
            var visible=new Rect(-origin.X/Zoom,-origin.Y/Zoom,ActualWidth/Zoom,ActualHeight/Zoom);
            visible = visible.Inflate(new Thickness(4/Zoom,4/Zoom));
            bool detailed=Zoom>=0.25;
            var buckets=new HashSet<(int,int)>();
            var drawing=new SceneGroup();
            using(var hints=drawing.Open())
            {
                foreach(var hint in pickupHintEntries)
                {
                    if(hint.Id==PickedStampId||!visible.Contains(hint.Marker))continue;
                    var screen=new Point(origin.X+hint.Marker.X*Zoom,origin.Y+hint.Marker.Y*Zoom);
                    // At overview scale, nearby markers share one dot instead of forming a solid blanket.
                    if(!detailed&&!buckets.Add(((int)Math.Floor(screen.X/12),(int)Math.Floor(screen.Y/12))))continue;
                    hints.DrawImage(pickupMarker,new(hint.Marker.X-4/Zoom,hint.Marker.Y-4/Zoom,8/Zoom,8/Zoom));
                }
            }
            pickupHints=drawing;pickupHintsKey=key;
        }
        dc.DrawDrawing(pickupHints);
    }

}
