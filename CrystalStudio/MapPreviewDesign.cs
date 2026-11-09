using System.Globalization;

namespace CrystalStudio;

internal static class MapPreviewProjection
{
    internal static Rect Content(Rect region, Size bitmap, bool padded)
    {
        if (!padded) return new(0, 0, 1, 1);
        if (region.Width <= 0 || region.Height <= 0 || bitmap.Width <= 8 || bitmap.Height <= 8) return default;
        double scale = Math.Min((bitmap.Width - 8) / (region.Width * 48), (bitmap.Height - 8) / (region.Height * 32));
        double width = region.Width * 48 * scale / bitmap.Width, height = region.Height * 32 * scale / bitmap.Height;
        return new((1 - width) / 2, (1 - height) / 2, width, height);
    }

    internal static Point Project(Point cell, Rect region, Size bitmap, bool padded)
    {
        var content = Content(region, bitmap, padded);
        return new(content.X + (cell.X - region.X) / region.Width * content.Width,
            content.Y + (cell.Y - region.Y) / region.Height * content.Height);
    }
}

internal sealed class MapPreviewDesign
{
    private readonly MapDesignSettings design;
    private readonly MapDesignWorkspace workspace;
    private readonly Func<(Rect Region, bool Padded)> frame;
    private readonly Func<bool> available;
    private readonly int width, height;
    private Point? start, current;
    private bool resizing;
    internal MapConstraintFinding? Highlight { get; set; }
    internal event Action<string>? Feedback;
    internal MapPreviewDesign(MapDesignSettings design, MapDesignWorkspace workspace, int width, int height,
        Func<(Rect Region, bool Padded)> frame, Func<bool> available)
    { this.design = design; this.workspace = workspace; this.width = width; this.height = height; this.frame = frame; this.available = available; }

    private bool Cell(Point normalized, Size bitmap, out Point cell)
    {
        var view = frame();
        if (!MapPreviewRegion.TrySelect(normalized, view.Region, bitmap, view.Padded, out var selected)) { cell = default; return false; }
        cell = new(Math.Clamp((int)selected.X, 0, width - 1), Math.Clamp((int)selected.Y, 0, height - 1)); return true;
    }

    internal bool Press(Point normalized, Size bitmap)
    {
        if (workspace.Tool == MapDesignTool.Pan) return false;
        if (!available()) { Feedback?.Invoke(LocalText.T("Wait for the current preview before editing markers.")); return true; }
        if (!Cell(normalized, bitmap, out var cell)) return true;
        start = current = cell; resizing = false;
        if (workspace.Tool != MapDesignTool.Select) return true;
        int pointIndex = design.Points.FindLastIndex(point => Math.Abs(point.X - cell.X) <= Math.Max(1, design.FootprintSize - 1) && Math.Abs(point.Y - cell.Y) <= Math.Max(1, design.FootprintSize - 1));
        int regionIndex = pointIndex >= 0 ? -1 : design.Regions.FindLastIndex(region => region.Contains((int)cell.X, (int)cell.Y));
        workspace.Select(pointIndex, regionIndex);
        if (regionIndex >= 0)
        {
            var region = design.Regions[regionIndex];
            resizing = Math.Abs(region.X + region.Width - 1 - cell.X) <= 1 && Math.Abs(region.Y + region.Height - 1 - cell.Y) <= 1;
        }
        return true;
    }

    internal void Move(Point normalized, Size bitmap) { if (start != null && Cell(normalized, bitmap, out var cell)) current = cell; }
    internal void Release(Point normalized, Size bitmap)
    {
        if (start is not { } origin) return;
        var target = Cell(normalized, bitmap, out var cell) ? cell : current ?? origin;
        Cancel();
        if (!available()) return;
        try
        {
            switch (workspace.Tool)
            {
                case MapDesignTool.Point: workspace.AddPoint((int)target.X, (int)target.Y); break;
                case MapDesignTool.Select:
                    if (resizing) workspace.ResizeSelected((int)target.X, (int)target.Y);
                    else workspace.MoveSelected((int)(target.X - origin.X), (int)(target.Y - origin.Y));
                    break;
                case MapDesignTool.ClearObjects: case MapDesignTool.Protect: case MapDesignTool.Walkable:
                    workspace.AddRegion(Area(origin, target), workspace.Tool switch
                    { MapDesignTool.ClearObjects => MapRegionPurpose.ClearObjects, MapDesignTool.Walkable => MapRegionPurpose.Walkable, _ => MapRegionPurpose.Protect });
                    break;
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.IO.InvalidDataException) { Feedback?.Invoke(exception.Message); }
    }

    internal void Cancel() { start = current = null; }
    internal bool Key(Key key, KeyModifiers modifiers)
    {
        if (key == Avalonia.Input.Key.Escape && start != null) { Cancel(); return true; }
        if (!available()) return false;
        try
        {
            if (modifiers.HasFlag(KeyModifiers.Control) && key == Avalonia.Input.Key.Z) { Cancel(); workspace.Undo(); return true; }
            if (modifiers.HasFlag(KeyModifiers.Control) && key == Avalonia.Input.Key.Y) { Cancel(); workspace.Redo(); return true; }
            if (workspace.Tool != MapDesignTool.Select) return false;
            if (key == Avalonia.Input.Key.Delete) { workspace.DeleteSelected(); return true; }
            int step = modifiers.HasFlag(KeyModifiers.Shift) ? 10 : 1;
            if (key is not (Avalonia.Input.Key.Left or Avalonia.Input.Key.Right or Avalonia.Input.Key.Up or Avalonia.Input.Key.Down)) return false;
            workspace.MoveSelected(key == Avalonia.Input.Key.Left ? -step : key == Avalonia.Input.Key.Right ? step : 0,
                key == Avalonia.Input.Key.Up ? -step : key == Avalonia.Input.Key.Down ? step : 0); return true;
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.IO.InvalidDataException) { Feedback?.Invoke(exception.Message); return true; }
    }

    private static Rect Area(Point first, Point last) => new(Math.Min(first.X, last.X), Math.Min(first.Y, last.Y), Math.Abs(first.X - last.X) + 1, Math.Abs(first.Y - last.Y) + 1);
    internal void Render(DrawingContext context, Rect image, Size bitmap)
    {
        var view = frame(); if (view.Region.Width <= 0 || view.Region.Height <= 0) return;
        Point Position(Point cell)
        {
            var normalized = MapPreviewProjection.Project(cell, view.Region, bitmap, view.Padded);
            return new(image.X + normalized.X * image.Width, image.Y + normalized.Y * image.Height);
        }
        void Box(Rect area, Brush color, bool selected)
        {
            var first = Position(area.TopLeft); var last = Position(area.BottomRight);
            context.DrawRectangle(null, new Pen(color, selected ? 3 : 1.5), new Rect(first, last));
            if (selected) context.DrawRectangle(color, null, new Rect(last - new Vector(4, 4), new Size(8, 8)));
        }
        for (int index = 0; index < design.Regions.Count; index++)
        {
            var region = design.Regions[index];
            Box(new(region.X, region.Y, region.Width, region.Height), region.Purpose switch
                { MapRegionPurpose.Protect => Brushes.Gold, MapRegionPurpose.Walkable => Brushes.LimeGreen, _ => Brushes.DeepSkyBlue }, index == workspace.RegionIndex);
        }
        Point? previous = null;
        for (int index = 0; index < design.Points.Count; index++)
        {
            var point = design.Points[index]; var position = Position(new(point.X + .5, point.Y + .5));
            if (previous is { } from) context.DrawLine(new Pen(Brushes.Orange, 1.5), from, position);
            Box(new(point.X, point.Y, design.FootprintSize, design.FootprintSize), Brushes.Orange, index == workspace.PointIndex);
            var text = new FormattedText((index + 1).ToString(CultureInfo.InvariantCulture) + (point.Role == MapPointRole.None ? "" : " " + LocalText.T(point.Role.ToString())), CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, Typeface.Default, 13, Brushes.White);
            context.DrawRectangle(Brushes.Black, null, new Rect(position, new Size(text.Width + 4, text.Height + 2)));
            context.DrawText(text, position + new Vector(2, 1)); previous = position;
        }
        if (Highlight is { } finding) Box(new(finding.X, finding.Y, design.FootprintSize, design.FootprintSize), Brushes.Red, true);
        if (start is { } moveStart && current is { } moveEnd && workspace.Tool == MapDesignTool.Select)
        {
            if (workspace.PointIndex >= 0 && workspace.PointIndex < design.Points.Count)
            {
                var point = design.Points[workspace.PointIndex];
                Box(new(Math.Clamp(point.X + moveEnd.X - moveStart.X, 0, Math.Max(0, width - design.FootprintSize)), Math.Clamp(point.Y + moveEnd.Y - moveStart.Y, 0, Math.Max(0, height - design.FootprintSize)), design.FootprintSize, design.FootprintSize), Brushes.White, true);
            }
            else if (workspace.RegionIndex >= 0 && workspace.RegionIndex < design.Regions.Count)
            {
                var region = design.Regions[workspace.RegionIndex];
                Box(resizing ? new Rect(region.X, region.Y, Math.Clamp(moveEnd.X - region.X + 1, 1, width - region.X), Math.Clamp(moveEnd.Y - region.Y + 1, 1, height - region.Y)) :
                    new Rect(Math.Clamp(region.X + moveEnd.X - moveStart.X, 0, width - region.Width), Math.Clamp(region.Y + moveEnd.Y - moveStart.Y, 0, height - region.Height), region.Width, region.Height), Brushes.White, true);
            }
        }
        if (start is { } origin && current is { } target && workspace.Tool is not (MapDesignTool.Select or MapDesignTool.Pan))
            Box(workspace.Tool == MapDesignTool.Point ? new(target.X, target.Y, design.FootprintSize, design.FootprintSize) : Area(origin, target), Brushes.White, true);
    }
}
