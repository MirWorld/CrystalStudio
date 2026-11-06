using System.IO;

namespace CrystalStudio;

public enum ModuleEdge { North, East, South, West }
public sealed record ModuleConnection(string Id, int X, int Y, ModuleEdge Edge, string Kind, int Width = 1);
public sealed record ModuleFootprint(int X, int Y, int Width, int Height);
public sealed record ModuleConnections(int Version, List<ModuleConnection> Points)
{
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public ModuleFootprint? Footprint { get; init; }
}

internal static class ModuleConnectionRules
{
    internal readonly record struct Bounds(int Left, int Top, int Right, int Bottom);

    internal static Bounds GetBounds(IReadOnlyList<StampCell> cells, ModuleConnections? connections = null)
    {
        var occupied = cells.Where(c => !c.Cell.Empty).ToArray();
        if (occupied.Length == 0) throw new InvalidDataException(LocalText.T("Paint a module before defining connections."));
        int left = occupied.Min(c => c.X), top = occupied.Min(c => c.Y);
        int right = occupied.Max(c => c.X), bottom = occupied.Max(c => c.Y);
        if (connections?.Footprint is { } footprint)
        {
            long endX = (long)footprint.X + footprint.Width - 1, endY = (long)footprint.Y + footprint.Height - 1;
            if (footprint.Width is < 1 or > 4096 || footprint.Height is < 1 or > 4096 || endX > int.MaxValue || endY > int.MaxValue)
                throw new InvalidDataException(LocalText.T("Module bounds exceed the editor limit."));
            left = Math.Min(left, footprint.X); top = Math.Min(top, footprint.Y);
            right = Math.Max(right, (int)endX); bottom = Math.Max(bottom, (int)endY);
        }
        if ((long)right - left > 4095 || (long)bottom - top > 4095)
            throw new InvalidDataException(LocalText.T("Module bounds exceed the editor limit."));
        return new(left, top, right, bottom);
    }

    // North/south openings run towards +X; east/west openings run towards +Y.
    internal static IEnumerable<(long X, long Y)> Opening(ModuleConnection point)
    {
        for (int i = 0; i < point.Width; i++)
            yield return ((long)point.X + (point.Edge is ModuleEdge.North or ModuleEdge.South ? i : 0),
                (long)point.Y + (point.Edge is ModuleEdge.East or ModuleEdge.West ? i : 0));
    }

    internal static void Validate(ModuleConnections? connections, IReadOnlyList<StampCell> cells)
    {
        if (connections == null) return;
        if (connections.Version != 1 || connections.Points == null || connections.Points.Count > 64)
            throw new InvalidDataException(LocalText.T("Unsupported connections or more than 64 openings."));
        if (connections.Points.Count == 0) return;
        var bounds = GetBounds(cells, connections);
        var lookup = new Dictionary<(long, long), Cell>();
        foreach (var cell in cells)
            if (!lookup.TryAdd((cell.X, cell.Y), cell.Cell))
                throw new InvalidDataException(LocalText.T("Module contains duplicate cells."));
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var openings = new HashSet<(long, long)>();
        bool Inside(long x, long y) => x >= bounds.Left && x <= bounds.Right && y >= bounds.Top && y <= bounds.Bottom;
        bool Walkable(long x, long y) => !lookup.TryGetValue((x, y), out var cell) ||
            ((cell.BackImage & 0x60000000) == 0 && (cell.Foreground == null || (cell.Foreground.Image & 0x8000) == 0));
        foreach (var point in connections.Points)
        {
            if (point == null || !Token(point.Id) || !Token(point.Kind) || !ids.Add(point.Id) ||
                !Enum.IsDefined(point.Edge) || point.Width is < 1 or > 64)
                throw new InvalidDataException(LocalText.T("Use unique connection IDs, valid types and widths from 1 to 64."));
            bool edge = point.Edge switch
            {
                ModuleEdge.North => point.Y == bounds.Top,
                ModuleEdge.South => point.Y == bounds.Bottom,
                ModuleEdge.East => point.X == bounds.Right,
                _ => point.X == bounds.Left
            };
            var (dx, dy) = point.Edge switch
            {
                ModuleEdge.North => (0, 1), ModuleEdge.South => (0, -1),
                ModuleEdge.East => (-1, 0), _ => (1, 0)
            };
            foreach (var (x, y) in Opening(point))
                if (!edge || !Inside(x, y) || !Inside(x + dx, y + dy) || !openings.Add((x, y)) ||
                    !Walkable(x, y) || !Walkable(x + dx, y + dy))
                    throw new InvalidDataException(LocalText.Format("Connection {0} must be on its edge, without overlap or blocked passage.", point.Id));
        }
    }

    private static bool Token(string? text) => !string.IsNullOrWhiteSpace(text) && text.Length <= 64 &&
        text == text.Trim() && !text.Any(char.IsControl);

    internal sealed record Prepared(List<StampCell> Cells, ModuleConnections? Connections, int OffsetX, int OffsetY);

    internal static Prepared PrepareSave(IReadOnlyList<StampCell> source, ModuleConnections? connections)
    {
        Validate(connections, source);
        var cells = CellCodec.CropStamp(source);
        if (cells.Count == 0) return new(cells, connections, 0, 0);
        var first = source.First(c => !c.Cell.Empty);
        int dx = checked(first.X - cells[0].X), dy = checked(first.Y - cells[0].Y);
        var bounds = connections?.Footprint == null ? (Bounds?)null : GetBounds(source, connections);
        var shifted = connections == null ? null : connections with
        {
            Points = connections.Points.Select(p => p with { X = checked(p.X - dx), Y = checked(p.Y - dy) }).ToList(),
            Footprint = bounds is { } b ? new(checked(b.Left - dx), checked(b.Top - dy), b.Right - b.Left + 1, b.Bottom - b.Top + 1) : null
        };
        Validate(shifted, cells);
        return new(cells, shifted, dx, dy);
    }
}
