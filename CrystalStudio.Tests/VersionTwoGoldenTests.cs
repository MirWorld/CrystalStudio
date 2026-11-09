using System.Security.Cryptography;

namespace CrystalStudio.Tests;

public sealed class VersionTwoGoldenTests
{
    [Theory]
    [InlineData("terrain", 1, "6DC24BC48E9DC5903B18C709C734E107E0495287E56D6DD5195C98863D32BEA7")]
    [InlineData("terrain", 27, "E39253DF812B1F22A7D6EA4A84A90231D3841977AAF6E995AE00DBBCBCABD999")]
    [InlineData("province", 1, "E1F6135DED0EB3AA90CEB8D3684EE1CC2B981EE120BF0B3210DD6ED2F8572208")]
    [InlineData("province", 27, "4E1A2A34152F3B5F8B28A3652DA1EAF4A22DA8C608998A82F0FFC2983E309C95")]
    [InlineData("dungeon", 1, "40400A9525FE44D3540BE270CC30160C02FCEEB4D34A4657EF0DBA4B4D1ADC94")]
    [InlineData("dungeon", 27, "EEE8CE1E35120740CBEB779203BC4117EB12637B839A1F6D94EAD2B35CF6E0A9")]
    public void VersionTwoMatchesIndependentlyCompiledOriginalSource(string kind, int seed, string expected)
    {
        var source = new MapDocument(256, 256); var terrain = TerrainCatalogue.Load(); MapDocument result;
        if (kind == "terrain") result = GeneratedMapSnapshot.Terrain(TerrainGenerator.Plan(source, new(0, 0, 256, 256), terrain,
            new(terrain.Sets.Single(set => set.Id == "0:0:0"), terrain.Sets.Single(set => set.Id == "0:0:950"), null, seed) { AlgorithmVersion = 2 }));
        else if (kind == "province") result = ProvinceGenerator.Plan(source, terrain, WallCatalogue.Read(WallCatalogue.ResolvePath()), [],
            new(seed, ProvinceStyle.All[0], CliffChance: 0, BeachChance: 0, RiverChance: 0, Vegetation: 0) { AlgorithmVersion = 2 }).Generated;
        else
        {
            var definition = TempleDefinition.All[2];
            result = TempleGenerator.Plan(source, WallCatalogue.Read(WallCatalogue.ResolvePath()).Families.Single(family => family.Id == definition.WallId).WithMode(true),
                terrain.Sets.Single(set => set.Id == definition.FloorId), new(definition, seed) { AlgorithmVersion = 2 }).Generated;
        }
        string path = Path.Combine(Path.GetTempPath(), "CrystalStudio-v2-golden-" + Guid.NewGuid().ToString("N") + ".map");
        try
        {
            result.Save(path); using var sample = new MemoryStream();
            using (var writer = new BinaryWriter(sample, System.Text.Encoding.UTF8, true)) CellCodec.Write(writer, new Cell(), true);
            int length = checked(8 + result.Width * result.Height * (int)sample.Length);
            Assert.Equal(expected, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path).AsSpan(0, length))));
        }
        finally { File.Delete(path); }
    }
}
