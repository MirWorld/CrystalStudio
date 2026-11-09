namespace CrystalStudio;

internal sealed class MapCreationSession(MapCreationSettings settings, IReadOnlyDictionary<string, string>? parameters = null)
{
    public MapCreationSettings Settings { get; private set; } = settings;
    public Dictionary<string, string> Parameters { get; private set; } = parameters == null ? [] : new(parameters);
    public string PresetName { get; private set; } = "";
    public int GenerationRevision { get; private set; }
    public QuickMapCandidates Candidates { get; } = new();
    public MapDesignSettings Design { get; set; } = new();
    public bool BatchRunning { get; set; }
    public IReadOnlyDictionary<string, string>? ExpectedRecipeParameters { get; set; }
    internal MapDocument? LockedBaseline { get; set; }
    internal MapDecorationVariant? DecorationVariant { get; set; }
    internal bool StructureLocked => LockedBaseline != null;
    internal bool VariationBusy { get; set; }
    internal int? RequestedDecorationSeed { get; set; }
    internal List<string>? DecorationNames { get; set; }
    internal bool RequestedLock { get; set; }
    internal MapDocument? Output => DecorationVariant?.Generated ?? LockedBaseline;
    internal event Action? OutputChanged;
    internal void NotifyOutput() => OutputChanged?.Invoke();
    internal QuickMapDraftStore? DraftStore { get; set; }
    internal bool DraftSaved { get; set; }
    internal Func<MapCreationRecipe>? RecipeFactory { get; set; }
    internal int? SelectedCandidateIndex { get; set; }
    internal int? CompareAIndex { get; set; }
    internal int? CompareBIndex { get; set; }
    internal event Action? DraftChanged;
    internal void NotifyDraft() => DraftChanged?.Invoke();

    public void RememberParameters(IReadOnlyDictionary<string, string> values) => Parameters = new(values);

    public bool Update(MapCreationSettings next, IReadOnlyDictionary<string, string>? parameters, string presetName)
    {
        var values = parameters == null ? new Dictionary<string, string>() : new(parameters);
        bool changed = next.Kind != Settings.Kind || next.Width != Settings.Width || next.Height != Settings.Height ||
            values.Count != Parameters.Count || values.Any(pair => !Parameters.TryGetValue(pair.Key, out string? value) || value != pair.Value);
        Settings = next; Parameters = values; PresetName = presetName;
        if (changed) { GenerationRevision++; LockedBaseline = null; DecorationVariant = null; }
        return changed;
    }
}
