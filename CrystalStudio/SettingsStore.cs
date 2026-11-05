using System.IO;
using System.Text.Json;
namespace CrystalStudio;

public enum AssetSourceFormat { Default, Sanxiu }

public sealed record LibrarySettings(string? LibraryFolder = null, string? Language = null,
    AssetSourceFormat AssetFormat = AssetSourceFormat.Default);

public sealed class SettingsStore
{
    public string FilePath { get; }
    public SettingsStore(string? filePath = null)
    {
        FilePath = filePath ?? Path.Combine(EditorPaths.ProfileDirectory, "settings.json");
    }
    public LibrarySettings Load()
    {
        if (File.Exists(FilePath)) return Read(FilePath);
        return new();
    }
    private static LibrarySettings Read(string path)
    {
        using var stream = File.OpenRead(path);
        return StudioJson.Deserialize<LibrarySettings>(stream)
            ?? throw new InvalidDataException("Settings are empty. Open Settings to configure your library folder.");
    }
    public void Save(LibrarySettings settings) => AtomicFile.WriteJson(FilePath, settings);
}
