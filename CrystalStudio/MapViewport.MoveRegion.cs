namespace CrystalStudio;

public sealed partial class MapViewport
{
    private Rect? movingSelection;
    private RegionCopy? movingPreview;
    private (int X, int Y) moveStart, moveOffset;

    private bool BeginSelectionMove(int x, int y)
    {
        if (!AllowEdit()) return false;
        movingSelection = Document.AlignSelection(Selection!.Value);
        Selection = movingSelection;
        var area = movingSelection.Value;
        movingPreview = new(Document, (int)area.X, (int)area.Y, (int)area.Width, (int)area.Height, true, true, true, true);
        moveStart = (x, y); moveOffset = default;
        return true;
    }

    private void UpdateSelectionMove(int x, int y)
    {
        moveOffset = ((x - moveStart.X) / 2 * 2, (y - moveStart.Y) / 2 * 2);
    }

    private void FinishSelectionMove()
    {
        if (movingSelection is not Rect area) return;
        if (AllowEdit() && Document.MoveRegion(area, moveOffset.X, moveOffset.Y, History, InvalidateCell))
        {
            Selection = new Rect(area.X + moveOffset.X, area.Y + moveOffset.Y, area.Width, area.Height);
            brushPreviewKey = null; chunks.Clear(); frontRows.Clear();
        }
        CancelSelectionMove();
    }

    private void CancelSelectionMove() { movingSelection = null; movingPreview = null; moveOffset = default; }

    private void DrawSelectionMove(SceneContext dc)
    {
        if (movingSelection is not Rect area || movingPreview == null) return;
        int x = (int)area.X + moveOffset.X, y = (int)area.Y + moveOffset.Y;
        bool valid = movingPreview.CanPaste(Document, x, y);
        dc.PushOpacity(.65); movingPreview.DrawPreview(dc, x, y, DrawSprite); dc.Pop();
        dc.DrawRectangle(null, new ScenePen(new ImmutableSolidColorBrush(valid ? Colors.CornflowerBlue : Colors.IndianRed), 2),
            new Rect(x * 48, y * 32, area.Width * 48, area.Height * 32));
    }
}
