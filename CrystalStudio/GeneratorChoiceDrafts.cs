namespace CrystalStudio;

internal sealed class GeneratorChoiceDrafts(string selector)
{
    private readonly Dictionary<int, Dictionary<string, string>> drafts = [];
    private int current = -1;

    public void Switch(EditorWindow window, int index, Action configure)
    {
        if (index < 0 || index == current) return;
        if (current >= 0)
        {
            var values = GeneratorPreferences.Capture(window);
            values.Remove(selector); values.Remove("SeedBox"); values.Remove("DetailToggle"); values.Remove(GenerationAlgorithms.Parameter);
            drafts[current] = values;
        }
        current = index;
        configure();
        if (drafts.TryGetValue(index, out var saved)) GeneratorPreferences.Restore(window, saved);
    }
}
