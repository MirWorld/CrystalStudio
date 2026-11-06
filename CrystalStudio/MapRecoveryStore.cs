using System.IO;

namespace CrystalStudio;

internal sealed record MapRecoveryEntry(int Version, Guid SessionId, Guid Id, string SnapshotFile,
    string? OriginalPath, string DisplayName, DateTime SavedAtUtc, AssetSourceFormat AssetFormat, MapCreationRecipe? Recipe,
    bool IsStamp = false, int OriginX = 0, int OriginY = 0, CategorizedStamp? Category = null,
    bool CategoryDirty = false, bool LegacyJson = false);

internal sealed class MapRecoveryStore : IDisposable
{
    private readonly string root;
    private readonly Guid sessionId = Guid.NewGuid();
    private readonly object gate = new();
    private FileStream? lease;
    private bool disposed;
    private readonly Dictionary<Guid, int> versions = [];
    internal int Version(Guid id) { lock (gate) return versions.GetValueOrDefault(id); }
    public Guid SessionId => sessionId;
    private string SessionFolder(Guid session) => Path.Combine(root, session.ToString("N"));

    public MapRecoveryStore(string? directory = null) => root = Path.GetFullPath(directory ?? Path.Combine(EditorPaths.ProfileDirectory, "recovery"));

    public static MapDocument Capture(MapDocument document)
    {
        var snapshot = new MapDocument(document.Width, document.Height) { SourceFormat = document.SourceFormat };
        foreach (var pair in document.Cells) snapshot.Set(pair.Key % document.Width, pair.Key / document.Width, pair.Value.Copy());
        snapshot.RestoreStamps(document.PlacedStamps.Select(stamp => stamp with
        {
            Cells = stamp.Cells.Select(cell => cell with { Cell = cell.Cell.Copy() }).ToList(),
            Changes = stamp.Changes.Select(change => change with { Before = change.Before.Copy(), After = change.After.Copy() }).ToList()
        }));
        return snapshot;
    }

    public void Write(Guid id, MapDocument snapshot, string? originalPath, string displayName, MapCreationRecipe? recipe, MapRecoveryEntry? metadata = null, int? version = null)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (version.HasValue && versions.GetValueOrDefault(id) != version.Value) return;
            string folder = SessionFolder(sessionId); Directory.CreateDirectory(folder);
            lease ??= new FileStream(Path.Combine(folder, "session.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read);
            string manifest = Path.Combine(folder, id.ToString("N") + ".json");
            string filename = id.ToString("N") + "." + Guid.NewGuid().ToString("N") + ".map";
            string target = Path.Combine(folder, filename);
            try
            {
                snapshot.Save(target);
                AtomicFile.WriteJson(manifest, new MapRecoveryEntry(1, sessionId, id, filename, originalPath, displayName,
                    DateTime.UtcNow, snapshot.SourceFormat, recipe, metadata?.IsStamp ?? false, metadata?.OriginX ?? 0, metadata?.OriginY ?? 0,
                    metadata?.Category, metadata?.CategoryDirty ?? false));
            }
            catch { if (File.Exists(target)) File.Delete(target); throw; }
            foreach (string old in Directory.GetFiles(folder, id.ToString("N") + ".*.map"))
                if (!string.Equals(old, target, StringComparison.OrdinalIgnoreCase)) TryDelete(old);
        }
    }

    public IReadOnlyList<MapRecoveryEntry> List()
    {
        lock (gate)
        {
            var entries = new List<MapRecoveryEntry>();
            if (!Directory.Exists(root)) return entries;
            foreach (string directory in Directory.GetDirectories(root))
            {
                if (!Guid.TryParseExact(Path.GetFileName(directory), "N", out Guid session) || session == sessionId ||
                    (File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) continue;
                try
                {
                    using var inactive = new FileStream(Path.Combine(directory, "session.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                    foreach (string path in Directory.GetFiles(directory, "*.json"))
                    {
                        try
                        {
                            using var stream = File.OpenRead(path);
                            var entry = StudioJson.Deserialize<MapRecoveryEntry>(stream);
                            if (entry is { Version: 0 })
                            {
                                stream.Position = 0;
                                var legacy = StudioJson.Deserialize<MapRecoveryData>(stream);
                                if (legacy != null && MapDimensions.CanRoundTrip(legacy.Width, legacy.Height) && Enum.IsDefined(legacy.Format))
                                    entry = new(1, session, legacy.Id, Path.GetFileName(path), legacy.OriginalPath, legacy.Name,
                                        legacy.SavedUtc, legacy.Format, null, legacy.IsStamp, legacy.OriginX, legacy.OriginY,
                                        legacy.Category, legacy.CategoryDirty, LegacyJson: true);
                            }
                            else if (stream.Length > 1_048_576) continue;
                            if (entry == null || entry.Version != 1 || entry.SessionId != session || !Enum.IsDefined(entry.AssetFormat) ||
                                Path.GetFileName(path) != entry.Id.ToString("N") + ".json" || !ValidSnapshotName(entry) ||
                                !File.Exists(Path.Combine(directory, entry.SnapshotFile))) continue;
                            entries.Add(entry);
                        }
                        catch (Exception exception) when (exception is IOException or System.Text.Json.JsonException or UnauthorizedAccessException)
                        { StudioLog.Error("Recovery.ReadEntry", exception, $"Path={path}"); }
                    }
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
            }
            return entries.OrderByDescending(entry => entry.SavedAtUtc).ToArray();
        }
    }

    private static bool ValidSnapshotName(MapRecoveryEntry entry)
    {
        if (string.IsNullOrWhiteSpace(entry.SnapshotFile)) return false;
        if (entry.LegacyJson) return entry.SnapshotFile == entry.Id.ToString("N") + ".json";
        string[] parts = entry.SnapshotFile.Split('.');
        return parts.Length == 3 && parts[0] == entry.Id.ToString("N") && Guid.TryParseExact(parts[1], "N", out _) && parts[2] == "map";
    }

    public MapDocument Load(MapRecoveryEntry entry)
    {
        if (!ValidSnapshotName(entry) || !Enum.IsDefined(entry.AssetFormat)) throw new InvalidDataException("Invalid recovery snapshot.");
        string path = Path.Combine(SessionFolder(entry.SessionId), entry.SnapshotFile);
        if (entry.LegacyJson)
        {
            using var stream = File.OpenRead(path);
            return (StudioJson.Deserialize<MapRecoveryData>(stream) ?? throw new InvalidDataException("Empty recovery snapshot.")).Restore();
        }
        return MapDocument.Load(path, entry.AssetFormat);
    }

    public void Delete(Guid id) => Delete(new MapRecoveryEntry(1, sessionId, id, "", null, "", DateTime.UtcNow, AssetSourceFormat.Default, null));
    public void Delete(MapRecoveryEntry entry)
    {
        lock (gate)
        {
            if (entry.SessionId == sessionId) versions[entry.Id] = versions.GetValueOrDefault(entry.Id) + 1;
            string folder = SessionFolder(entry.SessionId);
            if (!Directory.Exists(folder)) return;
            FileStream? inactive = null;
            try
            {
                if (entry.SessionId != sessionId)
                    inactive = new FileStream(Path.Combine(folder, "session.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                TryDelete(Path.Combine(folder, entry.Id.ToString("N") + ".json"));
                foreach (string path in Directory.GetFiles(folder, entry.Id.ToString("N") + ".*.map")) TryDelete(path);
            }
            finally { inactive?.Dispose(); }
        }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        { StudioLog.Error("Recovery.Cleanup", exception, $"Path={path}"); }
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            disposed = true; lease?.Dispose(); lease = null;
        }
    }

    public void ClearSession()
    {
        lock (gate)
        {
            Dispose();
            string folder = SessionFolder(sessionId);
            if (!Directory.Exists(folder)) return;
            foreach (string path in Directory.GetFiles(folder))
                if (Path.GetExtension(path) is ".json" or ".map" or ".tmp" || Path.GetFileName(path) == "session.lock") TryDelete(path);
            try { Directory.Delete(folder); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            { StudioLog.Error("Recovery.CleanupSession", exception); }
        }
    }
}
