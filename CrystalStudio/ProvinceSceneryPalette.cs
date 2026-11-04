using System.IO;
using System.Text.Json;

namespace CrystalStudio;

internal static class ProvinceSceneryPalette
{
    private static readonly Lazy<Dictionary<string, string?>> GroundReview = new(() =>
    {
        using var document = JsonDocument.Parse(File.ReadAllText(DataPaths.ResolveEditor("catalogue", "province-decoration-ground.json")));
        if (document.RootElement.GetProperty("schemaVersion").GetInt32() != 1)
            throw new InvalidDataException("Unsupported province decoration ground review.");
        return document.RootElement.GetProperty("objects").EnumerateArray().ToDictionary(
            s => s.GetProperty("id").GetString()!, s => s.GetProperty("status").GetString() switch
            {
                "freestanding" => null,
                "matched" => s.GetProperty("groundSetId").GetString() ?? throw new InvalidDataException("Missing decoration ground set."),
                "excluded" => "unverified",
                _ => throw new InvalidDataException("Invalid decoration ground review status.")
            });
    });

    internal static bool HasGroundReview(string id) => GroundReview.Value.ContainsKey(id);

    // Unknown catalogue objects fail closed; only explicitly inspected
    // freestanding artwork may bypass the ground compatibility check.
    internal static string? GroundSetId(string id) => GroundReview.Value.GetValueOrDefault(id, "unverified");

    // Reviewed against native sprites, including transparent sandstone spires
    // and boulders from other maps. Regional occurrence tags
    // also include the province's wooded areas and formations with opaque sand
    // bases. These complete native objects have transparent surroundings.
    private static readonly HashSet<string> DesertObjects =
    [
        "mir/mir_00182", "mir/mir_00183", "mir/mir_00185",
        "mir/mir_00186", "mir/mir_00187", "mir/mir_00188",
        "mir/mir_00189", "mir/mir_00190", "mir/mir_00191",
        "mir/mir_00229", "mir/mir_00230", "mir/mir_00232",
        "mir/mir_00480", "mir/mir_00495",
        "mir/mir_00699", "mir/mir_00700", "mir/mir_00701", "mir/mir_00702",
        "mir/mir_00703", "mir/mir_00704", "mir/mir_00705", "mir/mir_00706",
        "mir/mir_00707", "mir/mir_00708", "mir/mir_00709", "mir/mir_00710",
        "mir/mir_00711", "mir/mir_00712",
        "mir/mir_00743", "mir/mir_00744", "mir/mir_00745"
    ];

    internal static bool IsDesertObject(string id) => DesertObjects.Contains(id);

    internal static BrushTree[] Select(IReadOnlyList<ProvinceScenery> scenery,
        ProvinceStyle style, SpecialBrushKind kind, bool boundary = false)
    {
        var regional = scenery.Where(s => s.Kind == kind && s.Regions.Contains(style.Region));
        if (style.WallId == "mongchon-province")
        {
            var selected = scenery.Where(s => s.Kind == kind && (s.CatalogueId == null
                ? s.Regions.Contains(style.Region) : IsDesertObject(s.CatalogueId))).ToArray();
            // Larger boulders and spires form the barrier; smaller stones fill
            // the gaps. Keep the interior's scattering independent of this mix.
            var large = boundary ? selected.Where(s => s.CatalogueId is
                "mir/mir_00699" or "mir/mir_00700" or "mir/mir_00743" or "mir/mir_00744" or "mir/mir_00745").ToArray() : [];
            return selected.Concat(Enumerable.Range(0, 3).SelectMany(_ => large)).Select(s => s.Tree).ToArray();
        }
        return regional.Select(s => s.Tree).ToArray();
    }
}
