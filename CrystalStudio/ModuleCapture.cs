using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CrystalStudio;

internal sealed record ModuleCaptureDraft(MapDocument Source, long Revision, List<StampCell> Cells,
    ModuleConnections Connections);

internal static class ModuleCapture
{
    internal static ModuleCaptureDraft Capture(MapDocument source, ModuleArea area, CancellationToken token = default)
    {
        if (area.X < 0 || area.Y < 0 || area.Width is < 2 or > 4096 || area.Height is < 2 or > 4096 ||
            (long)area.X + area.Width > source.Width || (long)area.Y + area.Height > source.Height ||
            (long)area.Width * area.Height > 1_000_000)
            throw new InvalidDataException(LocalText.T("Select a rectangle at least 2 × 2 and at most 1,000,000 cells inside the map."));
        long revision = source.PickupRevision;
        var cells = new List<StampCell>();
        for (int y = 0; y < area.Height; y++)
        {
            token.ThrowIfCancellationRequested();
            for (int x = 0; x < area.Width; x++)
            {
                var cell = source.Get(area.X + x, area.Y + y);
                if (!cell.Empty) cells.Add(new(x, y, cell.Copy()));
            }
        }
        if (cells.Count == 0) throw new InvalidDataException(LocalText.T("Paint a module before defining connections."));
        // Keep the selection, including its empty margins, separate from the sparse .X artwork.
        // Preserve absolute ground parity when moving to selection-local coordinates.
        int parityX = area.X & 1, parityY = area.Y & 1;
        cells = cells.Select(c => c with { X = c.X + parityX, Y = c.Y + parityY }).ToList();
        var footprint = new ModuleFootprint(parityX, parityY, area.Width, area.Height);
        var lookup = cells.ToDictionary(c => (c.X, c.Y), c => c.Cell);
        bool Walkable(int x, int y) => !lookup.TryGetValue((x, y), out var cell) ||
            !ModuleTerrain.Blocked(cell);
        var points = new List<ModuleConnection>();
        var claimed = new HashSet<(int X, int Y)>();
        // North/south own walkable corners. Adjacent edges never propose overlapping openings.
        foreach (var edge in new[] { ModuleEdge.North, ModuleEdge.South, ModuleEdge.East, ModuleEdge.West })
        {
            token.ThrowIfCancellationRequested();
            bool horizontal = edge is ModuleEdge.North or ModuleEdge.South;
            int length = horizontal ? area.Width : area.Height, start = -1, number = 0;
            (int X, int Y) Position(int n) => edge switch
            {
                ModuleEdge.North => (parityX + n, parityY),
                ModuleEdge.South => (parityX + n, parityY + area.Height - 1),
                ModuleEdge.East => (parityX + area.Width - 1, parityY + n),
                _ => (parityX, parityY + n)
            };
            var (dx, dy) = edge switch
            { ModuleEdge.North => (0, 1), ModuleEdge.South => (0, -1), ModuleEdge.East => (-1, 0), _ => (1, 0) };
            void Flush(int end)
            {
                if (start < 0) return;
                for (int first = start; first < end; first += 64)
                {
                    var (x, y) = Position(first); int width = Math.Min(64, end - first);
                    points.Add(new($"{edge.ToString().ToLowerInvariant()}-{++number}", x, y, edge, "path", width));
                    for (int i = first; i < first + width; i++) claimed.Add(Position(i));
                }
                start = -1;
            }
            for (int n = 0; n < length; n++)
            {
                var (x, y) = Position(n);
                if (!claimed.Contains((x, y)) && Walkable(x, y) && Walkable(x + dx, y + dy))
                { if (start < 0) start = n; }
                else Flush(n);
            }
            Flush(length);
        }
        token.ThrowIfCancellationRequested();
        var result = new ModuleCaptureDraft(source, revision, cells, new(1, points) { Footprint = footprint, MapGroundGrid = true });
        EnsureCurrent(result);
        return result;
    }

    internal static void EnsureCurrent(ModuleCaptureDraft draft)
    {
        if (draft.Source.PickupRevision != draft.Revision)
            throw new InvalidOperationException(LocalText.T("The map changed after the module preview. Generate a fresh preview."));
    }

    internal static ModuleConnectionRules.Prepared Prepare(ModuleCaptureDraft draft, string name, ModuleConnections connections,
        CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested(); EnsureCurrent(draft);
        ValidateName(name);
        var reviewed = connections with { Footprint = draft.Connections.Footprint, MapGroundGrid = true };
        ModuleGeneration.ValidateModule(new(name.Trim(), draft.Cells, reviewed), token);
        return ModuleConnectionRules.PrepareSave(draft.Cells, reviewed);
    }
    internal static void ValidateName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Trim().Length > 128 || name.Any(char.IsControl))
            throw new InvalidDataException(LocalText.T("Enter a module name of 1–128 characters without control characters."));
    }
}

internal sealed record ModuleCaptureRequest(ModuleCaptureDraft Draft, string Name, ModuleConnections Connections,
    string Tag = "from-selection");

internal static class ModuleCaptureStore
{
    private static readonly JsonSerializerOptions json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };

    internal static CategorizedStamp Save(string indexPath, ModuleCaptureDraft draft, string name, ModuleConnections connections,
        CancellationToken token = default, Action<string>? fault = null) =>
        SaveBatch(indexPath, [new(draft, name, connections)], token, fault)[0];

    internal static IReadOnlyList<CategorizedStamp> SaveBatch(string indexPath, IReadOnlyList<ModuleCaptureRequest> requests,
        CancellationToken token = default, Action<string>? fault = null)
    {
        if (requests.Count is < 1 or > 256)
            throw new InvalidDataException(LocalText.T("Select between 1 and 256 modules for saving."));
        long total = 0;
        var prepared = requests.Select(request =>
        {
            var bounds = ModuleConnectionRules.GetBounds(request.Draft.Cells, request.Connections);
            total += ((long)bounds.Right - bounds.Left + 1) * (bounds.Bottom - bounds.Top + 1);
            if (total > 4_000_000) throw new InvalidDataException(LocalText.T("The selected module pool is too large. Select fewer modules."));
            return ModuleCapture.Prepare(request.Draft, request.Name, request.Connections, token);
        }).ToArray();
        indexPath = Path.GetFullPath(indexPath);
        using var writerLock = new FileStream(indexPath + ".extraction.lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        byte[] before = File.ReadAllBytes(indexPath);
        var root = JsonNode.Parse(before)?.AsObject() ?? throw new InvalidDataException("Empty catalogue.");
        var index = StampCategories.Read(indexPath);
        var entries = root["objects"]?.AsArray() ?? throw new InvalidDataException("Missing catalogue objects.");
        var ids = index.Objects.Select(s => s.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var created = new List<string>(); var temporary = new List<string>(); var saved = new List<CategorizedStamp>();
        bool committed = false;
        try
        {
            for (int i = 0; i < requests.Count; i++)
            {
                token.ThrowIfCancellationRequested();
                string id = "module_" + Guid.NewGuid().ToString("N");
                if (!ids.Add(id)) throw new IOException("Module ID already exists.");
                string file = "Modules/" + id + ".X";
                var request = requests[i];
                var entry = new CategorizedStamp(id, file, request.Name.Trim(), "Module", null, [], [], [], ["module", request.Tag],
                    null, "unknown", null, false, "reviewed") { Connections = prepared[i].Connections };
                string output = StampCategories.Resolve(index.ObjectRoot, file), temp = output + "." + Guid.NewGuid().ToString("N") + ".tmp";
                temporary.Add(temp); Directory.CreateDirectory(Path.GetDirectoryName(output)!);
                CellCodec.SaveStamp(temp, prepared[i].Cells);
                token.ThrowIfCancellationRequested(); File.Move(temp, output); created.Add(output);
                fault?.Invoke("object"); entries.Add(StudioJson.SerializeToNode(entry, json)); saved.Add(entry);
            }
            fault?.Invoke("index"); token.ThrowIfCancellationRequested();
            foreach (var request in requests) ModuleCapture.EnsureCurrent(request.Draft);
            if (!File.ReadAllBytes(indexPath).AsSpan().SequenceEqual(before))
                throw new IOException(LocalText.T("The catalogue changed during module saving. Open the preview again."));
            AtomicFile.Write(indexPath, stream => StudioJson.Serialize(stream, root, json));
            committed = true;
            return saved;
        }
        finally
        {
            foreach (string path in temporary) if (File.Exists(path)) File.Delete(path);
            if (!committed) foreach (string path in created) File.Delete(path);
        }
    }
}
