namespace CrystalStudio;

internal sealed record MapSizeChoice(int Width, int Height, string Purpose);
internal sealed record MapCreationTemplate(string Id, string Title, string Description, MapCreationKind Kind,
    int Width, int Height, Dictionary<string, string> Parameters)
{
    public override string ToString() => LocalText.T(Title);
}

internal static class MapCreationTemplates
{
    public static IReadOnlyList<MapCreationTemplate> All { get; } =
    [
        new("material-test", "Material test area", "An empty 128 × 128 map for checking artwork and collisions. No game artwork is required.",
            MapCreationKind.Blank, 128, 128, []),
        new("outdoor-exploration", "Outdoor exploration", "A medium woodland map with moderate vegetation and a chance of a river. Exact routes depend on generation.",
            MapCreationKind.Province, 256, 256, new()
            {
                ["StyleList"] = "province:bichon-province", ["ForestSlider"] = "85", ["CliffSlider"] = "40",
                ["BeachSlider"] = "0", ["RiverSlider"] = "35", ["VegetationSlider"] = "35",
                ["TownCountSlider"] = "0", ["DoorCountSlider"] = "0", ["SeedBox"] = "1", [GenerationAlgorithms.Parameter] = "3"
            }),
        new("town-surroundings", "Town surroundings", "Light vegetation and one requested town. Placement depends on available artwork and space; no fixed area is reserved.",
            MapCreationKind.Province, 512, 512, new()
            {
                ["StyleList"] = "province:bichon-province", ["ForestSlider"] = "50", ["CliffSlider"] = "0",
                ["BeachSlider"] = "0", ["RiverSlider"] = "0", ["VegetationSlider"] = "20",
                ["TownCountSlider"] = "1", ["DoorCountSlider"] = "0", ["SeedBox"] = "1", [GenerationAlgorithms.Parameter] = "3"
            }),
        new("small-dungeon", "Small dungeon", "A compact stone dungeon with six requested rooms and wide corridors. Check the preview for actual room placement.",
            MapCreationKind.Dungeon, 128, 128, new()
            {
                ["TempleList"] = "temple:stone-temple", ["DungeonTypeList"] = "Temple", ["RoomsSlider"] = "6",
                ["SizeSlider"] = "3", ["WidthList"] = "index:1", ["DoorCountSlider"] = "0", ["SeedBox"] = "1", [GenerationAlgorithms.Parameter] = "3"
            })
    ];

    public static IReadOnlyList<MapSizeChoice> Sizes(MapCreationKind kind) => new MapSizeChoice[]
    {
        new(64, 64, "Compact test"), new(128, 128, "Small area"), new(256, 256, "Medium area"),
        new(512, 512, "Large area"), new(1000, 1000, "Large generated map"),
        new(1024, 1024, "Large area"), new(2048, 2048, "Large blank map"),
        new(256, 128, "Wide area"), new(128, 256, "Tall area")
    }.Where(size => MapDimensions.CreationError(size.Width, size.Height, kind) == null).ToArray();

    public static string KindLabel(MapCreationKind kind) => LocalText.T(kind switch
    {
        MapCreationKind.Blank => "Blank map",
        MapCreationKind.Terrain => "Base terrain",
        MapCreationKind.Province => "Outdoor map",
        MapCreationKind.Dungeon => "Dungeon map",
        _ => "Map type"
    });
}
