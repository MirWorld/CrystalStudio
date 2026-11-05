using System.IO;
using System.Security.Cryptography;

namespace CrystalStudio;

internal sealed record MapCreationPreset(MapCreationSettings Settings, Dictionary<string, string> Parameters);
internal sealed class MapCreationProfile
{
    public int Version { get; set; } = 1;
    public MapCreationSettings? Last { get; set; }
    public Dictionary<string, MapCreationPreset> Presets { get; set; } = [];
    public Dictionary<string, Dictionary<string, string>> Generators { get; set; } = [];
}

internal sealed record MapCreationRecipe(int Version, DateTime CreatedAtUtc, MapCreationKind Kind, int Width, int Height,
    AssetSourceFormat AssetFormat, string? LibraryFolder, string GeneratorVersion,
    Dictionary<string, string> Parameters, Dictionary<string, string> CatalogueHashes)
{
    public Dictionary<string, string> LibraryVersions { get; init; } = [];
}

internal sealed class MapCreationStore
{
    public string FilePath { get; }
    public MapCreationProfile Data { get; private set; } = new();
    public MapCreationStore(string? filePath = null) => FilePath = filePath ?? Path.Combine(EditorPaths.ProfileDirectory, "map-creation.json");

    public void Load()
    {
        if (!File.Exists(FilePath)) return;
        if (new FileInfo(FilePath).Length > 1_048_576) throw new InvalidDataException("Map creation preferences are too large.");
        var loaded = StudioJson.Deserialize<MapCreationProfile>(File.ReadAllText(FilePath));
        if (loaded == null || loaded.Version != 1 || loaded.Presets == null || loaded.Generators == null ||
            loaded.Last != null && !ValidSettings(loaded.Last) || loaded.Presets.Count > 50 ||
            loaded.Presets.Any(pair => string.IsNullOrWhiteSpace(pair.Key) || pair.Value == null || !ValidSettings(pair.Value.Settings) || pair.Value.Parameters == null) ||
            loaded.Generators.Any(pair => pair.Value == null))
            throw new InvalidDataException("Unsupported map creation preferences.");
        Data = loaded;
    }

    private static bool ValidSettings(MapCreationSettings? settings) => settings != null && Enum.IsDefined(settings.Kind) &&
        !string.IsNullOrWhiteSpace(settings.Name) && MapDimensions.CreationError(settings.Width, settings.Height, settings.Kind) == null;

    public void Save() => AtomicFile.WriteJson(FilePath, Data);
    public Dictionary<string, string> Parameters(MapCreationKind kind) => Data.Generators.TryGetValue(kind.ToString(), out var values) && values != null ? new(values) : [];
    public void SetParameters(MapCreationKind kind, IReadOnlyDictionary<string, string> values) => Data.Generators[kind.ToString()] = new(values);
    public void SavePreset(string name, MapCreationSettings settings, IReadOnlyDictionary<string, string>? parameters = null)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Trim().Length > 80) throw new ArgumentException("Enter a preset name (1–80 characters).");
        name = name.Trim();
        if (Data.Presets.Count >= 50 && !Data.Presets.ContainsKey(name)) throw new InvalidOperationException("At most 50 map presets can be saved.");
        Data.Presets[name] = new(settings, parameters == null ? Parameters(settings.Kind) : new(parameters)); Save();
    }

    public static MapCreationRecipe Recipe(MapDocument document, MapCreationKind kind, IReadOnlyDictionary<string, string> parameters, LibrarySettings settings, AssetStore? assets = null)
    {
        var hashes = new Dictionary<string, string>();
        foreach (string name in new[] { "terrain.json", "walls.json", "palette.json" })
        {
            string path = DataPaths.ResolveEditor("catalogue", name);
            if (File.Exists(path)) { using var stream = File.OpenRead(path); hashes[name] = Convert.ToHexString(SHA256.HashData(stream)); }
        }
        var libraries = new Dictionary<string, string>();
        foreach (var pair in assets?.Libraries ?? [])
        {
            var file = new FileInfo(pair.Value.Path);
            libraries[pair.Key.ToString(System.Globalization.CultureInfo.InvariantCulture)] = file.FullName + "|" + file.Length + "|" + file.LastWriteTimeUtc.Ticks;
        }
        return new(1, DateTime.UtcNow, kind, document.Width, document.Height, document.SourceFormat,
            settings.LibraryFolder, "CrystalStudio/" + typeof(MainWindow).Assembly.GetName().Version + "/quick-map-v1", new(parameters), hashes) { LibraryVersions = libraries };
    }

    public static void SaveRecipe(string mapPath, MapCreationRecipe recipe) => AtomicFile.WriteJson(mapPath + ".creation.json", recipe);
    public static MapCreationRecipe? LoadRecipe(string mapPath, MapDocument document)
    {
        string path = mapPath + ".creation.json";
        if (!File.Exists(path)) return null;
        if (new FileInfo(path).Length > 1_048_576) throw new InvalidDataException("Map recipe is too large.");
        var recipe = StudioJson.Deserialize<MapCreationRecipe>(File.ReadAllText(path));
        return recipe is { Version: 1 } && recipe.Width == document.Width && recipe.Height == document.Height ? recipe : null;
    }
}
