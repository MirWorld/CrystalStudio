using System.IO;
using System.Text.Json;

namespace CrystalStudio;

internal sealed class AttachedAssetManifest
{
    public int SchemaVersion { get; set; }
    public List<AttachedLibrary> Libraries { get; set; } = [];
}

internal sealed class AttachedLibrary
{
    public int Id { get; set; }
    public string File { get; set; } = "";
}

internal static class AttachedAssets
{
    internal static void Register(AssetStore assets, string root)
    {
        string manifestPath = Path.Combine(root, "libraries.json");
        if (!System.IO.File.Exists(manifestPath)) return;
        var manifest = StudioJson.Deserialize<AttachedAssetManifest>(System.IO.File.ReadAllText(manifestPath),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        if (manifest == null || manifest.SchemaVersion != 1 || manifest.Libraries == null)
            throw new InvalidDataException("Unsupported attached asset manifest: " + manifestPath);
        var libraries = new Dictionary<int, CrystalLibrary>();
        string prefix = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar;
        foreach (var entry in manifest.Libraries)
        {
            if (entry == null || entry.Id is < 0 or > short.MaxValue || string.IsNullOrWhiteSpace(entry.File))
                throw new InvalidDataException("Invalid attached library entry: " + manifestPath);
            string path = Path.GetFullPath(Path.Combine(root, entry.File));
            if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || Path.IsPathRooted(entry.File) ||
                !Path.GetExtension(path).Equals(".lib", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Attached libraries must be .lib files inside the attachment folder: " + entry.File);
            if (assets.Libraries.ContainsKey(entry.Id) || libraries.ContainsKey(entry.Id))
                throw new InvalidDataException($"Attached library ID {entry.Id} conflicts with another library: {manifestPath}");
            libraries.Add(entry.Id, new CrystalLibrary(path));
        }
        foreach (var pair in libraries) assets.Libraries.Add(pair.Key, pair.Value);
    }
}
