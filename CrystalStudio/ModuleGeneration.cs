using System.IO;

namespace CrystalStudio;

internal enum ModuleLayout { Compact, Branching, Chain }
internal readonly record struct ModuleArea(int X, int Y, int Width, int Height);
internal sealed record ModuleGenerationOptions(int Count, int Seed, ModuleArea Area, ModuleLayout Layout, int SearchLimit = 100_000);
internal sealed record ModuleLink(int First, int FirstPort, int Second, int SecondPort);
internal sealed record ModuleGenerationPlan(ModuleJoinPlan Placement, ModuleGenerationOptions Options,
    IReadOnlyList<ModuleLink> Links, int WalkableCells, int SearchSteps);

internal static class ModuleGeneration
{
    private sealed record Definition(JoinModule Module, ModuleConnectionRules.Bounds Bounds, int WalkableCount, int? GroundX, int? GroundY);
    private sealed record Position(int Definition, int X, int Y, ModuleConnectionRules.Bounds Bounds, int Depth);
    private sealed class SearchExhausted : Exception;
    private sealed class RootExhausted : Exception;

    internal static IReadOnlyList<JoinModule> LoadPool(StampCategoryIndex catalogue, IReadOnlyList<CategorizedStamp> entries, CancellationToken cancellation = default)
    {
        if (entries.Count is < 1 or > 256)
            throw new InvalidDataException(LocalText.T("Select between 1 and 256 modules for automatic generation."));
        var modules = new List<JoinModule>();
        long total = 0;
        foreach (var entry in entries)
        {
            cancellation.ThrowIfCancellationRequested();
            try
            {
                var module = ModuleJoining.Load(catalogue, entry, cancellation);
                var bounds = ModuleConnectionRules.GetBounds(module.Cells, module.Connections);
                total += ((long)bounds.Right - bounds.Left + 1) * (bounds.Bottom - bounds.Top + 1);
                if (total > 4_000_000)
                    throw new InvalidDataException(LocalText.T("The selected module pool is too large. Select fewer modules."));
                ValidateModule(module, cancellation);
                modules.Add(module);
            }
            catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException)
            { ModuleDiagnostics.Identify(exception, entry.Id); throw new InvalidDataException(LocalText.Format("Module {0}: {1}", entry.Name, exception.Message), exception); }
        }
        return modules;
    }

    internal static ModuleGenerationPlan Generate(MapDocument source, IReadOnlyList<JoinModule> modules,
        ModuleGenerationOptions options, CancellationToken cancellation = default)
    {
        var area = options.Area;
        if (options.Count is < 2 or > 128 || !Enum.IsDefined(options.Layout) || options.SearchLimit is < 1 or > 200_000 ||
            area.X < 0 || area.Y < 0 || area.Width < 2 || area.Height < 2 ||
            (long)area.X + area.Width > source.Width || (long)area.Y + area.Height > source.Height ||
            (long)area.Width * area.Height > 1_000_000)
            throw new InvalidDataException(LocalText.T("Choose 2–128 modules and a valid generation area of at most 1,000,000 cells."));
        if (modules.Count is < 1 or > 256)
            throw new InvalidDataException(LocalText.T("Select between 1 and 256 modules for automatic generation."));
        if (source.PlacedStamps.Count + options.Count > 100_000)
            throw new InvalidDataException(LocalText.T("Too many stamp instances."));
        cancellation.ThrowIfCancellationRequested();
        long sourceRevision = source.PickupRevision;
        long poolArea = 0;
        var definitions = modules.Select(module =>
        {
            var bounds = ModuleConnectionRules.GetBounds(module.Cells, module.Connections);
            poolArea += ((long)bounds.Right - bounds.Left + 1) * (bounds.Bottom - bounds.Top + 1);
            if (poolArea > 4_000_000)
                throw new InvalidDataException(LocalText.T("The selected module pool is too large. Select fewer modules."));
            return Prepare(module, cancellation);
        }).ToArray();

        // A summed-area table makes checking existing content independent of module size.
        int stride = area.Width + 1;
        var occupied = new int[checked(stride * (area.Height + 1))];
        for (int row = 0; row < area.Height; row++)
        {
            cancellation.ThrowIfCancellationRequested();
            int sum = 0;
            for (int column = 0; column < area.Width; column++)
            {
                if (!source.Get(area.X + column, area.Y + row).Empty) sum++;
                occupied[(row + 1) * stride + column + 1] = occupied[row * stride + column + 1] + sum;
            }
        }
        var compatible = new Dictionary<(ModuleEdge Edge, string Kind, int Width), List<(int Module, int Port)>>();
        for (int m = 0; m < definitions.Length; m++)
            for (int p = 0; p < definitions[m].Module.Connections.Points.Count; p++)
            {
                var port = definitions[m].Module.Connections.Points[p];
                var key = (port.Edge, port.Kind.ToUpperInvariant(), port.Width);
                if (!compatible.TryGetValue(key, out var list)) compatible[key] = list = [];
                list.Add((m, p));
            }
        var random = new Random(options.Seed);
        var positions = new List<Position>();
        var used = new HashSet<(int Module, int Port)>();
        var links = new List<ModuleLink>();
        int steps = 0, best = 0, rootStart = 0;
        int rootLimit = Math.Min(options.SearchLimit, Math.Max(options.Count * 32, options.SearchLimit / 32));
        void Step()
        {
            cancellation.ThrowIfCancellationRequested();
            if (++steps > options.SearchLimit) throw new SearchExhausted();
        }
        bool Fits(int module, long x, long y, int depth, out Position position)
        {
            Step(); position = null!;
            var definition = definitions[module]; var bounds = definition.Bounds;
            long left = x + bounds.Left, top = y + bounds.Top, right = x + bounds.Right, bottom = y + bounds.Bottom;
            if (x < int.MinValue || x > int.MaxValue || y < int.MinValue || y > int.MaxValue ||
                left < area.X || top < area.Y || right >= (long)area.X + area.Width || bottom >= (long)area.Y + area.Height)
                return false;
            if (definition.GroundX is int gx && ((x & 1) != gx || (y & 1) != definition.GroundY)) return false;
            int l = (int)left - area.X, t = (int)top - area.Y, r = (int)right - area.X + 1, b = (int)bottom - area.Y + 1;
            if (occupied[b * stride + r] - occupied[t * stride + r] - occupied[b * stride + l] + occupied[t * stride + l] != 0) return false;
            if (positions.Any(p => left <= p.Bounds.Right && right >= p.Bounds.Left && top <= p.Bounds.Bottom && bottom >= p.Bounds.Top)) return false;
            position = new(module, (int)x, (int)y, new((int)left, (int)top, (int)right, (int)bottom), depth);
            return true;
        }
        IEnumerable<(int Parent, int Port, int Module, int OtherPort, long X, long Y)> Candidates()
        {
            var frontier = positions.SelectMany((position, i) => definitions[position.Definition].Module.Connections.Points
                .Select((port, p) => (Instance: i, Port: p, Definition: port)))
                .Where(p => !used.Contains((p.Instance, p.Port)) && (options.Layout != ModuleLayout.Chain || p.Instance == positions.Count - 1)).ToArray();
            random.Shuffle(frontier);
            IEnumerable<(int Instance, int Port, ModuleConnection Definition)> ordered = frontier;
            if (options.Layout == ModuleLayout.Branching) ordered = ordered.OrderBy(p => positions[p.Instance].Depth);
            if (options.Layout == ModuleLayout.Compact)
            {
                double cx = positions.Average(p => (p.Bounds.Left + p.Bounds.Right) / 2.0);
                double cy = positions.Average(p => (p.Bounds.Top + p.Bounds.Bottom) / 2.0);
                ordered = ordered.OrderBy(p => Math.Abs(positions[p.Instance].X + (double)p.Definition.X - cx) +
                    Math.Abs(positions[p.Instance].Y + (double)p.Definition.Y - cy));
            }
            foreach (var port in ordered)
            {
                cancellation.ThrowIfCancellationRequested();
                var key = ((ModuleEdge)(((int)port.Definition.Edge + 2) % 4), port.Definition.Kind.ToUpperInvariant(), port.Definition.Width);
                if (!compatible.TryGetValue(key, out var matches)) continue;
                var choices = matches.ToArray(); random.Shuffle(choices);
                var parent = positions[port.Instance];
                var (dx, dy) = Outward(port.Definition.Edge);
                long openingX = (long)parent.X + port.Definition.X + dx, openingY = (long)parent.Y + port.Definition.Y + dy;
                // Every matching module must own this first opening cell. An occupied or out-of-area cell
                // rules out the entire frontier, without spending search budget on each module variant.
                if (openingX < area.X || openingY < area.Y || openingX >= (long)area.X + area.Width || openingY >= (long)area.Y + area.Height ||
                    positions.Any(p => openingX >= p.Bounds.Left && openingX <= p.Bounds.Right && openingY >= p.Bounds.Top && openingY <= p.Bounds.Bottom) ||
                    !source.Get((int)openingX, (int)openingY).Empty) continue;
                foreach (var match in choices)
                {
                    var other = definitions[match.Module].Module.Connections.Points[match.Port];
                    yield return (port.Instance, port.Port, match.Module, match.Port,
                        (long)parent.X + port.Definition.X + dx - other.X, (long)parent.Y + port.Definition.Y + dy - other.Y);
                }
            }
        }
        bool Search()
        {
            best = Math.Max(best, positions.Count);
            if (positions.Count == options.Count) return true;
            if (steps - rootStart >= rootLimit) throw new RootExhausted();
            foreach (var candidate in Candidates())
            {
                if (!Fits(candidate.Module, candidate.X, candidate.Y, positions[candidate.Parent].Depth + 1, out var next)) continue;
                int index = positions.Count;
                positions.Add(next); links.Add(new(candidate.Parent, candidate.Port, index, candidate.OtherPort));
                used.Add((candidate.Parent, candidate.Port)); used.Add((index, candidate.OtherPort));
                if (Search()) return true;
                used.Remove((index, candidate.OtherPort)); used.Remove((candidate.Parent, candidate.Port));
                links.RemoveAt(links.Count - 1); positions.RemoveAt(index);
            }
            return false;
        }
        bool success = false;
        try
        {
            var roots = Enumerable.Range(0, definitions.Length).ToArray(); random.Shuffle(roots);
            var starts = roots.Select(root =>
            {
                var bounds = definitions[root].Bounds;
                int columns = area.Width - (bounds.Right - bounds.Left + 1) + 1, rows = area.Height - (bounds.Bottom - bounds.Top + 1) + 1;
                int total = columns <= 0 || rows <= 0 ? 0 : checked(columns * rows);
                return (Root: root, Bounds: bounds, Columns: columns, Rows: rows, Total: total, Start: total > 0 ? random.Next(total) : 0);
            }).Where(s => s.Total > 0).ToArray();
            // Alternate root definitions; retry from the corner and other seeded origins when a central layout gets stuck.
            int rootOrigins = starts.Select(s => s.Total).DefaultIfEmpty(0).Max();
            for (int n = -2; n < rootOrigins && !success; n++)
                foreach (var start in starts)
                {
                    if (n >= start.Total) continue;
                    int center = start.Rows / 2 * start.Columns + start.Columns / 2;
                    int cell = n == -2 ? center : n == -1 ? 0 : (start.Start + n) % start.Total;
                    if (n >= 0 && (cell == center || cell == 0)) continue;
                    if (!Fits(start.Root, (long)area.X + cell % start.Columns - start.Bounds.Left, (long)area.Y + cell / start.Columns - start.Bounds.Top, 0, out var position)) continue;
                    positions.Add(position);
                    rootStart = steps;
                    try { if (Search()) { success = true; break; } }
                    catch (RootExhausted) { }
                    positions.Clear(); used.Clear(); links.Clear();
                }
        }
        catch (SearchExhausted) { }
        if (!success)
            throw new InvalidDataException(LocalText.Format("Could not generate {0} connected modules (best attempt: {1}). Try a larger area, fewer modules, another seed or more matching modules.", options.Count, best));

        cancellation.ThrowIfCancellationRequested();
        var generated = MapRecoveryStore.Capture(source);
        var history = new EditHistory();
        var placements = positions.Select(p => new ModuleJoinPlacement(definitions[p.Definition].Module, p.X, p.Y)).ToArray();
        foreach (var placement in placements)
        {
            cancellation.ThrowIfCancellationRequested();
            if (generated.PlaceStamp(placement.Module.Cells, placement.X, placement.Y, placement.Module.Name, history, (_, _) => { }) == null)
                throw new InvalidDataException(LocalText.T("Module placement failed."));
        }
        // Verify actual four-neighbour reachability over only the generated footprints, never outside blank map cells.
        int walkable = VerifyConnectivity(generated, placements, area, cancellation);
        if (walkable != positions.Sum(p => definitions[p.Definition].WalkableCount))
            throw new InvalidDataException(LocalText.T("The generated modules are not fully connected."));
        var cells = placements.SelectMany(p => p.Module.Cells.Select(c => new StampCell(c.X + p.X, c.Y + p.Y, generated.Get(c.X + p.X, c.Y + p.Y).Copy()))).ToArray();
        var openings = links.SelectMany(link => new[] { (link.First, link.FirstPort), (link.Second, link.SecondPort) })
            .Select(pair =>
            {
                var placement = placements[pair.Item1]; var point = placement.Module.Connections.Points[pair.Item2];
                return point with { X = checked(point.X + placement.X), Y = checked(point.Y + placement.Y) };
            }).ToArray();
        if (source.PickupRevision != sourceRevision)
            throw new InvalidOperationException(LocalText.T("The map changed after the module preview. Generate a fresh preview."));
        var joined = new ModuleJoinPlan(source, sourceRevision, generated, placements, cells, openings,
            new(positions.Min(p => p.Bounds.Left), positions.Min(p => p.Bounds.Top), positions.Max(p => p.Bounds.Right), positions.Max(p => p.Bounds.Bottom)));
        return new(joined, options, links.ToArray(), walkable, Math.Min(steps, options.SearchLimit));
    }

    internal static void ValidateModule(JoinModule module, CancellationToken cancellation = default) => Prepare(module, cancellation);

    private static Definition Prepare(JoinModule module, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        try
        {
            ModuleConnectionRules.Validate(module.Connections, module.Cells);
            if (module.Connections.Points.Count == 0 || string.IsNullOrWhiteSpace(module.Name) || module.Name.Length > 4096)
                throw new InvalidDataException(LocalText.T("Save module connections before joining modules."));
            var bounds = ModuleConnectionRules.GetBounds(module.Cells, module.Connections);
            int width = bounds.Right - bounds.Left + 1, height = bounds.Bottom - bounds.Top + 1;
            if ((long)width * height > 1_000_000)
                throw new InvalidDataException(LocalText.T("Preview at most 1,000,000 module cells at once."));
            var mask = new byte[width * height]; Array.Fill(mask, (byte)1);
            int? gx = null, gy = null;
            foreach (var cell in module.Cells)
            {
                if (cell.Cell.Empty) continue;
                if (Blocked(cell.Cell)) mask[(cell.Y - bounds.Top) * width + cell.X - bounds.Left] = 0;
                if ((cell.Cell.BackImage & 0x1FFFFFFF) == 0) continue;
                if (gx.HasValue && (gx != (cell.X & 1) || gy != (cell.Y & 1)))
                    throw new InvalidDataException(LocalText.T("The connection would misalign ground tiles. Use even ground coordinates and compatible module sizes."));
                gx = cell.X & 1; gy = cell.Y & 1;
            }
            int walkable = mask.Count(value => value != 0);
            if (walkable == 0 || Flood(mask, width, cancellation) != walkable)
                throw ModuleDiagnostics.Error("connectivity", LocalText.T("All walkable cells and connections inside a module must be connected without leaving its footprint."));
            var copy = new JoinModule(module.Name, module.Cells.Where(c => !c.Cell.Empty).Select(c => c with { Cell = c.Cell.Copy() }).ToArray(),
                module.Connections with { Points = module.Connections.Points.ToList() });
            return new(copy, bounds, walkable, gx, gy);
        }
        catch (InvalidDataException exception)
        { throw new InvalidDataException(LocalText.Format("Module {0}: {1}", module.Name, exception.Message), exception); }
    }

    internal static int VerifyConnectivity(MapDocument generated, IReadOnlyList<ModuleJoinPlacement> placements, ModuleArea area, CancellationToken cancellation = default)
    {
        var mask = new byte[checked(area.Width * area.Height)];
        int count = 0;
        foreach (var placement in placements)
        {
            var bounds = ModuleConnectionRules.GetBounds(placement.Module.Cells, placement.Module.Connections);
            for (int row = 0; row <= bounds.Bottom - bounds.Top; row++)
            {
                cancellation.ThrowIfCancellationRequested();
                for (int column = 0; column <= bounds.Right - bounds.Left; column++)
                {
                    int wx = checked((int)((long)bounds.Left + column + placement.X)), wy = checked((int)((long)bounds.Top + row + placement.Y));
                    if (wx < area.X || wy < area.Y || (long)wx >= (long)area.X + area.Width || (long)wy >= (long)area.Y + area.Height)
                        throw new InvalidDataException(LocalText.T("Joined modules extend outside the map."));
                    int index = (wy - area.Y) * area.Width + wx - area.X;
                    if (!Blocked(generated.Get(wx, wy)) && mask[index] == 0) { mask[index] = 1; count++; }
                }
            }
        }
        if (count == 0 || Flood(mask, area.Width, cancellation) != count)
            throw new InvalidDataException(LocalText.T("The generated modules are not fully connected."));
        return count;
    }

    private static bool Blocked(Cell cell) => (cell.BackImage & 0x60000000) != 0 ||
        cell.Foreground is { } front && (front.Image & 0x8000) != 0;
    private static int Flood(byte[] mask, int width, CancellationToken cancellation)
    {
        int first = Array.IndexOf(mask, (byte)1);
        if (first < 0) return 0;
        var queue = new int[mask.Length]; queue[0] = first; mask[first] = 2;
        int read = 0, write = 1;
        void Visit(int index) { if (mask[index] == 1) { mask[index] = 2; queue[write++] = index; } }
        while (read < write)
        {
            if ((read & 4095) == 0) cancellation.ThrowIfCancellationRequested();
            int cell = queue[read++], x = cell % width;
            if (x > 0) Visit(cell - 1);
            if (x + 1 < width) Visit(cell + 1);
            if (cell >= width) Visit(cell - width);
            if (cell + width < mask.Length) Visit(cell + width);
        }
        return write;
    }
    private static (int X, int Y) Outward(ModuleEdge edge) => edge switch
    { ModuleEdge.North => (0, -1), ModuleEdge.East => (1, 0), ModuleEdge.South => (0, 1), _ => (-1, 0) };
}
