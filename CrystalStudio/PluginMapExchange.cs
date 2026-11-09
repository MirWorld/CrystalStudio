using System.IO;
using System.Text;

namespace CrystalStudio;

// CSMP v1: little-endian, sparse cells. See SDK/NativeMapPlugin/README.md.
internal static class PluginMapExchange
{
    internal const int CellBytes = 35;
    internal static byte[] Encode(MapDocument map, bool includePrivateData = true)
    {
        using var stream = new MemoryStream(); using var w = new BinaryWriter(stream);
        w.Write("CSMP"u8); w.Write(1); w.Write(map.Width); w.Write(map.Height); w.Write((int)map.SourceFormat);
        byte[] privateData = includePrivateData ? map.PluginData : [];
        w.Write(privateData.Length); w.Write(privateData);
        var cells = map.Cells.ToArray(); w.Write(cells.Length);
        foreach (var pair in cells)
        {
            w.Write(pair.Key % map.Width); w.Write(pair.Key / map.Width);
            var c = pair.Value; var f = c.Foreground;
            w.Write(c.BackLibrary); w.Write(c.BackImage); w.Write(c.MiddleLibrary); w.Write(c.MiddleImage);
            w.Write(f?.Library ?? (short)0); w.Write(f?.Image ?? (short)0); w.Write(f?.Frames ?? (byte)0); w.Write(f?.Tick ?? (byte)0); w.Write((byte)(f == null ? 0 : 1));
            w.Write(c.Door); w.Write(c.DoorOffset); w.Write(c.MiddleFrames); w.Write(c.MiddleTick);
            w.Write(c.TileAnimation); w.Write(c.TileOffset); w.Write(c.TileFrames); w.Write(c.Light);
        }
        return stream.ToArray();
    }

    internal static MapDocument Decode(byte[] bytes, string pluginId)
    {
        using var stream = new MemoryStream(bytes, false); using var r = new BinaryReader(stream);
        if (!r.ReadBytes(4).AsSpan().SequenceEqual("CSMP"u8) || r.ReadInt32() != 1) throw new InvalidDataException("Invalid plugin map exchange version.");
        int width = r.ReadInt32(), height = r.ReadInt32(), format = r.ReadInt32();
        MapDimensions.Validate(width, height);
        if (!Enum.IsDefined((AssetSourceFormat)format)) throw new InvalidDataException("Invalid plugin asset format.");
        int length = r.ReadInt32();
        if (length < 0 || length > stream.Length - stream.Position - 4) throw new InvalidDataException("Invalid plugin private data length.");
        var map = new MapDocument(width, height) { SourceFormat = (AssetSourceFormat)format, PluginId = pluginId, PluginData = r.ReadBytes(length) };
        int count = r.ReadInt32();
        if (count < 0 || count > (long)width * height || stream.Length - stream.Position != (long)count * CellBytes) throw new InvalidDataException("Invalid plugin cell count or record size.");
        var seen = new HashSet<int>();
        for (int i = 0; i < count; i++)
        {
            int x = r.ReadInt32(), y = r.ReadInt32();
            if (!map.Contains(x, y) || !seen.Add(y * width + x)) throw new InvalidDataException("Invalid or duplicate plugin cell coordinates.");
            var c = new Cell { BackLibrary = r.ReadInt16(), BackImage = r.ReadInt32(), MiddleLibrary = r.ReadInt16(), MiddleImage = r.ReadUInt16() };
            var front = new Front(r.ReadInt16(), r.ReadInt16(), r.ReadByte(), r.ReadByte()); byte hasFront = r.ReadByte();
            if (hasFront > 1) throw new InvalidDataException("Invalid foreground presence flag.");
            c.Foreground = hasFront == 1 ? front : null;
            c.Door = r.ReadByte(); c.DoorOffset = r.ReadByte(); c.MiddleFrames = r.ReadByte(); c.MiddleTick = r.ReadByte();
            c.TileAnimation = r.ReadInt16(); c.TileOffset = r.ReadInt16(); c.TileFrames = r.ReadByte(); c.Light = r.ReadByte(); map.Set(x, y, c);
        }
        return map;
    }
}
