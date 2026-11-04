namespace CrystalStudio;

internal static class StoneTempleSpecialWalls
{
    internal static bool IsSpecial(string id) => id is "mir/mir_00373" or "mir/mir_00374" or "mir/mir_00375" or "mir/mir_00376";

    internal static void Apply(WallFamily family, Dictionary<(int X, int Y), List<WallPart>> recipes,
        HashSet<(int X, int Y)> occupied, IReadOnlyDictionary<(int X, int Y), int> seeds)
    {
        if (!Enumerable.Range(373, 4).All(n => family.Pieces.Any(p => p.Id == $"mir/mir_{n:00000}"))) return;
        var selected = new List<(int X, int Y)>();
        foreach (var (anchor, parts) in recipes.OrderBy(p => p.Key.Y).ThenBy(p => p.Key.X))
        {
            uint roll = unchecked((uint)seeds[anchor] * 0x9e3779b9u);
            roll ^= roll >> 16;
            // Three in four eligible sections; reserve a plain section between projections.
            if (roll % 4 == 3 || selected.Any(p => Math.Abs(p.X - anchor.X) + Math.Abs(p.Y - anchor.Y) < 40)) continue;
            bool front = parts.Count == 2 && parts.All(p => p.PieceId == "mir/mir_00350");
            bool rear = parts.Count == 2 && parts.All(p => p.PieceId == "mir/mir_00346");
            if ((!front && !rear) || parts.Any(p => p.CellSelection != null || p.FrontSelection != null || p.FrontColumnSelection != null)) continue;
            int inward = front ? 1 : -1;
            bool fits = true;
            // A straight three-section boundary and two full interior rows leave
            // room for the native projection, its floor, and a walkable bypass.
            for (int along = -1; along <= 1; along++)
            for (int depth = -1; depth <= 2; depth++)
            {
                var p = (anchor.X + along * 10 + inward * depth * 10,
                    anchor.Y - along * 10 + inward * depth * 10);
                if (occupied.Contains(p) != (depth >= 0)) fits = false;
            }
            if (!fits) continue;
            int variant = (int)((roll >> 8) & 1);
            // Native ports: 373 -> 349 at (+7,-8)/(-8,+7), with 374
            // shifted one column; 375/376 retain the rear bank's five-cell phase.
            var first = parts.MaxBy(p => p.X)!;
            parts.Clear();
            parts.Add(front
                ? new($"mir/mir_{373 + variant:00000}", first.X - 2 + variant, first.Y + 3)
                : new($"mir/mir_{375 + variant:00000}", first.X - 5, first.Y + 5 - variant * 2));
            selected.Add(anchor);
        }
    }

    internal static Dictionary<(int X, int Y), int>? Collision(WallFamily family, IReadOnlyList<WallPart> parts)
    {
        Dictionary<(int X, int Y), int>? result = null;
        foreach (var part in parts.Where(p => IsSpecial(p.PieceId)))
        foreach (var cell in family.Pieces.Single(p => p.Id == part.PieceId).Cells)
        {
            result ??= [];
            var p = (part.X + cell.X, part.Y + cell.Y);
            result[p] = result.GetValueOrDefault(p) | (cell.Cell.BackImage & 0x60000000);
        }
        return result;
    }
}
