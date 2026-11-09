namespace CrystalStudio;

internal sealed record MapRouteLeg(MapRoutePoint From, MapRoutePoint To, bool Reachable, int Steps,
    int Manhattan, int NarrowCells, int DeadEnds, IReadOnlyList<int> Path)
{
    public int FootprintSize { get; init; } = 1;
    public int MinimumSquareClearance { get; init; }
    public override string ToString() => From.Name + " → " + To.Name + " · " + (Reachable
        ? LocalText.Format("{0:N0} steps · detour {1:0.00}× · narrow {2:N0} · dead ends {3:N0}", Steps,
            Manhattan == 0 ? 1 : (double)Steps / Manhattan, NarrowCells, DeadEnds)
        : LocalText.T("Unreachable")) + (FootprintSize > 1 ? LocalText.Format(" · footprint {0}×{0}", FootprintSize) : "")
        + (Reachable ? LocalText.Format(" · minimum square fit {0}×{0}", MinimumSquareClearance) : "");
}
internal sealed record MapRouteResult(IReadOnlyList<MapRouteLeg> Legs);
internal sealed record MapCandidateMetrics(int WalkableCells, int LargestArea, int TotalCells, int DeadEnds, int StampCount, int? Rooms = null)
{
    public int FootprintSize { get; init; } = 1;
    public int PossibleAnchors { get; init; } = TotalCells;
    public int? StartArea { get; init; }
    public double ReachableRatio => WalkableCells == 0 ? 0 : (double)(StartArea ?? LargestArea) / WalkableCells;
    public int? RouteSteps { get; init; }
    public double ReachableLegRatio { get; init; } = 1;
    public int MinimumRouteClearance { get; init; }
    public double MeanClearance { get; init; }
    public double MeanBranchDepth { get; init; }
    public int PreferredConflicts { get; init; }
    public string? TopologyFingerprint { get; init; }
    public double WalkableRatio => PossibleAnchors == 0 ? 0 : (double)WalkableCells / PossibleAnchors;
    public double ConnectedRatio => WalkableCells == 0 ? 0 : (double)LargestArea / WalkableCells;
    public override string ToString() => LocalText.Format("Walkable {0:P1} · largest / walkable {1:P1} · dead ends {2:N0} · stamps {3:N0}", WalkableRatio, ConnectedRatio, DeadEnds, StampCount)
        + (Rooms is { } rooms ? LocalText.Format(" · rooms {0}", rooms) : "")
        + LocalText.Format(" · footprint {0}×{0}", FootprintSize)
        + (StartArea != null ? LocalText.Format(" · from first point {0:P1}", ReachableRatio) : "")
        + (RouteSteps is { } steps ? LocalText.Format(" · route {0:N0} steps", steps) : "")
        + LocalText.Format(" · branch depth {0:0.0} · square fit {1:0.0}", MeanBranchDepth, MeanClearance);
}

internal static class MapRouteAnalysis
{
    internal const int MaximumCells = 4_000_000;
    internal static bool Walkable(MapDocument document, int column, int row) => document.Contains(column, row) && (document.Get(column, row).BackImage & 0x60000000) == 0;

    private static int CellCount(MapDocument document)
    {
        int count = checked(document.Width * document.Height);
        if (count > MaximumCells) throw new InvalidOperationException(LocalText.T("Route analysis supports up to 4,000,000 cells. Analyze a smaller map."));
        return count;
    }

    internal static MapRouteResult Analyze(MapDocument document, IReadOnlyList<MapRoutePoint> points, CancellationToken cancellation = default, int footprintSize = 1)
    {
        cancellation.ThrowIfCancellationRequested();
        if (footprintSize is < 1 or > 64) throw new ArgumentOutOfRangeException(nameof(footprintSize));
        new MapDesignSettings { Points = points.ToList() }.Validate(document.Width, document.Height);
        int count = CellCount(document);
        var legs = new List<MapRouteLeg>();
        if (points.Count < 2) return new(legs);
        var clearance = MapClearance.Build(document, cancellation);
        bool Fits(int column, int row) => clearance.At(column, row) >= footprintSize;
        var distance = new int[count]; var queue = new int[count];
        for (int pointIndex = 1; pointIndex < points.Count; pointIndex++)
        {
            cancellation.ThrowIfCancellationRequested();
            var from = points[pointIndex - 1]; var to = points[pointIndex];
            int start = from.Y * document.Width + from.X, goal = to.Y * document.Width + to.X;
            int manhattan = Math.Abs(from.X - to.X) + Math.Abs(from.Y - to.Y);
            if (!Fits(from.X, from.Y) || !Fits(to.X, to.Y))
            { legs.Add(new(from, to, false, 0, manhattan, 0, 0, []) { FootprintSize = footprintSize }); continue; }
            Array.Fill(distance, -1);
            int head = 0, tail = 0; queue[tail++] = start; distance[start] = 0;
            while (head < tail && distance[goal] < 0)
            {
                if ((head & 4095) == 0) cancellation.ThrowIfCancellationRequested();
                int current = queue[head++];
                foreach (int neighbour in Neighbours(current, document.Width, document.Height))
                    if (distance[neighbour] < 0 && Fits(neighbour % document.Width, neighbour / document.Width))
                    { distance[neighbour] = distance[current] + 1; queue[tail++] = neighbour; }
            }
            if (distance[goal] < 0) { legs.Add(new(from, to, false, 0, manhattan, 0, 0, []) { FootprintSize = footprintSize }); continue; }
            int cursor = goal, narrow = 0, dead = 0;
            int minimumClearance = int.MaxValue;
            var path = new List<int>(); int stride = Math.Max(1, (distance[goal] + 4095) / 4096);
            while (true)
            {
                if ((distance[cursor] & 4095) == 0) cancellation.ThrowIfCancellationRequested();
                minimumClearance = Math.Min(minimumClearance, clearance.At(cursor % document.Width, cursor / document.Width));
                int degree = 0;
                foreach (int neighbour in Neighbours(cursor, document.Width, document.Height)) if (Fits(neighbour % document.Width, neighbour / document.Width)) degree++;
                if (degree <= 2) narrow++;
                if (degree <= 1) dead++;
                if (cursor == start || cursor == goal || distance[cursor] % stride == 0) path.Add(cursor);
                if (cursor == start) break;
                foreach (int neighbour in Neighbours(cursor, document.Width, document.Height))
                    if (distance[neighbour] == distance[cursor] - 1) { cursor = neighbour; break; }
            }
            path.Reverse(); legs.Add(new(from, to, true, distance[goal], manhattan, narrow, dead, path)
                { FootprintSize = footprintSize, MinimumSquareClearance = minimumClearance });
        }
        return new(legs);
    }

    internal static MapCandidateMetrics Measure(MapDocument document, int? rooms = null, CancellationToken cancellation = default, MapDesignSettings? design = null)
    {
        cancellation.ThrowIfCancellationRequested();
        design ??= new(); design.Validate(document.Width, document.Height);
        int count = CellCount(document); var visited = new bool[count]; var queue = new int[count];
        var clearance = MapClearance.Build(document, cancellation); var degrees = new byte[count];
        bool Fits(int index) => clearance.At(index % document.Width, index / document.Width) >= design.FootprintSize;
        int start = design.Points.Count == 0 ? -1 : design.Points[0].Y * document.Width + design.Points[0].X;
        int? startArea = start < 0 ? null : 0; long clearanceSum = 0;
        using var hash = System.Security.Cryptography.IncrementalHash.CreateHash(System.Security.Cryptography.HashAlgorithmName.SHA256);
        Span<byte> header = stackalloc byte[12];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(header, document.Width);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(header[4..], document.Height);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(header[8..], design.FootprintSize); hash.AppendData(header);
        var fingerprintBuffer = new byte[4096];
        for (int index = 0; index < count; index++)
        {
            if ((index & 4095) == 0) cancellation.ThrowIfCancellationRequested();
            fingerprintBuffer[index & 4095] = Fits(index) ? (byte)1 : (byte)0;
            if ((index & 4095) == 4095 || index == count - 1) hash.AppendData(fingerprintBuffer.AsSpan(0, (index & 4095) + 1));
        }
        int walkable = 0, largest = 0, dead = 0;
        for (int index = 0; index < count; index++)
        {
            if ((index & 4095) == 0) cancellation.ThrowIfCancellationRequested();
            if (visited[index]) continue;
            visited[index] = true;
            if (!Fits(index)) continue;
            int head = 0, tail = 0; queue[tail++] = index; bool hasStart = false;
            while (head < tail)
            {
                if ((head & 4095) == 0) cancellation.ThrowIfCancellationRequested();
                int current = queue[head++], degree = 0;
                hasStart |= current == start; clearanceSum += clearance.At(current % document.Width, current / document.Width);
                foreach (int neighbour in Neighbours(current, document.Width, document.Height))
                {
                    bool open = Fits(neighbour);
                    if (open) degree++;
                    if (visited[neighbour]) continue;
                    visited[neighbour] = true;
                    if (open) queue[tail++] = neighbour;
                }
                if (degree <= 1) dead++;
                degrees[current] = (byte)degree;
            }
            walkable += tail; largest = Math.Max(largest, tail);
            if (hasStart) startArea = tail;
        }
        long branchDepth = 0; int branchCount = 0;
        for (int index = 0; index < count; index++)
        {
            if ((index & 4095) == 0) cancellation.ThrowIfCancellationRequested();
            if (degrees[index] != 1) continue;
            int previous = -1, cursor = index, depth = 0;
            while (true)
            {
                int next = -1;
                foreach (int neighbour in Neighbours(cursor, document.Width, document.Height))
                    if (neighbour != previous && Fits(neighbour)) { next = neighbour; break; }
                if (next < 0) break;
                previous = cursor; cursor = next; depth++;
                if ((depth & 4095) == 0) cancellation.ThrowIfCancellationRequested();
                if (degrees[cursor] != 2) break;
            }
            branchDepth += depth; branchCount++;
        }
        var route = Analyze(document, design.Points, cancellation, design.FootprintSize);
        bool allReachable = route.Legs.All(leg => leg.Reachable);
        return new(walkable, largest, count, dead, document.PlacedStamps.Count, rooms)
        {
            FootprintSize = design.FootprintSize, PossibleAnchors = Math.Max(0, document.Width - design.FootprintSize + 1) * Math.Max(0, document.Height - design.FootprintSize + 1), StartArea = startArea,
            RouteSteps = route.Legs.Count > 0 && allReachable ? route.Legs.Sum(leg => leg.Steps) : null,
            ReachableLegRatio = route.Legs.Count == 0 ? 1 : (double)route.Legs.Count(leg => leg.Reachable) / route.Legs.Count,
            MinimumRouteClearance = route.Legs.Count > 0 && allReachable ? route.Legs.Min(leg => leg.MinimumSquareClearance) : 0,
            MeanClearance = walkable == 0 ? 0 : (double)clearanceSum / walkable,
            MeanBranchDepth = branchCount == 0 ? 0 : (double)branchDepth / branchCount,
            TopologyFingerprint = Convert.ToHexString(hash.GetHashAndReset())
        };
    }

    internal static NeighbourCells Neighbours(int index, int width, int height) => new(index, width, height);

    internal struct NeighbourCells(int index, int width, int height)
    {
        private int direction;
        public int Current { get; private set; }
        public readonly NeighbourCells GetEnumerator() => this;
        public bool MoveNext()
        {
            while (direction < 4)
            {
                int next = direction++;
                if (next == 0 && index % width > 0) { Current = index - 1; return true; }
                if (next == 1 && index % width + 1 < width) { Current = index + 1; return true; }
                if (next == 2 && index / width > 0) { Current = index - width; return true; }
                if (next == 3 && index / width + 1 < height) { Current = index + width; return true; }
            }
            return false;
        }
    }

    internal static RasterImage Draw(MapDocument document, MapRouteResult route, MapDesignSettings design)
    {
        const int width = 640, height = 400;
        var pixels = new byte[width * height * 4];
        for (int row = 0; row < height; row++) for (int column = 0; column < width; column++)
        {
            int mapX = column * document.Width / width, mapY = row * document.Height / height;
            bool open = Walkable(document, mapX, mapY);
            Paint(column, row, open ? (byte)75 : (byte)30, open ? (byte)90 : (byte)32, open ? (byte)80 : (byte)40);
        }
        foreach (var region in design.Regions)
        {
            int left = region.X * width / document.Width, right = Math.Max(left + 1, (region.X + region.Width) * width / document.Width);
            int top = region.Y * height / document.Height, bottom = Math.Max(top + 1, (region.Y + region.Height) * height / document.Height);
            for (int column = left; column < right; column++) { Paint(column, top, 230, 170, 70); Paint(column, bottom - 1, 230, 170, 70); }
            for (int row = top; row < bottom; row++) { Paint(left, row, 230, 170, 70); Paint(right - 1, row, 230, 170, 70); }
        }
        foreach (var leg in route.Legs)
        {
            int? previous = null;
            foreach (int index in leg.Path)
            {
                int column = index % document.Width * width / document.Width, row = index / document.Width * height / document.Height;
                for (int offsetY = -1; offsetY <= 1; offsetY++) for (int offsetX = -1; offsetX <= 1; offsetX++) Paint(column + offsetX, row + offsetY, 70, 210, 250);
                if (previous is { } last && Math.Abs(last % document.Width - index % document.Width) + Math.Abs(last / document.Width - index / document.Width) == 1)
                {
                    int oldColumn = last % document.Width * width / document.Width, oldRow = last / document.Width * height / document.Height;
                    for (int sampleY = Math.Min(oldRow, row); sampleY <= Math.Max(oldRow, row); sampleY++)
                        for (int sampleX = Math.Min(oldColumn, column); sampleX <= Math.Max(oldColumn, column); sampleX++) Paint(sampleX, sampleY, 70, 210, 250);
                }
                previous = index;
            }
        }
        foreach (var point in design.Points)
        {
            int column = point.X * width / document.Width, row = point.Y * height / document.Height;
            for (int offsetY = -3; offsetY <= 3; offsetY++) for (int offsetX = -3; offsetX <= 3; offsetX++) Paint(column + offsetX, row + offsetY, 250, 100, 90);
        }
        return RasterImage.Create(width, height, 96, 96, RasterFormats.Bgra32, null, pixels, width * 4);

        void Paint(int column, int row, byte red, byte green, byte blue)
        {
            if (column < 0 || column >= width || row < 0 || row >= height) return;
            int offset = (row * width + column) * 4; pixels[offset] = blue; pixels[offset + 1] = green; pixels[offset + 2] = red; pixels[offset + 3] = 255;
        }
    }
}
