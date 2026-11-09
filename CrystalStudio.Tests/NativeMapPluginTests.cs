using System.Buffers.Binary;
using System.IO;

namespace CrystalStudio.Tests;

public sealed class NativeMapPluginTests
{
    [Fact]
    public void ExchangePreservesEveryFieldRawFlagsAndPrivateData()
    {
        var map = CreateMap(); byte[] bytes = PluginMapExchange.Encode(map);
        Assert.Equal(28 + map.PluginData.Length + PluginMapExchange.CellBytes, bytes.Length);
        var decoded = PluginMapExchange.Decode(bytes, "test");
        Assert.Equal((8, 6, AssetSourceFormat.Sanxiu), (decoded.Width, decoded.Height, decoded.SourceFormat));
        Assert.True(map.Get(2, 3).SameContents(decoded.Get(2, 3)));
        Assert.Equal(map.PluginData, decoded.PluginData); Assert.Equal("test", decoded.PluginId);
        Assert.Empty(PluginMapExchange.Decode(PluginMapExchange.Encode(map, false), "other").PluginData);
    }

    [Fact]
    public void ExchangeRejectsMalformedDimensionsCountsCoordinatesAndTruncation()
    {
        var map = CreateMap(); map.PluginData = []; byte[] valid = PluginMapExchange.Encode(map);
        foreach (var (offset, value) in new[] { (4, 99), (8, -1), (12, int.MaxValue), (16, 2), (20, -1), (24, 100), (28, 8), (32, -1) })
        {
            byte[] broken = (byte[])valid.Clone(); BinaryPrimitives.WriteInt32LittleEndian(broken.AsSpan(offset), value);
            Assert.ThrowsAny<Exception>(() => PluginMapExchange.Decode(broken, "bad"));
        }
        Assert.ThrowsAny<Exception>(() => PluginMapExchange.Decode(valid[..^1], "bad"));
        byte[] duplicate = [..valid, ..valid.AsSpan(28).ToArray()]; BinaryPrimitives.WriteInt32LittleEndian(duplicate.AsSpan(24), 2);
        Assert.Throws<InvalidDataException>(() => PluginMapExchange.Decode(duplicate, "bad"));
    }

    [Fact]
    public void RecoveryAndResizeKeepPrivateDataWithoutChangingSaveFormatOnUndo()
    {
        string folder = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try
        {
            var map = CreateMap(); map.PluginId = "private.v1";
            map.PluginData = new byte[1_100_000]; map.PluginData[^1] = 42;
            using var store = new MapRecoveryStore(folder); Guid id = Guid.NewGuid();
            store.Write(id, MapRecoveryStore.Capture(map), "original.pmap", "private", null);
            store.Dispose(); using var nextSession = new MapRecoveryStore(folder);
            var entry = Assert.Single(nextSession.List());
            var restored = nextSession.Load(entry);
            Assert.Equal(map.PluginId, restored.PluginId); Assert.Equal(map.PluginData, restored.PluginData);
            var history = new EditHistory(); history.Resize(map, map.Expand(2, 2, 0, 0));
            Assert.Equal("private.v1", map.PluginId); Assert.Equal(restored.PluginData, map.PluginData);
            // Save As changes the format. Undoing a resize must not restore the previous codec.
            map.PluginId = "other"; map.PluginData = [];
            history.Undo(map, (_, _) => { });
            Assert.Equal("other", map.PluginId); Assert.Empty(map.PluginData);
            nextSession.Delete(entry); Assert.Empty(nextSession.List());
            Assert.Empty(Directory.GetFiles(folder, "*.plugin", SearchOption.AllDirectories));
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
    }

    [NativePluginFact]
    public void RealCppDllOpensEditsSavesReopensAndRegistersAcrossSessions()
    {
        string folder = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")); Directory.CreateDirectory(folder);
        try
        {
            var registry = new MapFormatPlugins(Path.Combine(folder, "plugins.json"));
            var plugin = registry.Load(Dll("example.plugin.dll")); Assert.True(plugin.CanRead); Assert.True(plugin.CanWrite);
            var map = CreateMap(); map.PluginId = plugin.Id;
            string path = Path.Combine(folder, "private.map"); // Deliberately shares built-in extension.
            registry.Save(map, path, plugin.Id);
            var loaded = registry.Open(path, AssetSourceFormat.Default);
            Assert.Equal(plugin.Id, loaded.PluginId); Assert.True(map.Get(2, 3).SameContents(loaded.Get(2, 3))); Assert.Equal(map.PluginData, loaded.PluginData);
            loaded.Set(4, 4, new Cell { Light = 7, BackImage = 0x20000001 }); registry.Save(loaded, path, plugin.Id);
            Assert.True(File.Exists(path + ".bak")); Assert.Equal(7, registry.Open(path, AssetSourceFormat.Default).Get(4, 4).Light);
            var nextSession = new MapFormatPlugins(Path.Combine(folder, "plugins.json")); Assert.Equal(plugin.Id, Assert.Single(nextSession.Plugins).Id);
            string native = Path.Combine(folder, "native.map"); registry.Save(loaded, native, null);
            Assert.Equal(7, registry.Open(native, AssetSourceFormat.Default).Get(4, 4).Light);
            Assert.Null(registry.Open(native, AssetSourceFormat.Default).PluginId);
            Assert.Contains("*.csdemo", registry.OpenFilter);
            Assert.Throws<InvalidOperationException>(() => registry.Save(map, path, "missing"));
            Assert.Equal(7, registry.Open(path, AssetSourceFormat.Default).Get(4, 4).Light);
            Assert.Throws<InvalidDataException>(() => plugin.Read("bad"u8.ToArray()));
            string badDll = Path.Combine(folder, "not-a-dll.dll"); File.WriteAllText(badDll, "invalid");
            Assert.ThrowsAny<Exception>(() => registry.Load(badDll)); Assert.Single(registry.Plugins);
        }
        finally { Directory.Delete(folder, true); }
    }

    [NativePluginFact]
    public void ReadOnlyAndFailedWriterPreserveOriginalFile()
    {
        string folder = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")); Directory.CreateDirectory(folder);
        try
        {
            var registry = new MapFormatPlugins(Path.Combine(folder, "plugins.json"));
            var readOnly = registry.Load(Dll("readonly.plugin.dll")); Assert.True(readOnly.CanRead); Assert.False(readOnly.CanWrite);
            var failed = registry.Load(Dll("failed.plugin.dll")); Assert.False(failed.CanRead); Assert.True(failed.CanWrite);
            string path = Path.Combine(folder, "original.map"); byte[] original = [1, 2, 3, 4]; File.WriteAllBytes(path, original);
            Assert.Throws<NotSupportedException>(() => registry.Save(CreateMap(), path, readOnly.Id));
            Assert.Equal(original, File.ReadAllBytes(path));
            var error = Assert.Throws<InvalidDataException>(() => registry.Save(CreateMap(), path, failed.Id));
            Assert.Contains("deliberate failure", error.Message); Assert.Equal(original, File.ReadAllBytes(path));
            Assert.False(File.Exists(path + ".bak")); Assert.Empty(Directory.GetFiles(folder, "*.tmp"));
            var example = registry.Load(Dll("example.plugin.dll"));
            string duplicate = Path.Combine(folder, "duplicate.dll"); File.Copy(example.FilePath, duplicate);
            Assert.Throws<InvalidDataException>(() => registry.Load(duplicate));
            registry.Save(CreateMap(), path, example.Id);
            Assert.Contains("Multiple plugins", Assert.Throws<InvalidDataException>(() => registry.Open(path, AssetSourceFormat.Default)).Message);
        }
        finally { Directory.Delete(folder, true); }
    }

    private static string Dll(string name) => Path.Combine(Environment.GetEnvironmentVariable("CS_NATIVE_PLUGIN_TEST_DIR")!, name);
    private static MapDocument CreateMap()
    {
        var map = new MapDocument(8, 6) { SourceFormat = AssetSourceFormat.Sanxiu, PluginData = [0, 1, 255, 128] };
        map.Set(2, 3, new Cell { BackLibrary = 32, BackImage = 0x60000005, MiddleLibrary = 1002, MiddleImage = 0x8003,
            Foreground = new Front(2003, unchecked((short)0x8004), 5, 6), Door = 7, DoorOffset = 8, MiddleFrames = 9,
            MiddleTick = 10, TileAnimation = -11, TileOffset = -12, TileFrames = 13, Light = 14 });
        return map;
    }
}

public sealed class NativePluginFactAttribute : FactAttribute
{
    public NativePluginFactAttribute()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("CS_NATIVE_PLUGIN_TEST_DIR")))
            Skip = "Compile SDK example and native test fixtures, then set CS_NATIVE_PLUGIN_TEST_DIR to their DLL directory.";
    }
}
