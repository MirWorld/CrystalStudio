using System.IO;

namespace CrystalStudio;

internal sealed class MapFormatPlugins
{
    internal static MapFormatPlugins Default { get; } = new();
    private readonly List<NativeMapPlugin> plugins = [];
    private readonly List<string> errors = [];
    private readonly object gate = new();
    private readonly string registrationPath;
    private bool initialized;
    internal MapFormatPlugins(string? registrationPath = null) => this.registrationPath = registrationPath ?? Path.Combine(EditorPaths.ProfileDirectory, "map-plugins.json");
    internal NativeMapPlugin[] Plugins { get { EnsureLoaded(); lock (gate) return plugins.ToArray(); } }
    internal string[] Errors { get { EnsureLoaded(); lock (gate) return errors.ToArray(); } }

    private void EnsureLoaded()
    {
        lock (gate)
        {
            if (initialized) return;
            initialized = true;
            var paths = new List<string>();
            string folder = Path.Combine(AppContext.BaseDirectory, "Plugins", "MapFormats");
            try
            {
                // One plugin per subfolder; dependencies stay alongside its entry DLL.
                if (Directory.Exists(folder)) paths.AddRange(Directory.GetFiles(folder, "*.dll").Concat(Directory.GetDirectories(folder).SelectMany(d => Directory.GetFiles(d, "*.plugin.dll"))));
                if (File.Exists(registrationPath)) paths.AddRange(StudioJson.Deserialize<List<string>>(File.ReadAllText(registrationPath)) ?? []);
            }
            catch (Exception e) { errors.Add(e.Message); StudioLog.Error("MapPlugins.Discover", e); }
            foreach (string path in paths.Distinct(StringComparer.OrdinalIgnoreCase))
                try { Load(path, false); }
                catch (Exception e) { errors.Add($"{path}: {e.Message}"); StudioLog.Error("MapPlugins.Load", e, path); }
        }
    }

    internal NativeMapPlugin Load(string path, bool remember = true)
    {
        EnsureLoaded();
        lock (gate)
        {
            string fullPath = Path.GetFullPath(path);
            var existing = plugins.FirstOrDefault(p => string.Equals(p.FilePath, fullPath, StringComparison.OrdinalIgnoreCase));
            if (existing != null) return existing;
            var plugin = new NativeMapPlugin(fullPath);
            if (plugins.Any(p => p.Id == plugin.Id))
            { plugin.ReleaseRejected(); throw new InvalidDataException($"Duplicate map plugin ID: {plugin.Id}. Restart after replacing the old DLL."); }
            plugins.Add(plugin);
            if (remember)
            {
                try { AtomicFile.WriteJson(registrationPath, plugins.Select(p => p.FilePath).ToList()); }
                catch { plugins.Remove(plugin); plugin.ReleaseRejected(); throw; }
            }
            return plugin;
        }
    }

    internal NativeMapPlugin Require(string id) => Plugins.FirstOrDefault(p => p.Id == id)
        ?? throw new InvalidOperationException($"Map format plugin '{id}' is missing. Load its DLL before saving, or use Save As to choose Crystal format.");

    internal string OpenFilter => "Supported maps|" + string.Join(';', new[] { ".map" }.Concat(Plugins.Where(p => p.CanRead).SelectMany(p => p.Extensions)).Distinct().Select(e => "*" + e)) + "|All files|*.*";

    internal MapDocument Open(string path, AssetSourceFormat format)
    {
        var readers = Plugins.Where(p => p.CanRead).ToArray();
        if (readers.Length == 0) return MapDocument.Load(path, format);
        if (new FileInfo(path).Length > NativeMapPlugin.MaximumBytes) throw new InvalidDataException("Map exceeds the plugin input size limit.");
        byte[] bytes = File.ReadAllBytes(path);
        var matches = readers.Where(p => p.Matches(bytes)).ToArray();
        if (matches.Length > 1) throw new InvalidDataException("Multiple plugins recognize this map: " + string.Join(", ", matches.Select(p => p.Name)));
        if (matches.Length == 0) return MapDocument.Load(path, format);
        return PluginMapExchange.Decode(matches[0].Read(bytes), matches[0].Id);
    }

    internal void Save(MapDocument map, string path, string? pluginId, bool overwrite = true)
    {
        if (pluginId == null) { if (overwrite) map.Save(path); else map.SaveNew(path); return; }
        var plugin = Require(pluginId);
        // Encode before opening the destination; conversion failures cannot damage it.
        byte[] output = plugin.Write(PluginMapExchange.Encode(map, includePrivateData: pluginId == map.PluginId));
        AtomicFile.Write(path, s => s.Write(output), overwrite, backup: overwrite);
    }
}
