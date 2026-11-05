namespace CrystalStudio;

public sealed partial class MapViewport
{
    public SpecialBrushKind SpecialBrushKind { get; set; } = SpecialBrushKind.Tree;
    public TreeBrushOptions TreeOptions { get; set; } = new();
    public IReadOnlyList<BrushTree> Trees { get; set; } = [];
    private (MapDocument Document, long Revision, IReadOnlyList<BrushTree> Trees, TreeBrushOptions Options, int Size, int X, int Y)? treePlanKey;
    private IReadOnlyList<TreePlacement> treePlan = [];

    public IReadOnlyList<TreePlacement> PlanTrees(int x, int y)
    {
        var key = (Document, Document.PickupRevision, Trees, TreeOptions, BrushSize, x, y);
        if (treePlanKey != key)
        {
            treePlan = TreeBrush.Plan(Document, Trees, TreeOptions, BrushSize, x, y);
            treePlanKey = key;
        }
        return treePlan;
    }

    private void PaintTrees(int x, int y)
    {
        if (!AllowEdit()) return;
        if (Trees.Count == 0) { Message?.Invoke(LocalText.Format("Choose {0} in {1} settings first",SpecialBrushKind.Plural(),SpecialBrushKind.BrushName())); return; }
        foreach (var placement in PlanTrees(x, y))
            Document.PlaceStamp(placement.Cells, placement.X, placement.Y, placement.Tree.Name, History, InvalidateCell);
    }

    private void DrawTreeBrush(SceneContext dc)
    {
        var area = PaintFootprint(hover.X, hover.Y);
        var bounds = new Rect(area.X * 48, area.Y * 32, area.Width * 48, area.Height * 32);
        if (TreeOptions.Round) dc.DrawEllipse(null, cursor, new(bounds.X + bounds.Width / 2, bounds.Y + bounds.Height / 2), bounds.Width / 2, bounds.Height / 2);
        else dc.DrawRectangle(null, cursor, bounds);
        dc.PushOpacity(.55);
        foreach (var cell in PlanTrees(hover.X, hover.Y).SelectMany(p => p.Cells.Select(c => c with { X = p.X + c.X, Y = p.Y + c.Y }))
            .OrderBy(c => c.Y).ThenBy(c => c.X))
        foreach (var front in cell.Cell.ForegroundImages)
            DrawSprite(dc, cell.X, cell.Y, front.Library, (front.Image & 0x7FFF) - 1, true, true, MapSpriteDrawing.FrontBlend(front.Frames));
        dc.Pop();
    }
}
