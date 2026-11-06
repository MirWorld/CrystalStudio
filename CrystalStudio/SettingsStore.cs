using System.IO;
using System.Text.Json;
namespace CrystalStudio;

public enum AssetSourceFormat { Default, Sanxiu }

/// <summary>
/// 默认保存格式。FollowSource 表示“按读取时的格式保存”——12 字节地图存回 12 字节，
/// 14 字节存回 14 字节，Crystal 地图存回 Crystal；下拉选其它值时强制写成该格式。
/// </summary>
public enum MapSaveFormat
{
    FollowSource = 0,
    Crystal = 1,
    Mir2OldMap = 2,
    Mir2NewMap = 3,
    Mir2EnhancedMap = 4,
}

public sealed record LibrarySettings(string? LibraryFolder = null, string? Language = null,
    AssetSourceFormat AssetFormat = AssetSourceFormat.Default, MapSaveFormat SaveFormat = MapSaveFormat.FollowSource);

public sealed class SettingsStore
{
    public string FilePath { get; }
    public SettingsStore(string? filePath = null)
    {
        FilePath = filePath ?? Path.Combine(EditorPaths.ProfileDirectory, "settings.json");
    }
    public LibrarySettings Load()
    {
        if (File.Exists(FilePath)) return Normalize(Read(FilePath));
        return new();
    }
    private static LibrarySettings Read(string path)
    {
        using var stream = File.OpenRead(path);
        return StudioJson.Deserialize<LibrarySettings>(stream)
            ?? throw new InvalidDataException("Settings are empty. Open Settings to configure your library folder.");
    }
    private static LibrarySettings Normalize(LibrarySettings settings) => string.IsNullOrWhiteSpace(settings.LibraryFolder)
        ? settings : settings with { LibraryFolder = DataPaths.GetClientRoot(settings.LibraryFolder) };

    public void Save(LibrarySettings settings) => AtomicFile.WriteJson(FilePath, Normalize(settings));
}
