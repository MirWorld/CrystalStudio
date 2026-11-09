using System.Security.Cryptography;

namespace CrystalStudio.Tests;

public sealed class LegacyGeneratorGoldenTests
{
    [Theory]
    [InlineData("terrain", 1, "6DC24BC48E9DC5903B18C709C734E107E0495287E56D6DD5195C98863D32BEA7")]
    [InlineData("terrain", 27, "F87EE9EA0E8443E2691F316C5CBB083F62A86A2FD547F75C12610987D49719D1")]
    [InlineData("province", 1, "5649251F7E773EDA420DA733DC73FA45CADFBA6EBC1B2EE11B771D61DA3C36CE")]
    [InlineData("province", 27, "C059505003DC77FB25F0CE68606D624DF1CED10703E9FBDD23FA5C3A324E9F2E")]
    [InlineData("dungeon", 1, "AAC748B8030587DE80344E3101F78E0B9640F690C250453E2E6B625D6A38CFE6")]
    [InlineData("dungeon", 27, "D389B876EB380233E4B3044DD0FC9D03086008546F58CD575CE8EAC8ED356285")]
    public void LegacyNativeCellBytesMatchOriginalGenerator(string kind, int seed, string expected)
    {
        var source = new MapDocument(256, 256); var terrain = TerrainCatalogue.Load(); MapDocument result;
        if (kind == "terrain")
        {
            var basis = terrain.Sets.Single(set => set.Id == "0:0:0");
            var patch = terrain.Sets.Single(set => set.Id == "0:0:950");
            var plan = TerrainGenerator.Plan(source, new(0, 0, 256, 256), terrain, new(basis, patch, null, seed));
            result = source;
            foreach (var tile in plan.Tiles) result.Set(tile.X, tile.Y, new Cell { BackLibrary = (short)tile.Library, BackImage = tile.Index + 1 });
        }
        else if (kind == "province")
        {
            ProvinceScenery[] scenery = [new(new BrushTree("Golden tree", "", [new(0, 0, new Cell { Foreground = new(2, 1), BackImage = 0x60000000 })]), SpecialBrushKind.Tree, [ProvinceStyle.All[0].Region])];
            result = ProvinceGenerator.Plan(source, terrain, WallCatalogue.Read(WallCatalogue.ResolvePath()), scenery,
                new(seed, ProvinceStyle.All[0], CliffChance: 0, BeachChance: 0, RiverChance: 0)).Generated;
        }
        else
        {
            var definition = TempleDefinition.All[2];
            var walls = WallCatalogue.Read(WallCatalogue.ResolvePath()).Families.Single(family => family.Id == definition.WallId).WithMode(true);
            result = TempleGenerator.Plan(source, walls, terrain.Sets.Single(set => set.Id == definition.FloorId), new(definition, seed)).Generated;
        }
        string path = Path.Combine(Path.GetTempPath(), "CrystalStudio-algorithm-golden-" + Guid.NewGuid().ToString("N") + ".map");
        try
        {
            result.Save(path);
            using var sample = new MemoryStream();
            using (var writer = new BinaryWriter(sample, System.Text.Encoding.UTF8, leaveOpen: true)) CellCodec.Write(writer, new Cell(), true);
            int nativeLength = checked(8 + result.Width * result.Height * (int)sample.Length);
            string actual = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path).AsSpan(0, nativeLength)));
            Assert.True(expected == actual, "Golden " + kind + " " + seed + " " + actual);
        }
        finally { File.Delete(path); }
    }
}
