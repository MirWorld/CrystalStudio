using System.IO;

namespace CrystalStudio;

internal sealed record JoinModule(string Name, IReadOnlyList<StampCell> Cells, ModuleConnections Connections);
internal sealed record ModuleJoinPlacement(JoinModule Module, int X, int Y);
internal sealed record ModuleJoinPlan(MapDocument Source, long SourceRevision, MapDocument Generated,
    IReadOnlyList<ModuleJoinPlacement> Placements, IReadOnlyList<StampCell> Cells,
    IReadOnlyList<ModuleConnection> Openings, ModuleConnectionRules.Bounds Bounds);

internal static class ModuleJoining
{
    internal static bool Matches(ModuleConnection first, ModuleConnection second) =>
        Enum.IsDefined(first.Edge) && Enum.IsDefined(second.Edge) &&
        ((int)first.Edge + 2) % 4 == (int)second.Edge && first.Width == second.Width &&
        string.Equals(first.Kind, second.Kind, StringComparison.OrdinalIgnoreCase);

    internal static JoinModule Load(StampCategoryIndex catalogue, CategorizedStamp entry, CancellationToken cancellation)
    {
        if (entry.Connections is not { Points.Count: > 0 } connections)
            throw new InvalidDataException(LocalText.T("Save module connections before joining modules."));
        var cells = CellCodec.LoadStamp(StampCategories.Resolve(catalogue.ObjectRoot, entry.File), cancellation);
        ModuleConnectionRules.Validate(connections, cells);
        return new(entry.Name, cells, connections);
    }

    internal static ModuleJoinPlan Preview(MapDocument source, JoinModule first, string firstId,
        JoinModule second, string secondId, int x, int y, CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        long revision = source.PickupRevision;
        // Own the input data: changing a loaded catalogue/module must not alter an already approved preview.
        JoinModule Copy(JoinModule module)
        {
            ModuleConnectionRules.Validate(module.Connections, module.Cells);
            if (string.IsNullOrWhiteSpace(module.Name) || module.Name.Length > 4096)
                throw new InvalidDataException(LocalText.T("Invalid module name."));
            return new(module.Name, module.Cells.Where(c => !c.Cell.Empty).Select(c => c with { Cell = c.Cell.Copy() }).ToArray(),
                module.Connections with { Points = module.Connections.Points.ToList() });
        }
        first = Copy(first); second = Copy(second);
        var a = first.Connections.Points.SingleOrDefault(p => p.Id == firstId);
        var b = second.Connections.Points.SingleOrDefault(p => p.Id == secondId);
        if (a == null || b == null || !Matches(a, b))
            throw new InvalidDataException(LocalText.T("Choose opposite connections with the same type and width."));
        var (dx, dy) = a.Edge switch
        { ModuleEdge.North => (0, -1), ModuleEdge.East => (1, 0), ModuleEdge.South => (0, 1), _ => (-1, 0) };
        int bx, by;
        try { bx = checked(x + a.X + dx - b.X); by = checked(y + a.Y + dy - b.Y); }
        catch (OverflowException) { throw new InvalidDataException(LocalText.T("Joined modules extend outside the map.")); }
        ModuleJoinPlacement[] placements = [new(first, x, y), new(second, bx, by)];
        var bounds = placements.Select(p => WorldBounds(p, source)).ToArray();
        if (bounds[0].Left <= bounds[1].Right && bounds[0].Right >= bounds[1].Left &&
            bounds[0].Top <= bounds[1].Bottom && bounds[0].Bottom >= bounds[1].Top)
            throw new InvalidDataException(LocalText.T("Module footprints overlap."));
        if (first.Cells.Count + (long)second.Cells.Count > 1_000_000 ||
            bounds.Sum(r => ((long)r.Right - r.Left + 1) * (r.Bottom - r.Top + 1)) > 1_000_000)
            throw new InvalidDataException(LocalText.T("Preview at most 1,000,000 module cells at once."));
        if (source.PlacedStamps.Count > 99998)
            throw new InvalidDataException(LocalText.T("Too many stamp instances."));
        foreach (var placement in placements)
        {
            foreach (var cell in placement.Module.Cells)
                if ((cell.Cell.BackImage & 0x1FFFFFFF) != 0 &&
                    (((cell.X + placement.X) & 1) != 0 || ((cell.Y + placement.Y) & 1) != 0))
                    throw new InvalidDataException(LocalText.T("The connection would misalign ground tiles. Use even ground coordinates and compatible module sizes."));
        }
        foreach (var area in bounds)
        {
            for (int row = area.Top; row <= area.Bottom; row++)
            {
                cancellation.ThrowIfCancellationRequested();
                for (int column = area.Left; column <= area.Right; column++)
                    if (!source.Get(column, row).Empty)
                        throw new InvalidDataException(LocalText.Format("Module footprint contains existing content at ({0}, {1}). Choose an empty area.", column, row));
            }
        }
        // Build on a separate document. The live map and its undo history stay untouched until Apply.
        var generated = MapRecoveryStore.Capture(source);
        var history = new EditHistory();
        foreach (var placement in placements)
        {
            cancellation.ThrowIfCancellationRequested();
            if (generated.PlaceStamp(placement.Module.Cells, placement.X, placement.Y, placement.Module.Name, history, (_, _) => { }) == null)
                throw new InvalidDataException(LocalText.T("Module placement failed."));
        }
        var openings = new[] { a with { X = checked(a.X + x), Y = checked(a.Y + y) }, b with { X = checked(b.X + bx), Y = checked(b.Y + by) } };
        // Check the resulting map too, including sparse cells at the seam.
        foreach (var opening in openings)
        {
            var (ix, iy) = opening.Edge switch
            { ModuleEdge.North => (0, 1), ModuleEdge.East => (-1, 0), ModuleEdge.South => (0, -1), _ => (1, 0) };
            foreach (var (cx, cy) in ModuleConnectionRules.Opening(opening))
                foreach (var cell in new[] { generated.Get((int)cx, (int)cy), generated.Get((int)cx + ix, (int)cy + iy) })
                    if ((cell.BackImage & 0x60000000) != 0 || cell.Foreground is { } front && (front.Image & 0x8000) != 0)
                        throw new InvalidDataException(LocalText.T("The joined passage is blocked."));
        }
        cancellation.ThrowIfCancellationRequested();
        if (source.PickupRevision != revision)
            throw new InvalidOperationException(LocalText.T("The map changed after the module preview. Generate a fresh preview."));
        var cells = placements.SelectMany(p => p.Module.Cells.Select(c => new StampCell(c.X + p.X, c.Y + p.Y, generated.Get(c.X + p.X, c.Y + p.Y).Copy()))).ToArray();
        return new(source, revision, generated, placements, cells, openings,
            new(bounds.Min(r => r.Left), bounds.Min(r => r.Top), bounds.Max(r => r.Right), bounds.Max(r => r.Bottom)));
    }

    private static ModuleConnectionRules.Bounds WorldBounds(ModuleJoinPlacement placement, MapDocument source)
    {
        var area = ModuleConnectionRules.GetBounds(placement.Module.Cells, placement.Module.Connections);
        long left = (long)area.Left + placement.X, top = (long)area.Top + placement.Y,
            right = (long)area.Right + placement.X, bottom = (long)area.Bottom + placement.Y;
        if (left < 0 || top < 0 || right >= source.Width || bottom >= source.Height)
            throw new InvalidDataException(LocalText.T("Joined modules extend outside the map."));
        return new((int)left, (int)top, (int)right, (int)bottom);
    }
}
