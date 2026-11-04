using System.IO;
using System.IO.Compression;
using System.Collections.Concurrent;
using System.Buffers;
using System.Threading.Channels;
namespace CrystalStudio;

public record Sprite(RasterImage Bitmap, int X, int Y, int OriginalWidth = 0, int OriginalHeight = 0)
{
    public int Width => OriginalWidth > 0 ? OriginalWidth : Bitmap.PixelWidth;
    public int Height => OriginalHeight > 0 ? OriginalHeight : Bitmap.PixelHeight;
}
public readonly record struct SpriteHeader(int Width, int Height, int X, int Y);
public sealed class CrystalLibrary
{
    public string Path { get; }
    public int Count => offsets.Length;
    private readonly int[] offsets;
    public CrystalLibrary(string path)
    {
        Path = System.IO.Path.GetFullPath(path); using var r = new BinaryReader(File.OpenRead(Path)); int version = r.ReadInt32(), count = r.ReadInt32();
        if ((version != 2 && version != 3) || count < 0 || count > 2_000_000 || (version == 3 ? 12L : 8L) + count * 4L > r.BaseStream.Length) throw new InvalidDataException("Expected a Crystal version 2 or 3 library.");
        if (version == 3) r.ReadInt32();
        offsets = new int[count]; for (int i = 0; i < count; i++) { offsets[i] = r.ReadInt32(); if (offsets[i] < 0 || offsets[i] >= r.BaseStream.Length) throw new InvalidDataException("Invalid sprite offset."); }
    }
    public SpriteHeader? ReadHeader(int index)
    {
        if (index < 0 || index >= Count) return null;
        using var r = new BinaryReader(File.OpenRead(Path)); r.BaseStream.Position = offsets[index];
        int w = r.ReadInt16(), h = r.ReadInt16(), x = r.ReadInt16(), y = r.ReadInt16();
        r.ReadInt16(); r.ReadInt16(); r.ReadByte(); int length = r.ReadInt32();
        if (w <= 0 || h <= 0) return null;
        if (w > 8192 || h > 8192 || length < 0 || length > r.BaseStream.Length - r.BaseStream.Position)
            throw new InvalidDataException("Invalid sprite dimensions or length.");
        return new(w, h, x, y);
    }
    public Sprite? Decode(int index)
    {
        if (index < 0 || index >= Count) return null;
        using var r = new BinaryReader(File.OpenRead(Path)); r.BaseStream.Position = offsets[index];
        int w = r.ReadInt16(), h = r.ReadInt16(), x = r.ReadInt16(), y = r.ReadInt16(); r.ReadInt16(); r.ReadInt16(); r.ReadByte(); int length = r.ReadInt32();
        if (w <= 0 || h <= 0) return null;
        if (w > 8192 || h > 8192 || length < 0 || length > r.BaseStream.Length - r.BaseStream.Position) throw new InvalidDataException("Invalid sprite dimensions or length.");
        byte[] raw = new byte[checked(w * h * 4)]; using (var gzip = new GZipStream(new MemoryStream(r.ReadBytes(length)), CompressionMode.Decompress)) { gzip.ReadExactly(raw); if (gzip.ReadByte() != -1) throw new InvalidDataException("Unexpected sprite pixel length."); }
        var bitmap = RasterImage.Create(w, h, 96, 96, RasterFormats.Bgra32, null, raw, w * 4);  return new(bitmap, x, y);
    }
    internal Sprite? DecodeOverview(BinaryReader reader, int index, int level, CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        if (index < 0 || index >= Count) return null;
        reader.BaseStream.Position = offsets[index];
        int w = reader.ReadInt16(), h = reader.ReadInt16(), x = reader.ReadInt16(), y = reader.ReadInt16();
        reader.ReadInt16(); reader.ReadInt16(); reader.ReadByte(); int length = reader.ReadInt32();
        if (w <= 0 || h <= 0) return null;
        if (w > 8192 || h > 8192 || length < 0 || length > reader.BaseStream.Length - reader.BaseStream.Position)
            throw new InvalidDataException("Invalid sprite dimensions or length.");
        int width = Math.Max(1, (w + (1 << level) - 1) >> level), height = Math.Max(1, (h + (1 << level) - 1) >> level);
        byte[] compressed = ArrayPool<byte>.Shared.Rent(Math.Max(1, length));
        byte[] raw = ArrayPool<byte>.Shared.Rent(checked(w * h * 4));
        try
        {
            reader.BaseStream.ReadExactly(compressed.AsSpan(0, length));
            using (var gzip = new GZipStream(new MemoryStream(compressed, 0, length, false), CompressionMode.Decompress))
            {
                int pixelBytes = w * h * 4;
                for (int offset = 0; offset < pixelBytes; offset += 65536)
                {
                    cancellation.ThrowIfCancellationRequested();
                    gzip.ReadExactly(raw.AsSpan(offset, Math.Min(65536, pixelBytes - offset)));
                }
                if (gzip.ReadByte() != -1) throw new InvalidDataException("Unexpected sprite pixel length.");
            }
            byte[] pixels = OverviewPixels.Reduce(raw, w, h, width, height, cancellation);
            var bitmap = RasterImage.Create(width, height, 96, 96, RasterFormats.Bgra32, null, pixels, width * 4);
             return new(bitmap, x, y, w, h);
        }
        finally { ArrayPool<byte>.Shared.Return(compressed); ArrayPool<byte>.Shared.Return(raw); }
    }
}
public sealed class AssetStore : IDisposable
{
    public Dictionary<int, CrystalLibrary> Libraries { get; } = [];
    private readonly ConcurrentDictionary<(int, int), Sprite> cache = [];
    private readonly ConcurrentDictionary<(int, int), byte> pending = [];
    private readonly ConcurrentDictionary<(int, int, int), Sprite> overviewCache = [];
    private readonly ConcurrentQueue<(int, int, int)> overviewOrder = [];
    private readonly ConcurrentDictionary<(int, int), byte> failed = [];
    private readonly ConcurrentQueue<(int, int)> order = [];
    private readonly Channel<(int, int)> queue = Channel.CreateBounded<(int, int)>(new BoundedChannelOptions(4096) { FullMode = BoundedChannelFullMode.Wait });
    private readonly CancellationTokenSource stop = new();
    private readonly CancellationToken stopToken;
    private readonly Task[] workers;
    private int disposed;
    private long bytes;
    private long overviewBytes;
    public event Action? Updated;
    public event Action<string>? Error;
    public int Cached => cache.Count;
    public long OverviewBytes => Interlocked.Read(ref overviewBytes);
    public int OverviewCached => overviewCache.Count;
    public bool IsResolved(int library, int index) => Volatile.Read(ref disposed) != 0 || cache.ContainsKey((library, index)) || failed.ContainsKey((library, index)) || !Libraries.TryGetValue(library, out var atlas) || index < 0 || index >= atlas.Count;
    public AssetStore()
    {
        stopToken = stop.Token;
        workers = [Task.Run(Worker), Task.Run(Worker)];
    }
    public Sprite? Get(int lib, int image)
    {
        if (Volatile.Read(ref disposed) != 0) return null;
        var key = (lib, image); if (cache.TryGetValue(key, out var s)) return s;
        if (!Libraries.TryGetValue(lib, out var atlas) || image < 0 || image >= atlas.Count || failed.ContainsKey(key)) return null;
        if (pending.TryAdd(key, 0) && !queue.Writer.TryWrite(key)) pending.TryRemove(key, out _); return null;
    }

    public async Task<Dictionary<(int Library, int Image), Sprite>> PrepareOverviewAsync(
        IEnumerable<(int Library, int Image)> images, int level, CancellationToken cancellationToken)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, stopToken);
        cancellation.Token.ThrowIfCancellationRequested();
        level = Math.Clamp(level, 2, 16);
        var result = new ConcurrentDictionary<(int Library, int Image), Sprite>();
        var missing = new List<(int Library, int Image)>();
        foreach (var key in images.Distinct())
        {
            cancellation.Token.ThrowIfCancellationRequested();
            if (failed.ContainsKey(key) || !Libraries.TryGetValue(key.Library, out var atlas) || key.Image < 0 || key.Image >= atlas.Count) continue;
            if (overviewCache.TryGetValue((key.Library, key.Image, level), out var cached)) result[key] = cached;
            else missing.Add(key);
        }
        var jobs = missing
            .GroupBy(k => k.Library).SelectMany(group => group.OrderBy(k => k.Image).Chunk(128)).ToArray();
        await Parallel.ForEachAsync(jobs, new ParallelOptions
        {
            MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount / 2, 2, 8),
            CancellationToken = cancellation.Token
        }, (job, token) =>
        {
            var library = Libraries[job[0].Library];
            using var reader = new BinaryReader(new FileStream(library.Path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536));
            foreach (var key in job)
            {
                token.ThrowIfCancellationRequested();
                if (failed.ContainsKey(key)) continue;
                if (!overviewCache.TryGetValue((key.Library, key.Image, level), out var sprite))
                {
                    try { sprite = library.DecodeOverview(reader, key.Image, level, token); }
                    catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentException)
                    { failed[key] = 0; Error?.Invoke(ex.Message); continue; }
                    if (sprite == null) { failed[key] = 0; continue; }
                    token.ThrowIfCancellationRequested();
                    if (overviewCache.TryAdd((key.Library, key.Image, level), sprite))
                    {
                        overviewOrder.Enqueue((key.Library, key.Image, level));
                        Interlocked.Add(ref overviewBytes, (long)sprite.Bitmap.PixelWidth * sprite.Bitmap.PixelHeight * 4);
                        // Evict as we populate: cancellation must not bypass the cache budget.
                        while (Interlocked.Read(ref overviewBytes) > 64L * 1024 * 1024 && overviewOrder.TryDequeue(out var old))
                            if (overviewCache.TryRemove(old, out var removed)) Interlocked.Add(ref overviewBytes, -(long)removed.Bitmap.PixelWidth * removed.Bitmap.PixelHeight * 4);
                    }
                }
                result[key] = sprite;
            }
            return ValueTask.CompletedTask;
        });
        return result.ToDictionary(k => k.Key, k => k.Value);
    }

    private async Task Worker()
    {
        try
        {
            await foreach (var k in queue.Reader.ReadAllAsync(stopToken))
            {
                if (stopToken.IsCancellationRequested) break;
                try
                {
                    if (Libraries.TryGetValue(k.Item1, out var l))
                    {
                        var imageKey = (k.Item1, k.Item2);
                        var s = l.Decode(k.Item2);
                        if (stopToken.IsCancellationRequested) break;
                        if (s != null)
                        {
                            if (cache.TryAdd(imageKey, s))
                            {
                                order.Enqueue(imageKey); Interlocked.Add(ref bytes, (long)s.Bitmap.PixelWidth * s.Bitmap.PixelHeight * 4);
                                while (Interlocked.Read(ref bytes) > 384L * 1024 * 1024 && order.TryDequeue(out var old)) if (cache.TryRemove(old, out var removed)) Interlocked.Add(ref bytes, -(long)removed.Bitmap.PixelWidth * removed.Bitmap.PixelHeight * 4);
                            }
                        }
                        else failed[(k.Item1, k.Item2)] = 0;
                    }
                }
                catch (Exception ex) { failed[(k.Item1, k.Item2)] = 0; Error?.Invoke(ex.Message); }
                finally { pending.TryRemove(k, out _); Updated?.Invoke(); }
            }
        }
        catch (OperationCanceledException) { }
    }
    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        Updated = null; Error = null;
        stop.Cancel(); queue.Writer.TryComplete();
        _ = Task.WhenAll(workers).ContinueWith(completed =>
        {
            _ = completed.Exception;
            pending.Clear();
            stop.Dispose();
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }
    public static AssetStore Load(string root) { var store = new AssetStore(); try { store.Register(root); return store; } catch { store.Dispose(); throw; } }
    private void Add(int id, string root, string relative) { string path = System.IO.Path.Combine(root, relative.Replace('\\', System.IO.Path.DirectorySeparatorChar) + ".lib"); if (File.Exists(path)) Libraries[id] = new(path); }
    private void Register(string root)
    {
        // IDs follow the reference editor's Crystal library table.
        string map = Directory.Exists(System.IO.Path.Combine(root, "Map")) ? System.IO.Path.Combine(root, "Map") : root;
        Add(0, map, @"WemadeMir2\Tiles"); Add(1, map, @"WemadeMir2\Smtiles"); Add(2, map, @"WemadeMir2\Objects");
        for (int i = 2; i < 99; i++) Add(i + 1, map, @"WemadeMir2\Objects" + i);
        Add(100, map, @"ShandaMir2\Tiles"); Add(110, map, @"ShandaMir2\SmTiles"); Add(120, map, @"ShandaMir2\Objects");
        for (int i = 1; i < 10; i++) { Add(100 + i, map, @"ShandaMir2\Tiles" + (i + 1)); Add(110 + i, map, @"ShandaMir2\SmTiles" + (i + 1)); }
        for (int i = 1; i < 31; i++) Add(120 + i, map, @"ShandaMir2\Objects" + (i + 1)); Add(190, map, @"ShandaMir2\AniTiles1");
        string[] names = ["Tilesc", "Tiles30c", "Tiles5c", "Smtilesc", "Housesc", "Cliffsc", "Dungeonsc", "Innersc", "Furnituresc", "Wallsc", "smObjectsc", "Animationsc", "Object1c", "Object2c"];
        string[] states = ["", "wood", "sand", "snow", "forest"];
        for (int i = 0; i < states.Length; i++) for (int j = 0; j < names.Length; j++)
        {
            Add(200 + i * 15 + j, map, System.IO.Path.Combine("WemadeMir3", states[i], names[j]));
            Add(300 + i * 15 + j, map, System.IO.Path.Combine("ShandaMir3", names[j] + states[i]));
        }
        // Flat folders are accepted for the original Mir2 libraries too.
        if (Libraries.Count == 0) { Add(0, map, "Tiles"); Add(1, map, "SmTiles"); Add(2, map, "Objects"); for (int i = 2; i < 99; i++) Add(i + 1, map, "Objects" + i); }
        if (Libraries.Count == 0) throw new InvalidDataException("No Crystal .lib files found. Select Data, Data/Map, or a flat Mir2 library folder.");
    }
}



