namespace CrystalStudio;

internal enum MapDesignTool { Pan, Select, Point, ClearObjects, Protect, Walkable }

internal sealed class MapDesignWorkspace
{
    private readonly MapDesignEditor editor;
    private readonly MapDesignSettings design;
    private readonly int width, height;
    private readonly List<MapDesignSettings> undo = [];
    private readonly List<MapDesignSettings> redo = [];
    private MapDesignSettings previous;
    private bool restoring;
    internal int PointIndex { get; private set; } = -1;
    internal int RegionIndex { get; private set; } = -1;
    internal MapDesignTool Tool { get; set; }
    internal bool CanUndo => undo.Count > 0;
    internal bool CanRedo => redo.Count > 0;
    internal event Action? Changed;

    internal MapDesignWorkspace(MapDesignEditor editor, MapDesignSettings design, int width, int height)
    {
        this.editor = editor; this.design = design; this.width = width; this.height = height; previous = design.Copy();
        editor.Changed += () =>
        {
            if (!restoring && editor.InputValid && !previous.SameAs(design))
            {
                undo.Add(previous); if (undo.Count > 100) undo.RemoveAt(0);
                redo.Clear(); previous = design.Copy();
            }
            Changed?.Invoke();
        };
        editor.SelectionChanged += (point, region) => { PointIndex = point; RegionIndex = region; Changed?.Invoke(); };
    }

    internal void Select(int point, int region)
    {
        PointIndex = point; RegionIndex = region; editor.Select(point, region); Changed?.Invoke();
    }

    internal void Apply(Action<MapDesignSettings> change)
    {
        var next = design.Copy(); change(next); next.Validate(width, height); editor.Restore(next);
    }

    internal void AddPoint(int column, int row)
    {
        Apply(next => next.Points.Add(new(LocalText.Format("Point {0}", next.Points.Count + 1), column, row)));
        Select(design.Points.Count - 1, -1);
    }

    internal void AddRegion(Rect area, MapRegionPurpose purpose)
    {
        Apply(next => next.Regions.Add(new(LocalText.Format("Region {0}", next.Regions.Count + 1),
            (int)area.X, (int)area.Y, (int)area.Width, (int)area.Height, purpose)));
        Select(-1, design.Regions.Count - 1);
    }

    internal void MoveSelected(int deltaX, int deltaY)
    {
        if (PointIndex >= 0 && PointIndex < design.Points.Count)
            Apply(next => { var point = next.Points[PointIndex]; next.Points[PointIndex] = point with
                { X = Math.Clamp(point.X + deltaX, 0, Math.Max(0, width - design.FootprintSize)), Y = Math.Clamp(point.Y + deltaY, 0, Math.Max(0, height - design.FootprintSize)) }; });
        else if (RegionIndex >= 0 && RegionIndex < design.Regions.Count)
            Apply(next => { var region = next.Regions[RegionIndex]; next.Regions[RegionIndex] = region with
                { X = Math.Clamp(region.X + deltaX, 0, width - region.Width), Y = Math.Clamp(region.Y + deltaY, 0, height - region.Height) }; });
    }

    internal void ResizeSelected(int right, int bottom)
    {
        if (RegionIndex < 0 || RegionIndex >= design.Regions.Count) return;
        Apply(next => { var region = next.Regions[RegionIndex]; next.Regions[RegionIndex] = region with
            { Width = Math.Clamp(right - region.X + 1, 1, width - region.X), Height = Math.Clamp(bottom - region.Y + 1, 1, height - region.Y) }; });
    }

    internal void DeleteSelected()
    {
        if (PointIndex >= 0 && PointIndex < design.Points.Count) Apply(next => next.Points.RemoveAt(PointIndex));
        else if (RegionIndex >= 0 && RegionIndex < design.Regions.Count) Apply(next => next.Regions.RemoveAt(RegionIndex));
        Select(-1, -1);
    }

    internal void Undo() => Restore(undo, redo);
    internal void Redo() => Restore(redo, undo);
    private void Restore(List<MapDesignSettings> source, List<MapDesignSettings> destination)
    {
        if (source.Count == 0) return;
        var target = source[^1]; destination.Add(design.Copy()); source.RemoveAt(source.Count - 1);
        restoring = true;
        try { editor.Restore(target); previous = design.Copy(); Select(-1, -1); }
        finally { restoring = false; }
        Changed?.Invoke();
    }
}
