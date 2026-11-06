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
            ((cell.BackImage & 0x60000000) == 0 && (cell.Foreground == null || (cell.Foreground.Image & 0x8000) == 0));
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
        var result = new ModuleCaptureDraft(source, revision, cells, new(1, points) { Footprint = footprint });
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
        if (string.IsNullOrWhiteSpace(name) || name.Trim().Length > 128 || name.Any(char.IsControl))
            throw new InvalidDataException(LocalText.T("Enter a module name of 1–128 characters without control characters."));
        var reviewed = connections with { Footprint = draft.Connections.Footprint };
        ModuleGeneration.ValidateModule(new(name.Trim(), draft.Cells, reviewed), token);
        return ModuleConnectionRules.PrepareSave(draft.Cells, reviewed);
    }
}

internal static class ModuleCaptureStore
{
    private static readonly JsonSerializerOptions json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };

    internal static CategorizedStamp Save(string indexPath, ModuleCaptureDraft draft, string name, ModuleConnections connections,
        CancellationToken token = default, Action<string>? fault = null)
    {
        var prepared = ModuleCapture.Prepare(draft, name, connections, token);
        indexPath = Path.GetFullPath(indexPath);
        using var writerLock = new FileStream(indexPath + ".extraction.lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        byte[] before = File.ReadAllBytes(indexPath);
        var root = JsonNode.Parse(before)?.AsObject() ?? throw new InvalidDataException("Empty catalogue.");
        var index = StampCategories.Read(indexPath);
        var entries = root["objects"]?.AsArray() ?? throw new InvalidDataException("Missing catalogue objects.");
        string id = "module_" + Guid.NewGuid().ToString("N");
        if (index.Objects.Any(s => s.Id.Equals(id, StringComparison.OrdinalIgnoreCase)))
            throw new IOException("Module ID already exists.");
        string file = "Modules/" + id + ".X";
        var entry = new CategorizedStamp(id, file, name.Trim(), "Module", null, [], [], [], ["module", "from-selection"],
            null, "unknown", null, false, "reviewed") { Connections = prepared.Connections };
        string output = StampCategories.Resolve(index.ObjectRoot, file), temporary = output + "." + Guid.NewGuid().ToString("N") + ".tmp";
        bool created = false, committed = false;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(output)!);
            CellCodec.SaveStamp(temporary, prepared.Cells);
            token.ThrowIfCancellationRequested();
            File.Move(temporary, output); created = true;
            fault?.Invoke("object");
            entries.Add(StudioJson.SerializeToNode(entry, json));
            fault?.Invoke("index");
            token.ThrowIfCancellationRequested(); ModuleCapture.EnsureCurrent(draft);
            if (!File.ReadAllBytes(indexPath).AsSpan().SequenceEqual(before))
                throw new IOException(LocalText.T("The catalogue changed during module saving. Open the preview again."));
            AtomicFile.Write(indexPath, stream => StudioJson.Serialize(stream, root, json));
            committed = true;
            return entry;
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
            if (created && !committed) File.Delete(output);
        }
    }
}
