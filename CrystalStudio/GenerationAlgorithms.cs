using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using Avalonia.LogicalTree;

namespace CrystalStudio;

internal static class GenerationAlgorithms
{
    internal const int Legacy = 1;
    internal const int Current = 2;
    internal const string Parameter = "AlgorithmVersion";
    private sealed class State(int version, Action changed)
    {
        internal int Version = version;
        internal MapDesignSettings? Design;
        internal ComboBox? Choice;
        internal readonly Action Changed = changed;
    }
    private static readonly ConditionalWeakTable<EditorWindow, State> states = new();

    internal static void Validate(int version)
    {
        if (version is not (Legacy or Current)) throw new InvalidDataException("Unsupported map generation algorithm version.");
    }

    internal static int Read(IReadOnlyDictionary<string, string>? values, int fallback = Legacy)
    {
        if (values == null || !values.TryGetValue(Parameter, out string? text)) return fallback;
        if (!int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out int version))
            throw new InvalidDataException("Invalid map generation algorithm version.");
        Validate(version); return version;
    }

    internal static void Register(EditorWindow window, IReadOnlyDictionary<string, string>? values, Action changed)
    {
        var state = new State(Read(values, values is { Count: > 0 } ? Legacy : Current), changed);
        states.Add(window, state);
        var settings = window.GetLogicalDescendants().OfType<ScrollViewer>().Select(view => view.Content).OfType<StackPanel>()
            .FirstOrDefault(panel => panel.Children.OfType<ComboBox>().Any());
        if (settings == null) return;
        var choice = new ComboBox { Name = "GeneratorAlgorithm", ItemsSource = new[] { Label(Legacy), Label(Current) }, SelectedIndex = state.Version - 1, HorizontalAlignment = HorizontalAlignment.Stretch };
        Avalonia.Automation.AutomationProperties.SetName(choice, LocalText.T("Generation algorithm"));
        choice.SelectionChanged += (_, _) =>
        {
            int version = choice.SelectedIndex + 1;
            if (version is not (Legacy or Current) || state.Version == version) return;
            state.Version = version; state.Changed();
        };
        state.Choice = choice;
        var group = new StackPanel { Spacing = 6, Margin = new Thickness(0, 12, 0, 0) };
        group.Children.Add(new TextBlock { Text = LocalText.T("Generation algorithm") }); group.Children.Add(choice);
        group.Children.Add(MapDesignEditor.Hint("Changing algorithms changes seeded results. Old recipes keep the legacy algorithm."));
        settings.Children.Add(group);
    }

    internal static string Label(int version) => LocalText.T(version == Current ? "Refined v2" : "Legacy v1");

    internal static int Version(EditorWindow window) => states.TryGetValue(window, out var state) ? state.Version : Legacy;
    internal static MapDesignSettings? Design(EditorWindow window) => states.TryGetValue(window, out var state) && state.Version == Current ? state.Design : null;

    internal static void Capture(EditorWindow window, Dictionary<string, string> values)
    {
        if (states.TryGetValue(window, out var state)) values[Parameter] = state.Version.ToString(CultureInfo.InvariantCulture);
    }

    internal static void Restore(EditorWindow window, IReadOnlyDictionary<string, string> values)
    {
        if (!states.TryGetValue(window, out var state) || !values.ContainsKey(Parameter) && !values.ContainsKey("SeedBox")) return;
        int version = Read(values);
        if (state.Version == version) return;
        state.Version = version;
        if (state.Choice != null) state.Choice.SelectedIndex = version - 1;
        state.Changed();
    }

    internal static void SetDesign(EditorWindow window, MapDesignSettings design)
    {
        if (!states.TryGetValue(window, out var state) || state.Design == null && design.Empty || state.Design?.SameAs(design) == true) return;
        state.Design = design.Empty ? null : design.Copy();
        if (state.Version == Current) state.Changed();
    }
}

internal static class GenerationRandom
{
    internal static ulong Bits(int seed, int domain, int x = 0, int y = 0, int lane = 0)
    {
        unchecked
        {
            ulong value = (uint)seed | ((ulong)(uint)domain << 32);
            value = Mix(value ^ ((ulong)(uint)x * 0x9E3779B97F4A7C15UL));
            value = Mix(value ^ ((ulong)(uint)y * 0xD1B54A32D192ED03UL));
            return Mix(value ^ ((ulong)(uint)lane * 0x94D049BB133111EBUL));
        }
    }

    private static ulong Mix(ulong value)
    {
        unchecked
        {
            value ^= value >> 30; value *= 0xBF58476D1CE4E5B9UL;
            value ^= value >> 27; value *= 0x94D049BB133111EBUL;
            return value ^ (value >> 31);
        }
    }

    internal static double Unit(int seed, int domain, int x = 0, int y = 0, int lane = 0) =>
        (Bits(seed, domain, x, y, lane) >> 11) * (1.0 / 9007199254740992.0);
    internal static int Index(int seed, int domain, int x, int y, int count, int lane = 0) =>
        count > 0 ? (int)(Bits(seed, domain, x, y, lane) % (uint)count) : throw new ArgumentOutOfRangeException(nameof(count));
    internal static Random Stream(int seed, int domain) => new(unchecked((int)Bits(seed, domain)));
}
