using System.Security.Cryptography;
using System.Buffers.Binary;

namespace CrystalStudio;

internal sealed record MapDecorationVariant(MapDocument Generated, int Seed, int Movable, int Moved, IReadOnlyList<int> Changed, string StructureFingerprint);

internal static class MapDecorationVariation
{
    internal static bool SameStructure(Cell first, Cell second) => first.BackLibrary == second.BackLibrary && first.BackImage == second.BackImage &&
        first.TileAnimation == second.TileAnimation && first.TileOffset == second.TileOffset && first.TileFrames == second.TileFrames &&
        first.Door == second.Door && first.DoorOffset == second.DoorOffset && first.Light == second.Light;

    internal static string Fingerprint(MapDocument map, CancellationToken cancellation = default)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Span<byte> data = stackalloc byte[20];
        BinaryPrimitives.WriteInt32LittleEndian(data, map.Width); BinaryPrimitives.WriteInt32LittleEndian(data[4..], map.Height);
        BinaryPrimitives.WriteInt32LittleEndian(data[8..], (int)map.SourceFormat); hash.AppendData(data[..12]);
        for (int row = 0; row < map.Height; row++) for (int column = 0; column < map.Width; column++)
        {
            if ((column & 1023) == 0) cancellation.ThrowIfCancellationRequested();
            var cell = map.Get(column, row); data.Clear();
            BinaryPrimitives.WriteInt32LittleEndian(data, cell.BackLibrary); BinaryPrimitives.WriteInt32LittleEndian(data[4..], cell.BackImage);
            BinaryPrimitives.WriteInt16LittleEndian(data[8..], cell.TileAnimation); BinaryPrimitives.WriteInt16LittleEndian(data[10..], cell.TileOffset);
            data[12] = cell.TileFrames; data[13] = cell.Door; data[14] = cell.DoorOffset; data[15] = cell.Light; hash.AppendData(data[..16]);
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    internal static MapDecorationVariant Create(MapDocument baseline, MapDesignSettings design, int seed, CancellationToken cancellation = default, IReadOnlySet<string>? allowedNames = null)
    {
        design.Validate(baseline.Width, baseline.Height);
        string fingerprint = Fingerprint(baseline, cancellation);
        var output = MapRecoveryStore.Capture(baseline); var occupied = new Dictionary<int, int>();
        foreach (var stamp in baseline.PlacedStamps)
            foreach (int index in Indices(stamp).Distinct()) occupied[index] = occupied.GetValueOrDefault(index) + 1;
        var stamps = baseline.PlacedStamps.ToList(); var changed = new HashSet<int>(); int movable = 0, moved = 0;
        for (int stampIndex = 0; stampIndex < stamps.Count; stampIndex++)
        {
            cancellation.ThrowIfCancellationRequested(); var stamp = stamps[stampIndex];
            if (allowedNames != null && !allowedNames.Contains(stamp.Name) || !Eligible(stamp)) continue;
            movable++; if (moved >= 256) continue;
            var originalIndices = Indices(stamp).Distinct().ToArray();
            foreach (int index in originalIndices) occupied.Remove(index);
            foreach (var delta in stamp.Changes) output.Set(stamp.X + delta.X, stamp.Y + delta.Y, delta.Before.Copy());
            bool placed = false;
            int minX = stamp.Changes.Min(delta => delta.X), minY = stamp.Changes.Min(delta => delta.Y);
            int maxX = stamp.Changes.Max(delta => delta.X), maxY = stamp.Changes.Max(delta => delta.Y);
            int columns = baseline.Width - (maxX - minX), rows = baseline.Height - (maxY - minY);
            for (int attempt = 0; attempt < 64 && columns > 0 && rows > 0; attempt++)
            {
                cancellation.ThrowIfCancellationRequested();
                int column = GenerationRandom.Index(seed, 7301, stampIndex, attempt, columns) - minX;
                int row = GenerationRandom.Index(seed, 7302, stampIndex, attempt, rows) - minY;
                if (column == stamp.X && row == stamp.Y || stamp.Changes.Any(delta => !Free(column + delta.X, row + delta.Y, delta.After))) continue;
                var deltas = new List<StampDelta>();
                foreach (var delta in stamp.Changes)
                {
                    int targetX = column + delta.X, targetY = row + delta.Y;
                    var before = output.Get(targetX, targetY).Copy(); var after = before.Copy();
                    after.MiddleLibrary = delta.After.MiddleLibrary; after.MiddleImage = delta.After.MiddleImage;
                    after.MiddleFrames = delta.After.MiddleFrames; after.MiddleTick = delta.After.MiddleTick; after.Foreground = delta.After.Foreground;
                    deltas.Add(delta with { Before = before, After = after.Copy() }); output.Set(targetX, targetY, after);
                    int index = targetY * output.Width + targetX; occupied[index] = 1; changed.Add(index);
                }
                stamps[stampIndex] = stamp with { X = column, Y = row, Changes = deltas };
                foreach (int index in originalIndices) changed.Add(index);
                moved++; placed = true; break;
            }
            if (placed) continue;
            foreach (var delta in stamp.Changes) output.Set(stamp.X + delta.X, stamp.Y + delta.Y, delta.After.Copy());
            foreach (int index in originalIndices) occupied[index] = 1;
        }
        output.RestoreStamps(stamps);
        if (Fingerprint(output, cancellation) != fingerprint) throw new InvalidOperationException(LocalText.T("Structure verification failed. The decoration variant was rejected."));
        var check = MapDesignConstraints.Check(output, design, cancellation);
        if (!check.Accepted) throw GenerationDiagnostics.Reject(GenerationFailureKind.SeedRejected, "Decoration variation", LocalText.T("The decoration variant failed required layout checks."), check.Findings);
        return new(output, seed, movable, moved, changed.Order().ToArray(), fingerprint);

        IEnumerable<int> Indices(PlacedStamp stamp) => stamp.Cells.Select(cell => (stamp.Y + cell.Y) * baseline.Width + stamp.X + cell.X)
            .Concat(stamp.Changes.Select(delta => (stamp.Y + delta.Y) * baseline.Width + stamp.X + delta.X));
        bool Protected(int column, int row) => design.Regions.Any(region => region.Contains(column, row)) || design.Points.Any(point =>
            column >= point.X - point.ClearRadius - 1 && column < point.X + design.FootprintSize + point.ClearRadius + 1 &&
            row >= point.Y - point.ClearRadius - 1 && row < point.Y + design.FootprintSize + point.ClearRadius + 1);
        bool Eligible(PlacedStamp stamp) => stamp.Wall == null && stamp.GeneratedBanks == null && stamp.Changes.Count is > 0 and <= 4096 &&
            stamp.Changes.Select(delta => (delta.X, delta.Y)).Distinct().Count() == stamp.Changes.Count &&
            stamp.Cells.All(cell => stamp.Changes.Any(delta => delta.X == cell.X && delta.Y == cell.Y)) &&
            stamp.Changes.All(delta => baseline.Contains(stamp.X + delta.X, stamp.Y + delta.Y) && !Protected(stamp.X + delta.X, stamp.Y + delta.Y) &&
                occupied.GetValueOrDefault((stamp.Y + delta.Y) * baseline.Width + stamp.X + delta.X) == 1 && SameStructure(delta.Before, delta.After) &&
                (delta.After.BackImage & 0x60000000) == 0 && !delta.Before.HasForeground && delta.Before.MiddleImage == 0 &&
                baseline.Get(stamp.X + delta.X, stamp.Y + delta.Y).SameContents(delta.After)) &&
            stamp.Changes.Any(delta => delta.After.HasForeground || delta.After.MiddleImage != 0);
        bool Free(int column, int row, Cell native) => output.Contains(column, row) && !Protected(column, row) && !occupied.ContainsKey(row * output.Width + column) &&
            SameStructure(output.Get(column, row), native) && !output.Get(column, row).HasForeground && output.Get(column, row).MiddleImage == 0;
    }
}
