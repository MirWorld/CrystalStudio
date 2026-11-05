using System.IO;
using CrystalStudio.Formats;
namespace CrystalStudio;

public enum MapLayer { Ground, Middle, Objects, LowCollision, HighCollision }
public record Front(short Library, short Image, byte Frames = 0, byte Tick = 0);
public sealed record Cell
{
    public short BackLibrary { get; set; }
    public int BackImage { get; set; }
    public short MiddleLibrary { get; set; }
    public ushort MiddleImage { get; set; }
    [System.Text.Json.Serialization.JsonIgnore]
    public Front? Foreground { get; set; }
    [System.Text.Json.Serialization.JsonPropertyName("Fronts")]
    public ForegroundImages ForegroundImages { get => new(Foreground); init => Foreground = value.Value; }
    public byte Door { get; set; }
    public byte DoorOffset { get; set; }
    public byte MiddleFrames { get; set; }
    public byte MiddleTick { get; set; }
    public short TileAnimation { get; set; }
    public short TileOffset { get; set; }
    public byte TileFrames { get; set; }
    public byte Light { get; set; }
    public Cell Copy() => this with { };
    internal bool HasForeground => Foreground is { } front && (front.Image & 0x7FFF) != 0;
    internal bool SameContents(Cell other) => BackLibrary == other.BackLibrary && BackImage == other.BackImage &&
        MiddleLibrary == other.MiddleLibrary && MiddleImage == other.MiddleImage && Door == other.Door && DoorOffset == other.DoorOffset &&
        MiddleFrames == other.MiddleFrames && MiddleTick == other.MiddleTick && TileAnimation == other.TileAnimation &&
        TileOffset == other.TileOffset && TileFrames == other.TileFrames && Light == other.Light && Foreground == other.Foreground;
    public bool Empty => BackImage == 0 && MiddleImage == 0 && Foreground is null && Door == 0 && DoorOffset == 0 && MiddleFrames == 0 && MiddleTick == 0 && TileAnimation == 0 && TileOffset == 0 && TileFrames == 0 && Light == 0;
}
public sealed partial class MapDocument(int width, int height)
{
    public int Width { get; } = width;
    public int Height { get; } = height;
    public AssetSourceFormat SourceFormat { get; internal set; }
    private readonly Dictionary<int, Cell> cells = [];
    private readonly Dictionary<int, Cell> presentationCells = [];
    private static readonly Cell empty = new();
    internal long PickupRevision { get; private set; }
    public IEnumerable<KeyValuePair<int, Cell>> Cells => cells;
    internal IEnumerable<KeyValuePair<int, Cell>> PresentationCells => presentationCells;
    public bool Contains(int x, int y) => x >= 0 && y >= 0 && x < Width && y < Height;
    public Cell Get(int x, int y) => Contains(x, y) ? cells.GetValueOrDefault(y * Width + x) ?? empty : empty;
    public void Set(int x, int y, Cell c)
    {
        if (!Contains(x, y)) return;
        int k = y * Width + x;
        if (c.Empty) cells.Remove(k); else cells[k] = c;
        if (MapPreview.HasLight(c) || MapPreview.HasDoor(c)) presentationCells[k] = c;
        else presentationCells.Remove(k);
        PickupRevision++;
    }
    public static MapDocument Load(string path, AssetSourceFormat format = AssetSourceFormat.Default)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("Map file not found.", path);
        var legacy = new MapReader(path);
        if (legacy.Width <= 0 || legacy.Height <= 0 || (long)legacy.Width * legacy.Height > 16_000_000) throw new InvalidDataException("Invalid map dimensions.");
        var d = new MapDocument(legacy.Width, legacy.Height) { SourceFormat = format };
        bool sanxiuIds = format == AssetSourceFormat.Sanxiu && legacy.IsCrystalVersion1;
        for (int x = 0; x < d.Width; x++) for (int y = 0; y < d.Height; y++)
        {
            var s = legacy.MapCells[x, y] ?? throw new InvalidDataException("Incomplete map cell data.");
            var c = new Cell { BackLibrary = s.BackIndex, BackImage = s.BackImage, MiddleLibrary = s.MiddleIndex, MiddleImage = s.MiddleImage, Door = s.DoorIndex, DoorOffset = s.DoorOffset, MiddleFrames = s.MiddleAnimationFrame, MiddleTick = s.MiddleAnimationTick, TileAnimation = s.TileAnimationImage, TileOffset = s.TileAnimationOffset, TileFrames = s.TileAnimationFrames, Light = s.Light };
            if (s.HasFront) c.Foreground = new(s.FrontIndex, s.FrontImage, s.FrontAnimationFrame, s.FrontAnimationTick);
            if (sanxiuIds)
            {
                if (c.MiddleLibrary >= 0 && c.MiddleImage != 0) c.MiddleLibrary = checked((short)(c.MiddleLibrary + 1000));
                if (c.Foreground is { Library: >= 0 } front && (front.Image & 0x7FFF) != 0)
                    c.Foreground = front with { Library = checked((short)(front.Library + 2000)) };
            }
            d.Set(x, y, c);
        }
        StampMetadata.Read(path, d);
        return d;
    }
    public void Save(string path)
    {
        AtomicFile.Write(path, stream =>
        {
            using var w = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true);
            w.Write((short)1); w.Write((byte)'C'); w.Write((byte)'#'); w.Write(checked((short)Width)); w.Write(checked((short)Height));
            for (int x = 0; x < Width; x++) for (int y = 0; y < Height; y++)
            {
                Cell cell = Get(x, y);
                if (SourceFormat == AssetSourceFormat.Sanxiu)
                {
                    cell = cell.Copy();
                    if (cell.MiddleImage != 0 && cell.MiddleLibrary is >= 1000 and < 2000)
                        cell.MiddleLibrary -= 1000;
                    if (cell.Foreground is { Library: >= 2000 and < 3000 } front)
                        cell.Foreground = front with { Library = (short)(front.Library - 2000) };
                }
                CellCodec.Write(w, cell, true);
            }
            StampMetadata.Write(w, d: this);
        });
    }
}
public record StampCell(int X, int Y, Cell Cell);
public static class CellCodec
{
    public static void Write(BinaryWriter w, Cell c, bool map)
    {
        w.Write(c.BackLibrary); w.Write(c.BackImage); w.Write(c.MiddleLibrary); w.Write(c.MiddleImage);
        if (map)
        {
            var f = c.Foreground;
            w.Write(f?.Library ?? (short)0); w.Write(f?.Image ?? (short)0); w.Write(c.Door); w.Write(c.DoorOffset); w.Write(f?.Frames ?? (byte)0); w.Write(f?.Tick ?? (byte)0);
            w.Write(c.MiddleFrames); w.Write(c.MiddleTick); w.Write(c.TileAnimation); w.Write(c.TileOffset); w.Write(c.TileFrames); w.Write(c.Light);
            return;
        }
        w.Write(checked((byte)c.ForegroundImages.Count));
        foreach (var f in c.ForegroundImages) { w.Write(f.Library); w.Write(f.Image); w.Write(f.Frames); w.Write(f.Tick); }
        w.Write(c.Door); w.Write(c.DoorOffset); w.Write(c.MiddleFrames); w.Write(c.MiddleTick); w.Write(c.TileAnimation); w.Write(c.TileOffset); w.Write(c.TileFrames); w.Write(c.Light);

    }
    public static List<StampCell> LoadStamp(string path, CancellationToken cancellation = default)
    {
        using var stream = File.OpenRead(path);
        return LoadStamp(stream, cancellation);
    }
    internal static List<StampCell> LoadStamp(Stream stream, CancellationToken cancellation = default)
    {
        using var r = new BinaryReader(stream, System.Text.Encoding.UTF8, leaveOpen: true);
        int version = 0; if (r.BaseStream.Length >= 3 && r.ReadByte() == 'C' && r.ReadByte() == '#') version = r.ReadByte(); else r.BaseStream.Position = 0;
        if (version > 2) throw new InvalidDataException("Unsupported object version.");
        int count = r.ReadInt32();
        int minimumCellBytes = version == 0 ? 34 : version == 1 ? 31 : 29;
        if (count < 0 || count > 1_000_000 || (long)count * minimumCellBytes > r.BaseStream.Length - r.BaseStream.Position)
            throw new InvalidDataException("Invalid or truncated object cell data.");
        List<StampCell> result = new(count);
        for (int i = 0; i < count; i++)
        {
            if ((i & 1023) == 0) cancellation.ThrowIfCancellationRequested();
            int x = r.ReadInt32(), y = r.ReadInt32(); var c = new Cell { BackLibrary = r.ReadInt16(), BackImage = r.ReadInt32(), MiddleLibrary = r.ReadInt16(), MiddleImage = r.ReadUInt16() };
            int fronts = version == 0 ? 1 : r.ReadByte();
            if (fronts > 1) throw new InvalidDataException("An object cell supports one foreground image.");
            if (fronts == 1) c.Foreground = new(r.ReadInt16(), r.ReadInt16(), version >= 2 ? r.ReadByte() : (byte)0, version >= 2 ? r.ReadByte() : (byte)0);
            c.Door = r.ReadByte(); c.DoorOffset = r.ReadByte(); if (version < 2) { byte frames = r.ReadByte(), tick = r.ReadByte(); if (c.Foreground is { } front) c.Foreground = front with { Frames = frames, Tick = tick }; }
            c.MiddleFrames = r.ReadByte(); c.MiddleTick = r.ReadByte(); c.TileAnimation = r.ReadInt16(); c.TileOffset = r.ReadInt16(); c.TileFrames = r.ReadByte(); c.Light = r.ReadByte(); result.Add(new(x, y, c));
        }
        return result;
    }
    public static List<StampCell> CropStamp(IEnumerable<StampCell> cells)
    {
        var occupied = cells.Where(s => !s.Cell.Empty).ToList();
        if (occupied.Count == 0) return occupied;
        int left = occupied.Min(s => s.X), top = occupied.Min(s => s.Y);
        int centerX = checked((int)(left + ((long)occupied.Max(s => s.X) - left + 1) / 2));
        int centerY = checked((int)(top + ((long)occupied.Max(s => s.Y) - top + 1) / 2));
        // Ground tiles use a two-cell grid: translate by even offsets to retain their alignment.
        if (occupied.Any(s => (s.Cell.BackImage & 0x1FFFFFFF) != 0)) { centerX &= ~1; centerY &= ~1; }
        return occupied.Select(s => new StampCell(checked(s.X - centerX), checked(s.Y - centerY), s.Cell.Copy())).ToList();
    }
    public static void SaveStamp(string path, IEnumerable<StampCell> cells)
    {
        var list = cells.ToList();
        if (list.Count > 1_000_000) throw new InvalidDataException("Invalid object cell count.");
        AtomicFile.Write(path, stream =>
        {
            using var w = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true);
            w.Write((byte)'C'); w.Write((byte)'#'); w.Write((byte)2); w.Write(list.Count);
            foreach (var c in list) { w.Write(c.X); w.Write(c.Y); Write(w, c.Cell, false); }
        });
    }
}
public sealed class EditHistory
{
    public bool CanUndo => undo.Count > 0 || stroke?.Count > 0 || stampSnapshot != null;
    public bool CanRedo => redo.Count > 0;
    private sealed record Change(Dictionary<int, Cell> Cells, List<PlacedStamp>? Stamps, long State);
    private readonly Stack<Change> undo = [], redo = [];
    private Dictionary<int, Cell>? stroke;
    private List<PlacedStamp>? stampSnapshot;
    private long currentState, savedState, nextState;
    public bool Dirty => stroke?.Count > 0 || stampSnapshot != null || currentState != savedState;
    public void Begin() => stroke ??= [];
    public void RecordStamps(MapDocument d) { Begin(); stampSnapshot ??= [.. d.PlacedStamps]; }
    public void Record(MapDocument d, int x, int y)
    {
        if (!d.Contains(x, y)) return;
        Begin();
        int key = y * d.Width + x;
        // A stroke owns the first snapshot, even when repeated brush footprints overlap.
        if (!stroke!.ContainsKey(key)) stroke.Add(key, d.Get(x, y).Copy());
    }
    public void Commit()
    {
        if (stroke?.Count > 0 || stampSnapshot != null)
        {
            undo.Push(new(stroke ?? [], stampSnapshot, currentState));
            currentState = ++nextState;
            redo.Clear();
            if (undo.Count > 100)
            {
                var keep = undo.Take(100).Reverse().ToArray();
                undo.Clear();
                foreach (var change in keep) undo.Push(change);
            }
        }
        stroke = null; stampSnapshot = null;
    }
    private void Swap(MapDocument d, Stack<Change> from, Stack<Change> to, Action<int, int>? changed)
    {
        Commit();
        if (!from.TryPop(out var change)) return;
        var inverse = new Dictionary<int, Cell>(change.Cells.Count);
        List<PlacedStamp>? inverseStamps = change.Stamps == null ? null : [.. d.PlacedStamps];
        foreach (var (key, cell) in change.Cells)
        {
            int x = key % d.Width, y = key / d.Width;
            inverse[key] = d.Get(x, y).Copy();
            d.Set(x, y, cell); changed?.Invoke(x, y);
        }
        if (change.Stamps != null) d.RestoreStamps(change.Stamps);
        to.Push(new(inverse, inverseStamps, currentState));
        currentState = change.State;
    }
    public void Undo(MapDocument d, Action<int, int>? changed = null) => Swap(d, undo, redo, changed);
    public void Redo(MapDocument d, Action<int, int>? changed = null) => Swap(d, redo, undo, changed);
    public void Saved() { Commit(); savedState = currentState; }
}

