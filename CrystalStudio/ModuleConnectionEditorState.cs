namespace CrystalStudio;

internal sealed record ModuleConnectionEntry(ModuleConnection Point, bool Enabled = true);

internal sealed class ModuleConnectionEditorState
{
    private sealed record Snapshot(ModuleConnectionEntry[] Entries, string? Selected);
    private readonly Stack<Snapshot> undo = [], redo = [];
    private readonly Dictionary<(long, long), Cell> lookup = [];
    internal IReadOnlyList<StampCell> Cells { get; }
    internal ModuleFootprint? Footprint { get; }
    private readonly bool mapGroundGrid;
    internal ModuleConnectionRules.Bounds Bounds { get; }
    internal List<ModuleConnectionEntry> Entries { get; } = [];
    internal string? SelectedId { get; private set; }
    internal ModuleConnection? Draft { get; private set; }
    internal string? DraftTarget { get; private set; }
    internal bool Adding { get; private set; }
    internal ModuleProblem? Problem { get; private set; }
    internal event Action? Changed;
    internal ModuleConnections Connections => new(1, Entries.Where(e => e.Enabled).Select(e => e.Point).ToList()) { Footprint = Footprint, MapGroundGrid = mapGroundGrid };
    internal ModuleConnections PreviewConnections
    {
        get
        {
            var points = Entries.Where(e => e.Enabled && (Draft == null || e.Point.Id != DraftTarget)).Select(e => e.Point).ToList();
            if (Draft != null && (DraftTarget == null || Entries.Any(e => e.Point.Id == DraftTarget && e.Enabled))) points.Add(Draft);
            return new(1, points) { Footprint = Footprint, MapGroundGrid = mapGroundGrid };
        }
    }
    internal ModuleConnection? Selected => Entries.FirstOrDefault(e => e.Point.Id == SelectedId)?.Point;
    internal bool CanUndo => undo.Count > 0;
    internal bool CanRedo => redo.Count > 0;
    internal ModuleConnectionEditorState(IReadOnlyList<StampCell> cells, ModuleConnections? saved)
    {
        if (saved != null && (saved.Version != 1 || saved.Points == null || saved.Points.Any(p => p == null)))
            throw new System.IO.InvalidDataException(LocalText.T("Unsupported connections or more than 64 openings."));
        Cells = cells.Select(c => c with { Cell = c.Cell.Copy() }).ToArray(); Footprint = saved?.Footprint;
        mapGroundGrid = saved?.MapGroundGrid == true;
        foreach (var cell in Cells)
            if (!lookup.TryAdd((cell.X, cell.Y), cell.Cell)) throw new System.IO.InvalidDataException(LocalText.T("Module contains duplicate cells."));
        Bounds = Cells.Any(c => !c.Cell.Empty) ? ModuleConnectionRules.GetBounds(Cells, saved) : new(0, 0, 0, 0);
        Entries.AddRange((saved?.Points ?? []).Select(p => new ModuleConnectionEntry(p)));
        SelectedId = Entries.FirstOrDefault()?.Point.Id;
    }
    internal ModuleProblem? Check(ModuleConnections connections) => ModuleDiagnostics.Check(connections, Cells, lookup, Bounds);
    private Snapshot Capture() => new(Entries.ToArray(), SelectedId);
    private void Record() { undo.Push(Capture()); redo.Clear(); }
    private void Restore(Snapshot snapshot)
    {
        Entries.Clear(); Entries.AddRange(snapshot.Entries); SelectedId = snapshot.Selected;
        ClearDraft(); Changed?.Invoke();
    }
    private void ClearDraft() { Draft = null; DraftTarget = null; Problem = null; Adding = false; }
    internal bool Select(string? id)
    {
        if (!Commit()) return false;
        SelectedId = id; Adding = false; Changed?.Invoke(); return true;
    }
    internal bool BeginNew()
    {
        if (!Commit()) return false;
        SelectedId = null; Adding = true; Changed?.Invoke(); return true;
    }
    internal string NextId()
    {
        int n = 1; while (Entries.Any(e => e.Point.Id.Equals("entry" + n, StringComparison.OrdinalIgnoreCase))) n++;
        return "entry" + n;
    }
    internal void Preview(ModuleConnection? point, string? target, string? inputError = null)
    {
        Draft = point; DraftTarget = target;
        if (inputError != null) Problem = new("input", inputError, ConnectionId: target);
        else if (point != null)
        {
            // Disabled points may overlap active proposals, but must be valid when enabled.
            bool disabled = Entries.Any(e => e.Point.Id == target && !e.Enabled);
            var candidates = Entries.Where(e => !disabled && e.Enabled && e.Point.Id != target).Select(e => e.Point).Append(point).ToList();
            Problem = Check(new(1, candidates) { Footprint = Footprint });
            if (Entries.Any(e => e.Point.Id != target && e.Point.Id.Equals(point.Id, StringComparison.OrdinalIgnoreCase)))
                Problem = new("duplicate", LocalText.T("Connection IDs must be unique."), ConnectionId: point.Id, X: point.X, Y: point.Y);
        }
        else Problem = null;
        Changed?.Invoke();
    }
    internal bool Commit()
    {
        if (Problem != null) return false;
        if (Draft is not { } point) return true;
        int index = Entries.FindIndex(e => e.Point.Id == DraftTarget);
        if (index < 0 || Entries[index].Point != point)
        {
            Record();
            if (index < 0) Entries.Add(new(point)); else Entries[index] = Entries[index] with { Point = point };
        }
        SelectedId = point.Id; ClearDraft(); Changed?.Invoke(); return true;
    }
    internal void CancelDraft() { ClearDraft(); Changed?.Invoke(); }
    internal bool Toggle(string id, bool enabled)
    {
        if (!Commit()) return false;
        int index = Entries.FindIndex(e => e.Point.Id == id); if (index < 0 || Entries[index].Enabled == enabled) return true;
        Record(); Entries[index] = Entries[index] with { Enabled = enabled }; Changed?.Invoke(); return true;
    }
    internal bool Remove()
    {
        if (SelectedId == null) { CancelDraft(); return false; }
        Record(); Entries.RemoveAll(e => e.Point.Id == SelectedId); SelectedId = null; ClearDraft(); Changed?.Invoke(); return true;
    }
    internal void Undo()
    {
        if (Draft != null || Problem != null || Adding) { CancelDraft(); return; }
        if (undo.TryPop(out var snapshot)) { redo.Push(Capture()); Restore(snapshot); }
    }
    internal void Redo() { if (redo.TryPop(out var snapshot)) { undo.Push(Capture()); Restore(snapshot); } }
}
