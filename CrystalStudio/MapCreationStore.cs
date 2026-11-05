using System.IO;
using System.Security.Cryptography;
using System.Text.Json.Nodes;

namespace CrystalStudio;

internal sealed record MapGenerationSettings(MapCreationKind Kind, int Width, int Height);
internal sealed record MapCreationPreset(MapGenerationSettings Generation, Dictionary<string, string> Parameters);
internal sealed record MapCreationPresetPackage(int Version, string Name, MapCreationPreset Preset);
internal sealed class MapCreationProfile
{
    public int Version { get; set; } = 2;
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
        var json = JsonNode.Parse(File.ReadAllText(FilePath)) ?? throw new InvalidDataException("Unsupported map creation preferences.");
        if (json["Version"]?.GetValue<int>() == 1)
        {
            if (json["Presets"] is JsonObject presets)
                foreach (var pair in presets)
                    if (pair.Value is JsonObject preset && preset["Settings"] is JsonObject legacy)
                    {
                        preset["Generation"] = new JsonObject
                        {
                            ["Kind"] = legacy["Kind"]?.DeepClone(), ["Width"] = legacy["Width"]?.DeepClone(),
                            ["Height"] = legacy["Height"]?.DeepClone()
                        };
                        preset.Remove("Settings");
                    }
            json["Version"] = 2;
        }
        var loaded = StudioJson.Deserialize<MapCreationProfile>(json.ToJsonString());
        if (loaded == null || loaded.Version != 2 || loaded.Presets == null || loaded.Generators == null ||
            loaded.Last != null && !ValidSettings(loaded.Last) || loaded.Presets.Count > 50 ||
            loaded.Presets.Any(pair => !ValidName(pair.Key) || !ValidPreset(pair.Value)) ||
            loaded.Generators.Any(pair => !ValidParameters(pair.Value)))
            throw new InvalidDataException("Unsupported map creation preferences.");
        Data = loaded;
    }

    private static bool ValidSettings(MapCreationSettings? settings) => settings != null && Enum.IsDefined(settings.Kind) &&
        !string.IsNullOrWhiteSpace(settings.Name) && MapDimensions.CreationError(settings.Width, settings.Height, settings.Kind) == null;
    private static bool ValidName(string? name) => !string.IsNullOrWhiteSpace(name) && name == name.Trim() && name.Length <= 80;
    private static bool ValidParameters(IReadOnlyDictionary<string, string>? values) => values is { Count: <= 256 } &&
        values.All(pair => !string.IsNullOrWhiteSpace(pair.Key) && pair.Key.Length <= 128 && pair.Value is { Length: <= 4096 });
    private static bool ValidPreset(MapCreationPreset? preset) => preset?.Generation is { } generation &&
        MapDimensions.CreationError(generation.Width, generation.Height, generation.Kind) == null && ValidParameters(preset.Parameters);

    public void Save() => WriteProfile(Data);

    private void WriteProfile(MapCreationProfile profile)
    {
        byte[] json = StudioJson.SerializeToUtf8Bytes(profile);
        if (json.Length > 1_048_576) throw new InvalidDataException("Map creation preferences are too large.");
        AtomicFile.Write(FilePath, stream => stream.Write(json));
    }
    public Dictionary<string, string> Parameters(MapCreationKind kind) => Data.Generators.TryGetValue(kind.ToString(), out var values) && values != null ? new(values) : [];
    public void SetParameters(MapCreationKind kind, IReadOnlyDictionary<string, string> values) => Data.Generators[kind.ToString()] = new(values);
    public void SavePreset(string name, MapCreationSettings settings, IReadOnlyDictionary<string, string>? parameters = null)
    {
        PutPreset(name.Trim(), new(new(settings.Kind, settings.Width, settings.Height), parameters == null ? Parameters(settings.Kind) : new(parameters)));
    }

    private void PutPreset(string name, MapCreationPreset preset)
    {
        if (!ValidName(name)) throw new ArgumentException("Enter a preset name (1–80 characters).");
        if (!ValidPreset(preset)) throw new ArgumentException("Invalid map preset.");
        var presets = new Dictionary<string, MapCreationPreset>(Data.Presets) { [name] = preset };
        CommitPresets(presets);
    }

    private void CommitPresets(Dictionary<string, MapCreationPreset> presets)
    {
        if (presets.Count > 50) throw new InvalidOperationException("At most 50 map presets can be saved.");
        var next = new MapCreationProfile { Last = Data.Last, Generators = Data.Generators, Presets = presets };
        WriteProfile(next); Data = next;
    }

    public void RenamePreset(string name, string newName)
    {
        newName = newName.Trim();
        if (!ValidName(newName)) throw new ArgumentException("Enter a preset name (1–80 characters).");
        if (name == newName) return;
        if (Data.Presets.ContainsKey(newName)) throw new InvalidOperationException("A preset with this name already exists.");
        var presets = new Dictionary<string, MapCreationPreset>(Data.Presets);
        var preset = presets[name]; presets.Remove(name); presets.Add(newName, preset); CommitPresets(presets);
    }

    public void DuplicatePreset(string name, string newName)
    {
        newName = newName.Trim();
        if (Data.Presets.ContainsKey(newName)) throw new InvalidOperationException("A preset with this name already exists.");
        var preset = Data.Presets[name]; PutPreset(newName, preset with { Parameters = new(preset.Parameters) });
    }

    public bool DeletePreset(string name)
    {
        var presets = new Dictionary<string, MapCreationPreset>(Data.Presets);
        if (!presets.Remove(name)) return false;
        CommitPresets(presets); return true;
    }

    public void ExportPreset(string name, string path) => AtomicFile.WriteJson(path, new MapCreationPresetPackage(1, name, Data.Presets[name]));

    public string ImportPreset(string path)
    {
        if (new FileInfo(path).Length > 1_048_576) throw new InvalidDataException("Map preset is too large.");
        var package = StudioJson.Deserialize<MapCreationPresetPackage>(File.ReadAllText(path));
        if (package is not { Version: 1 } || !ValidName(package.Name) || !ValidPreset(package.Preset))
            throw new InvalidDataException("Invalid map preset.");
        if (Data.Presets.ContainsKey(package.Name)) throw new InvalidOperationException("A preset with this name already exists.");
        PutPreset(package.Name, package.Preset); return package.Name;
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
