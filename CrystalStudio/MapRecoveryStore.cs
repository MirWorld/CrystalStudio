using System.IO;
using System.Text.Json;

namespace CrystalStudio;

internal sealed record MapRecoveryData(Guid Id, string Name, string? OriginalPath, DateTime SavedUtc,
    int Width, int Height, AssetSourceFormat Format, List<StampCell> Cells, List<PlacedStamp> Stamps,
    bool IsStamp = false, int OriginX = 0, int OriginY = 0, CategorizedStamp? Category = null, bool CategoryDirty = false)
{
    internal MapDocument Restore()
    {
        if (Cells == null || Stamps == null || !MapLimits.CanRead(Width, Height) || Cells.Count > (long)Width * Height)
            throw new InvalidDataException("Invalid recovery dimensions.");
        var document = new MapDocument(Width, Height) { SourceFormat = Format };
        var seen = new HashSet<int>();
        foreach (var cell in Cells)
        {
            if (!document.Contains(cell.X, cell.Y) || !seen.Add(cell.Y * Width + cell.X))
                throw new InvalidDataException("Invalid recovery cell coordinates.");
            document.Set(cell.X, cell.Y, cell.Cell.Copy());
        }
        if (Stamps.Count > 100000 || Stamps.Any(s => s.Changes.Any(c =>
            (long)s.X + c.X < 0 || (long)s.Y + c.Y < 0 || (long)s.X + c.X >= Width || (long)s.Y + c.Y >= Height)))
            throw new InvalidDataException("Invalid recovery stamp coordinates.");
        document.RestoreStamps(Stamps);
        return document;
    }
}

internal sealed record MapRecoveryEntry(string FilePath, MapRecoveryData Data)
{
    public override string ToString() => $"{Data.Name} · {Data.SavedUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss} · {Data.OriginalPath}";
}

internal sealed class MapRecoveryStore : IDisposable
{
    private readonly string root, session;
    private readonly FileStream lease;
    private readonly object gate = new();
    private readonly Dictionary<Guid, int> versions = [];
    private bool disposed;

    internal MapRecoveryStore(string? directory = null)
    {
        root = directory ?? Path.Combine(EditorPaths.ProfileDirectory, "Recovery");
        session = Path.Combine(root, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(session);
        lease = new FileStream(Path.Combine(session, "session.lock"), FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
    }
    internal int Version(Guid id) { lock (gate) return versions.GetValueOrDefault(id); }
    internal void Write(MapRecoveryData data, int version)
    {
        lock (gate)
        {
            if (disposed || versions.GetValueOrDefault(data.Id) != version) return;
            AtomicFile.WriteJson(Path.Combine(session, data.Id.ToString("N") + ".json"), data);
        }
    }
    internal void Remove(Guid id)
    {
        lock (gate)
        {
            versions[id] = versions.GetValueOrDefault(id) + 1;
            File.Delete(Path.Combine(session, id.ToString("N") + ".json"));
        }
    }
    internal List<MapRecoveryEntry> Available()
    {
        var entries = new List<MapRecoveryEntry>();
        foreach (var folder in Directory.EnumerateDirectories(root))
        {
            if (folder == session) continue;
            // A live editor exclusively owns its session; never offer its snapshots.
            try
            {
                using var probe = new FileStream(Path.Combine(folder, "session.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                foreach (var path in Directory.EnumerateFiles(folder, "*.json"))
                {
                    try
                    {
                        using var stream = File.OpenRead(path);
                        var data = StudioJson.Deserialize<MapRecoveryData>(stream);
                        if (data != null) entries.Add(new(path, data));
                    }
                    catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
                    { StudioLog.Error("Recovery.Read", ex, path); }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        return entries.OrderByDescending(e => e.Data.SavedUtc).ToList();
    }
    internal static void Dismiss(MapRecoveryEntry entry) => File.Delete(entry.FilePath);
    public void Dispose() => Close(clean: true);
    internal void Close(bool clean)
    {
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            lease.Dispose();
            if (!clean) return;
            foreach (var path in Directory.EnumerateFiles(session, "*.json")) File.Delete(path);
            File.Delete(Path.Combine(session, "session.lock"));
            Directory.Delete(session);
        }
    }
}
