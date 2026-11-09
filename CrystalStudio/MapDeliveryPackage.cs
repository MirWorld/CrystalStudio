using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace CrystalStudio;

internal sealed record MapAssetReference(int Library, int Image, int Uses);
internal sealed record MapAssetDependency(int Library, string? FileName, long? Length, DateTime? LastWriteUtc, string Status);
internal sealed record MapDeliveryReport(int Version, DateTime CreatedUtc, int Width, int Height, AssetSourceFormat Format,
    bool IncludedUnsavedChanges, bool ArtworkChecked, int TotalIssues, bool Truncated, IReadOnlyList<MapIssue> Issues,
    IReadOnlyList<MapAssetReference> References, IReadOnlyList<MapAssetDependency> Libraries, Dictionary<string, string> FileHashes)
{
    public bool RecipeIncluded { get; init; }
    public bool? RecipeDimensionsMatch { get; init; }
}

internal static class MapDeliveryPackage
{
    internal static IReadOnlyList<MapAssetReference> References(MapDocument document)
    {
        var references = new Dictionary<(int Library, int Image), int>();
        foreach (var pair in document.Cells)
        {
            var cell = pair.Value;
            Add(cell.BackLibrary, cell.BackImage & 0x1FFFFFFF); Add(cell.MiddleLibrary, cell.MiddleImage);
            if (cell.Foreground is { } front)
            {
                int image = front.Image & 0x7FFF; Add(front.Library, image);
                if (image != 0 && (cell.Door & 0x7F) != 0) Add(front.Library, image + cell.DoorOffset);
            }
        }
        return references.OrderBy(pair => pair.Key.Library).ThenBy(pair => pair.Key.Image)
            .Select(pair => new MapAssetReference(pair.Key.Library, pair.Key.Image, pair.Value)).ToArray();

        void Add(int library, int image)
        {
            if (image > 0) references[(library, image)] = references.GetValueOrDefault((library, image)) + 1;
        }
    }

    internal static MapCreationRecipe PublicRecipe(MapCreationRecipe recipe) => recipe with
    {
        LibraryFolder = null,
        LibraryVersions = recipe.LibraryVersions.ToDictionary(pair => pair.Key, pair =>
        {
            string[] parts = pair.Value.Split('|');
            return parts.Length == 3 ? Path.GetFileName(parts[0]) + "|" + parts[1] + "|" + parts[2] : "Identity unavailable";
        })
    };

    internal static async Task ExportAsync(string path, MapDocument snapshot, AssetStore? assets, MapCreationRecipe? recipe,
        MapDesignSettings design, bool unsaved, CancellationToken cancellation = default)
    {
        design.Validate(snapshot.Width, snapshot.Height);
        cancellation.ThrowIfCancellationRequested();
        var check = MapCheck.Run(snapshot, assets?.Libraries, cancellation);
        var references = References(snapshot);
        var dependencies = references.Select(reference => reference.Library).Distinct().Select(library =>
        {
            if (assets == null || !assets.Libraries.TryGetValue(library, out var loaded))
                return new MapAssetDependency(library, null, null, null, assets == null ? "Not checked" : "Missing");
            var file = new FileInfo(loaded.Path);
            return new MapAssetDependency(library, file.Name, file.Exists ? file.Length : null, file.Exists ? file.LastWriteTimeUtc : null, file.Exists ? "Loaded" : "Missing");
        }).ToArray();
        string temporary = Path.Combine(Path.GetTempPath(), "CrystalStudio-delivery-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporary);
        try
        {
            snapshot.Save(Path.Combine(temporary, "map.map")); cancellation.ThrowIfCancellationRequested();
            if (assets != null)
            {
                using var minimap = await MinimapExporter.CreateAsync(snapshot, assets, 1024, 768).ConfigureAwait(false);
                cancellation.ThrowIfCancellationRequested();
                AtomicFile.Write(Path.Combine(temporary, "minimap.png"), minimap.Save);
            }
            if (recipe != null) AtomicFile.WriteJson(Path.Combine(temporary, "map.map.creation.json"), PublicRecipe(recipe));
            AtomicFile.WriteJson(Path.Combine(temporary, "map.map.studio.json"), new MapDesignFile(1, snapshot.Width, snapshot.Height, design.Copy()));
            File.WriteAllText(Path.Combine(temporary, "README.txt"),
                "CrystalStudio map delivery\nThe map is an export snapshot; the original file was not saved or overwritten.\n" +
                "Artwork files are NOT included. Dependency paths are reduced to filenames. Identity metadata is not a content hash.\n" +
                "Creation recipes describe generation, not subsequent manual edits. Identical reconstruction is not guaranteed.\n" +
                (recipe == null ? "No generation recipe is available for this map.\n" : "A sanitized generation recipe is included.\n") +
                (assets == null ? "No artwork libraries were loaded: no minimap and no image-reference verification.\n" : "The minimap and report use the currently loaded libraries.\n") +
                "Route/design markers are editor metadata, not NPC, spawn or server-side configuration.\n", Encoding.UTF8);
            var hashes = Directory.GetFiles(temporary).Order().ToDictionary(file => Path.GetFileName(file), file =>
            {
                cancellation.ThrowIfCancellationRequested(); using var stream = File.OpenRead(file); return Convert.ToHexString(SHA256.HashData(stream));
            });
            var report = new MapDeliveryReport(1, DateTime.UtcNow, snapshot.Width, snapshot.Height, snapshot.SourceFormat, unsaved,
                assets != null, check.Total, check.Total > check.Issues.Count, check.Issues, references, dependencies, hashes)
            { RecipeIncluded = recipe != null, RecipeDimensionsMatch = recipe == null ? null : recipe.Width == snapshot.Width && recipe.Height == snapshot.Height };
            AtomicFile.WriteJson(Path.Combine(temporary, "report.json"), report);
            cancellation.ThrowIfCancellationRequested();
            AtomicFile.Write(path, stream =>
            {
                using var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true);
                foreach (string file in Directory.GetFiles(temporary).Order())
                {
                    cancellation.ThrowIfCancellationRequested();
                    using var input = File.OpenRead(file); using var output = archive.CreateEntry(Path.GetFileName(file), CompressionLevel.Fastest).Open();
                    var buffer = new byte[81920]; int read;
                    while ((read = input.Read(buffer)) > 0) { cancellation.ThrowIfCancellationRequested(); output.Write(buffer, 0, read); }
                }
            });
        }
        finally
        {
            foreach (string file in Directory.GetFiles(temporary)) File.Delete(file);
            Directory.Delete(temporary);
        }
    }
}
