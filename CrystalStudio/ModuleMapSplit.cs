using System.IO;

namespace CrystalStudio;

internal sealed record ModuleSplitPart(int Index, ModuleArea Area,
    ModuleCaptureDraft? Draft, ModuleProblem? Problem)
{
    internal bool Valid => Draft != null && Problem == null;
}
internal sealed record ModuleSplitResult(MapDocument Source, long Revision, IReadOnlyList<ModuleSplitPart> Parts);

internal static class ModuleMapSplit
{
    internal static ModuleSplitResult Split(MapDocument source, int width, int height, CancellationToken token = default)
    {
        if (width is < 4 or > 512 || height is < 4 or > 512 || (width & 1) != 0 || (height & 1) != 0)
            throw new InvalidDataException(LocalText.T("Use even block dimensions from 4 to 512 cells."));
        if (source.Width < 2 || source.Height < 2 || (long)source.Width * source.Height > 4_000_000)
            throw new InvalidDataException(LocalText.T("Split maps of at least 2 × 2 and at most 4,000,000 cells."));
        var columns = Segments(source.Width, width); var rows = Segments(source.Height, height);
        if ((long)columns.Count * rows.Count > 256)
            throw new InvalidDataException(LocalText.T("The split would exceed 256 blocks. Increase the block dimensions."));
        var areas = new List<ModuleArea>();
        for (int row = 0; row < rows.Count; row++)
            for (int column = 0; column < columns.Count; column++)
                areas.Add(new(columns[column].Start, rows[row].Start, columns[column].Length, rows[row].Length));
        return Build(source, areas, token);
    }

    internal static ModuleSplitResult Build(MapDocument source, IReadOnlyList<ModuleArea> areas, CancellationToken token = default)
    {
        ValidatePartition(source, areas);
        long revision = source.PickupRevision;
        var parts = new List<ModuleSplitPart>();
        foreach (var area in areas)
        {
            token.ThrowIfCancellationRequested();
            bool content = false;
            for (int y = area.Y; y < area.Y + area.Height && !content; y++)
                for (int x = area.X; x < area.X + area.Width; x++) if (!source.Get(x, y).Empty) { content = true; break; }
            bool large = area.Width > 4096 || area.Height > 4096 || (long)area.Width * area.Height > 1_000_000;
            var draft = content && !large ? ModuleCapture.Capture(source, area, token) : null;
            if (draft != null) draft = draft with { Connections = draft.Connections with { Points = [] } };
            parts.Add(new(parts.Count, area, draft,
                large ? new("split-large", LocalText.T("This block is too large. Add cuts before saving.")) : content ? null : new("split-empty", LocalText.T("Empty block; excluded from the module pool."))));
        }

        // Derive both sides of a seam together. A passable opening must include the inward cells on both sides.
        var seams = new List<(int A, string APort, int B, string BPort)>();
        void Connect(ModuleSplitPart a, ModuleSplitPart b, bool horizontal)
        {
            if (a.Draft == null || b.Draft == null) return;
            var af = a.Draft.Connections.Footprint!; var bf = b.Draft.Connections.Footprint!;
            foreach (var (first, second) in SharedOpenings(source, a.Area, b.Area, horizontal, token))
            {
                int n = horizontal ? first.Y : first.X;
                var pa = first with { Id = $"{first.Edge.ToString().ToLowerInvariant()}-{b.Index}-{n}", X = first.X - a.Area.X + af.X, Y = first.Y - a.Area.Y + af.Y };
                var pb = second with { Id = $"{second.Edge.ToString().ToLowerInvariant()}-{a.Index}-{n}", X = second.X - b.Area.X + bf.X, Y = second.Y - b.Area.Y + bf.Y };
                a.Draft.Connections.Points.Add(pa); b.Draft.Connections.Points.Add(pb);
                seams.Add((a.Index, pa.Id, b.Index, pb.Id));
            }
        }
        foreach (var a in parts)
            foreach (var b in parts)
            {
                if (a.Area.X + a.Area.Width == b.Area.X) Connect(a, b, true);
                if (a.Area.Y + a.Area.Height == b.Area.Y) Connect(a, b, false);
            }
        for (int i = 0; i < parts.Count; i++)
        {
            token.ThrowIfCancellationRequested();
            var part = parts[i]; if (part.Draft == null) continue;
            if (part.Draft.Connections.Points.Count == 0)
                parts[i] = part with { Problem = NoSeam() };
            else
            {
                try { ModuleGeneration.ValidateModule(new($"#{i + 1}", part.Draft.Cells, part.Draft.Connections), token); }
                catch (InvalidDataException error)
                {
                    var problem = ModuleDiagnostics.Read(error) ?? new("split-invalid", error.Message);
                    var footprint = part.Draft.Connections.Footprint!;
                    parts[i] = part with { Problem = problem with { ModuleId = $"#{i + 1}",
                        X = problem.X.HasValue ? problem.X - footprint.X + part.Area.X : part.Area.X,
                        Y = problem.Y.HasValue ? problem.Y - footprint.Y + part.Area.Y : part.Area.Y } };
                }
            }
        }
        // Invalid neighbours must not leave apparently paired interfaces on otherwise valid blocks.
        foreach (var seam in seams)
            if (!parts[seam.A].Valid || !parts[seam.B].Valid)
            {
                parts[seam.A].Draft!.Connections.Points.RemoveAll(p => p.Id == seam.APort);
                parts[seam.B].Draft!.Connections.Points.RemoveAll(p => p.Id == seam.BPort);
            }
        for (int i = 0; i < parts.Count; i++)
            if (parts[i].Valid && parts[i].Draft!.Connections.Points.Count == 0)
                parts[i] = parts[i] with { Problem = NoSeam() };
        token.ThrowIfCancellationRequested();
        if (source.PickupRevision != revision)
            throw new InvalidOperationException(LocalText.T("The map changed after the module preview. Generate a fresh preview."));
        for (int i = 0; i < parts.Count; i++)
            if (parts[i].Problem is { } problem)
                parts[i] = parts[i] with { Problem = problem with { ModuleId = $"#{i + 1}", X = problem.X ?? parts[i].Area.X, Y = problem.Y ?? parts[i].Area.Y } };
        return new(source, revision, parts);
    }

    // World-space pairs serve both partition validation and the interactive cut overlay.
    internal static IReadOnlyList<(ModuleConnection A, ModuleConnection B)> SharedOpenings(MapDocument source,
        ModuleArea a, ModuleArea b, bool vertical, CancellationToken token = default)
    {
        var result = new List<(ModuleConnection, ModuleConnection)>();
        int first = vertical ? Math.Max(a.Y, b.Y) + 1 : Math.Max(a.X, b.X) + 1;
        int end = vertical ? Math.Min(a.Y + a.Height, b.Y + b.Height) - 1 : Math.Min(a.X + a.Width, b.X + b.Width) - 1;
        int start = -1;
        bool Open(int x, int y) => source.Contains(x, y) && !ModuleTerrain.Blocked(source.Get(x, y));
        void Flush(int until)
        {
            if (start < 0) return;
            for (int n = start; n < until; n += 64)
            {
                int width = Math.Min(64, until - n), number = result.Count + 1;
                result.Add(vertical
                    ? (new($"A{number}", b.X - 1, n, ModuleEdge.East, "path", width), new($"B{number}", b.X, n, ModuleEdge.West, "path", width))
                    : (new($"A{number}", n, b.Y - 1, ModuleEdge.South, "path", width), new($"B{number}", n, b.Y, ModuleEdge.North, "path", width)));
            }
            start = -1;
        }
        for (int n = first; n < end; n++)
        {
            token.ThrowIfCancellationRequested();
            bool open = vertical ? Open(b.X - 2, n) && Open(b.X - 1, n) && Open(b.X, n) && Open(b.X + 1, n)
                : Open(n, b.Y - 2) && Open(n, b.Y - 1) && Open(n, b.Y) && Open(n, b.Y + 1);
            if (open) { if (start < 0) start = n; } else Flush(n);
        }
        Flush(end); return result;
    }

    internal static void ValidatePartition(MapDocument source, IReadOnlyList<ModuleArea> areas)
    {
        if (source.Width < 2 || source.Height < 2 || (long)source.Width * source.Height > 4_000_000)
            throw new InvalidDataException(LocalText.T("Split maps of at least 2 × 2 and at most 4,000,000 cells."));
        if (areas.Count is < 1 or > 256)
            throw new InvalidDataException(LocalText.T("The split would exceed 256 blocks. Increase the block dimensions."));
        long covered = 0;
        for (int i = 0; i < areas.Count; i++)
        {
            var a = areas[i];
            if (a.X < 0 || a.Y < 0 || a.Width < 2 || a.Height < 2 || (long)a.X + a.Width > source.Width || (long)a.Y + a.Height > source.Height || (a.X & 1) != 0 || (a.Y & 1) != 0)
                throw new InvalidDataException(LocalText.T("Cuts must preserve the two-cell ground grid and leave at least two cells on each side."));
            covered += (long)a.Width * a.Height;
            for (int j = 0; j < i; j++) if (ModulePartitions.Intersects(a, areas[j]))
                throw new InvalidDataException(LocalText.T("Module footprints overlap."));
        }
        if (covered != (long)source.Width * source.Height)
            throw new InvalidDataException(LocalText.T("The partition must cover the entire map without gaps."));
    }

    private static ModuleProblem NoSeam() => new("split-no-seam", LocalText.T("No usable connection to another valid block. Adjust the block dimensions."));
    private static List<(int Start, int Length)> Segments(int total, int size)
    {
        var segments = new List<(int Start, int Length)>();
        for (int start = 0; start < total;)
        {
            int length = Math.Min(size, total - start);
            if (total - start - length == 1) length++;
            segments.Add((start, length)); start += length;
        }
        return segments;
    }
}
