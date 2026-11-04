using System.Globalization;
using System.Text.Json;
using Avalonia.Data;
using Avalonia.Data.Converters;
using Avalonia.Markup.Xaml;

namespace CrystalStudio;

/// <summary>UI language is loaded once at startup. Preferences apply on the next launch.</summary>
public static class LocalText
{
    private sealed record Translation(string English, string Chinese);
    private static readonly Dictionary<string, Translation> resources = LoadResources();
    private static readonly Dictionary<string, Translation> byEnglish = resources.Values
        .GroupBy(value => value.English, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
    public static string Language { get; private set; } = "en-US";
    internal static IReadOnlyDictionary<string, (string English, string Chinese)> Resources => resources
        .ToDictionary(pair => pair.Key, pair => (pair.Value.English, pair.Value.Chinese));

    private static Dictionary<string, Translation> LoadResources()
    {
        using var stream = typeof(LocalText).Assembly.GetManifestResourceStream("CrystalStudio.Localization.Strings.json")
            ?? throw new InvalidOperationException("Missing UI language resources.");
        return JsonSerializer.Deserialize<Dictionary<string, Translation>>(stream)
            ?? throw new InvalidOperationException("Empty UI language resources.");
    }

    public static string DefaultLanguage => CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "zh" ? "zh-CN" : "en-US";
    public static string Normalize(string? language) => language?.StartsWith("zh", StringComparison.OrdinalIgnoreCase) == true ? "zh-CN" : "en-US";
    public static string PreferredLanguage(SettingsStore? store = null)
    {
        try
        {
            string? saved = (store ?? new SettingsStore()).Load().Language;
            return saved == null ? DefaultLanguage : Normalize(saved);
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException or JsonException)
        { return DefaultLanguage; }
    }

    public static void Initialize(SettingsStore? store = null) => Language = PreferredLanguage(store);

    public static void SaveLanguage(string language, SettingsStore? store = null)
    {
        if (language is not ("zh-CN" or "en-US")) throw new ArgumentException("Unsupported UI language.", nameof(language));
        var settings = store ?? new SettingsStore();
        settings.Save(settings.Load() with { Language = language });
    }

    public static string T(string source) => Language == "zh-CN" && byEnglish.TryGetValue(source, out var entry) ? entry.Chinese : source;
    public static string Source(string key) => resources.TryGetValue(key, out var entry) ? entry.English : throw new KeyNotFoundException("Missing UI resource: " + key);
    public static string Format(string source, params object?[] args)
    {
        for (int i = 0; i < args.Length; i++)
            if (args[i] is Enum option) args[i] = T(option.ToString());
        return string.Format(CultureInfo.GetCultureInfo(Language), T(source), args);
    }

    public static string Placement(StampPlacementResult result)
    {
        if (result.Allowed) return T("Stamp can be placed");
        var details = new List<string>();
        if (result.Error != null) details.Add(T(result.Error));
        foreach (var group in result.BlockedCells.GroupBy(cell => cell.Reason))
        {
            string label = group.Key switch
            {
                StampBlockReason.OccupiedObject => "Occupied objects (red)",
                StampBlockReason.OccupiedCollision => "Existing collisions (red)",
                _ => "Outside map boundaries (amber)"
            };
            int count = group.Count();
            string coordinates = string.Join(", ", group.Take(3).Select(cell => $"({cell.X}, {cell.Y})"));
            details.Add(Format("{0}: {1:N0} {2} · {3}", T(label), count, T(count == 1 ? "cell" : "cells"), coordinates)
                + (count > 3 ? Format(", +{0:N0} more", count - 3) : ""));
        }
        return T("Placement blocked · ") + string.Join(" · ", details);
    }

    /// <summary>Translate option labels while selection and saved values retain their original identifiers.</summary>
    public static IDataTemplate ChoiceTemplate { get; } = new FuncDataTemplate<object>((value, _) => new TextBlock { Text = T(value?.ToString() ?? "") });
}

public sealed class LocExtension(string key) : MarkupExtension
{
    public string Key { get; set; } = key;
    public override object ProvideValue(IServiceProvider serviceProvider) => LocalText.T(LocalText.Source(Key));
}

/// <summary>Translate built-in category and option values when their data context is assigned.</summary>
public sealed class LocValueExtension : MarkupExtension
{
    public LocValueExtension() { }
    public LocValueExtension(string path) => Path = path;
    public string Path { get; set; } = ".";
    private static readonly IValueConverter converter = new FuncValueConverter<object?, string>(value => LocalText.T(value?.ToString() ?? ""));
    public override object ProvideValue(IServiceProvider serviceProvider) => new Binding(Path) { Converter = converter };
}
