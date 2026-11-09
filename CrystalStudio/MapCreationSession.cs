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

    public void RememberParameters(IReadOnlyDictionary<string, string> values) => Parameters = new(values);

    public bool Update(MapCreationSettings next, IReadOnlyDictionary<string, string>? parameters, string presetName)
    {
        var values = parameters == null ? new Dictionary<string, string>() : new(parameters);
        bool changed = next.Kind != Settings.Kind || next.Width != Settings.Width || next.Height != Settings.Height ||
            values.Count != Parameters.Count || values.Any(pair => !Parameters.TryGetValue(pair.Key, out string? value) || value != pair.Value);
        Settings = next; Parameters = values; PresetName = presetName;
        if (changed) GenerationRevision++;
        return changed;
    }
}
