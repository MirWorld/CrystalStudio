namespace CrystalStudio;

public sealed record GeneratedSideRoom(int X, int Y, string PieceId);

internal static class ZumaTempleSideRooms
{
    private sealed record Definition(string Role, int Number, int DX, int DY, int PortX, int PortY,
        int InwardX, int InwardY, (int X, int Y) Bridge, (int X, int Y) Exit);
    private sealed record Slot((int X, int Y) Owner, WallPart Part, string Role, int X, int Y);
    // Each complete native room replaces four three-cell wall cards, including
    // its doorway and the terminal card continuing along the corridor.
    private static readonly Definition[] Definitions =
    [
        new("sw", 1039, 3, 3, -7, 2, -6, 6, (-4, 5), (-4, 6)),
        new("se", 1041, -3, 3, 8, 2, 6, 6, (5, 5), (5, 6)),
        new("backSW", 1037, 3, 3, -1, -13, 6, -6, (5, -4), (6, -4)),
        new("backSE", 1038, -3, 3, 2, -10, -6, -6, (-4, -4), (-5, -4))
    ];

    internal static IReadOnlyList<GeneratedSideRoom> Apply(MapDocument map, WallFamily family,
        Dictionary<(int X, int Y), List<WallPart>> recipes, HashSet<(int X, int Y)> occupied,
        IReadOnlyDictionary<(int X, int Y), int> seeds, CancellationToken cancellation = default)
    {
        var pieces = family.Pieces.ToDictionary(p => p.Id);
        if (Definitions.Any(d => !pieces.ContainsKey(Id(d.Number)))) return [];
        var interiors = Definitions.ToDictionary(d => Id(d.Number), d => Interior(pieces[Id(d.Number)]));
        var slots = recipes.SelectMany(r => r.Value.Where(p => p.CellSelection == null && p.FrontSelection == null &&
                p.FrontColumnSelection == null && p.Attachment == null)
            .Select(p => new Slot(r.Key, p, pieces[p.PieceId].Role, r.Key.X + p.X, r.Key.Y + p.Y))).ToArray();
        var byPosition = slots.GroupBy(s => (s.Role, s.X, s.Y)).ToDictionary(g => g.Key, g => g.ToArray());
        var allFronts = recipes.SelectMany(r => r.Value.SelectMany(p => family.Compose(0, 15, seeds[r.Key], [p]).Cells
            .Where(c => c.Cell.ForegroundImages.Count > 0).Select(c => (Owner: r.Key, Part: p, X: r.Key.X + c.X, Y: r.Key.Y + c.Y))))
            .ToLookup(p => (p.X, p.Y));
        var selected = new List<GeneratedSideRoom>();
        var reserved = new HashSet<(int X, int Y)>();
        var consumed = new HashSet<Slot>();
        foreach (var start in slots.OrderBy(s => s.Y).ThenBy(s => s.X).ThenBy(s => s.Role, StringComparer.Ordinal))
        {
            cancellation.ThrowIfCancellationRequested();
            var definition = Definitions.FirstOrDefault(d => d.Role == start.Role);
            if (definition == null) continue;
            uint roll = unchecked((uint)seeds[start.Owner] * 2654435761u ^ (uint)start.X * 2246822519u ^ (uint)start.Y * 3266489917u);
            roll ^= roll >> 16;
            if ((roll & 1) != 0) continue;
            var run = Enumerable.Range(-1, 6).Select(i => byPosition.GetValueOrDefault(
                (start.Role, start.X + i * definition.DX, start.Y + i * definition.DY))).ToArray();
            if (run.Any(s => s == null || s.Any(consumed.Contains))) continue;
            var replaced = run.Skip(1).Take(4).SelectMany(s => s!).ToHashSet();
            // Limit additions to narrow corridor banks, not the perimeter of
            // a large chamber. The corridor itself retains its selected width.
            if (replaced.Any(s => occupied.Contains((s.Owner.X + 2 * definition.InwardX, s.Owner.Y + 2 * definition.InwardY)))) continue;
            var room = new GeneratedSideRoom(start.X - definition.PortX, start.Y - definition.PortY, Id(definition.Number));
            var piece = pieces[room.PieceId];
            var footprint = piece.Cells.Select(c => (X: room.X + c.X, Y: room.Y + c.Y)).ToHashSet();
            foreach (var p in interiors[room.PieceId]) footprint.Add((room.X + p.X, room.Y + p.Y));
            if (footprint.Any(p => p.X < 2 || p.Y < 2 || p.X >= map.Width - 2 || p.Y >= map.Height - 2 || reserved.Contains(p))) continue;
            if (interiors[room.PieceId].Any(p => Inside(family, occupied, room.X + p.X, room.Y + p.Y))) continue;
            var replacedParts = replaced.Select(s => (s.Owner, s.Part)).ToHashSet();
            if (footprint.Any(point => allFronts[point].Any(p => !replacedParts.Contains((p.Owner, p.Part))))) continue;
            foreach (var slot in replaced) { recipes[slot.Owner].Remove(slot.Part); consumed.Add(slot); }
            var owner = replaced.OrderBy(s => s.Owner.Y).ThenBy(s => s.Owner.X).First().Owner;
            recipes[owner].Add(new(room.PieceId, room.X - owner.X, room.Y - owner.Y));
            selected.Add(room);
            foreach (var p in footprint)
            for (int y = -1; y <= 1; y++)
            for (int x = -1; x <= 1; x++) reserved.Add((p.X + x, p.Y + y));
        }
        return selected;
    }

    internal static HashSet<(int X, int Y)> OpenCells(WallFamily family, IReadOnlyList<GeneratedSideRoom> rooms)
    {
        var result = new HashSet<(int X, int Y)>();
        foreach (var room in rooms)
        {
            var definition = Definitions.Single(d => Id(d.Number) == room.PieceId);
            foreach (var p in Interior(family.Pieces.Single(p => p.Id == room.PieceId))) result.Add((room.X + p.X, room.Y + p.Y));
            // Native doorways meet on a diagonal. Open the threshold cell too
            // so the room has a continuous, four-connected walking route.
            result.Add((room.X + definition.Bridge.X, room.Y + definition.Bridge.Y));
            result.Add((room.X + definition.Exit.X, room.Y + definition.Exit.Y));
        }
        return result;
    }

    internal static Dictionary<(int X, int Y), int>? Collision(WallFamily family, IReadOnlyList<WallPart> parts)
    {
        Dictionary<(int X, int Y), int>? result = null;
        foreach (var part in parts.Where(p => Definitions.Any(d => Id(d.Number) == p.PieceId) &&
            p.CellSelection == null && p.FrontSelection == null && p.FrontColumnSelection == null))
        foreach (var cell in family.Pieces.Single(p => p.Id == part.PieceId).Cells)
        {
            result ??= [];
            var point = (part.X + cell.X, part.Y + cell.Y);
            result[point] = result.GetValueOrDefault(point) | (cell.Cell.BackImage & 0x60000000);
        }
        return result;
    }

    private static HashSet<(int X, int Y)> Interior(WallPiece piece)
    {
        var blocked = piece.Cells.Where(c => (c.Cell.BackImage & 0x60000000) != 0).Select(c => (c.X, c.Y)).ToHashSet();
        var result = new HashSet<(int X, int Y)>();
        var queue = new Queue<(int X, int Y)>(); queue.Enqueue((0, 1));
        while (queue.TryDequeue(out var p))
        {
            if (Math.Abs(p.X - .5) + Math.Abs(p.Y - 1) > 10.5 || blocked.Contains(p) || !result.Add(p)) continue;
            queue.Enqueue((p.X - 1, p.Y)); queue.Enqueue((p.X + 1, p.Y));
            queue.Enqueue((p.X, p.Y - 1)); queue.Enqueue((p.X, p.Y + 1));
        }
        return result;
    }

    private static bool Inside(WallFamily family, HashSet<(int X, int Y)> occupied, int x, int y)
    {
        var p = family.Snap(x, y);
        return occupied.Contains(p) && family.ContainsOffset(x - p.X, y - p.Y);
    }
    private static string Id(int number) => $"mir/mir_{number:00000}";
}
