using System.Globalization;
using System.IO;

namespace CrystalStudio;

internal sealed record MapCreationSettings(MapCreationKind Kind = MapCreationKind.Blank, int Width = 256,
    int Height = 256, string Name = "NewMap", string? Directory = null, bool SaveImmediately = true);

internal static class MapCreationPaths
{
    public static string FileName(string name) => name.EndsWith(".map", StringComparison.OrdinalIgnoreCase) ? name : name + ".map";
    public static string DefaultDirectory(string? libraryFolder) => string.IsNullOrWhiteSpace(libraryFolder)
        ? Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)
        : DataPaths.FindMapFolder(libraryFolder) ?? Path.Combine(DataPaths.GetClientRoot(libraryFolder), "Map");

    public static void ValidateName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name != name.Trim() || name.EndsWith('.') ||
            name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name is "." or "..")
            throw new ArgumentException("Enter a valid map filename without a folder path.");
        string stem = name.Split('.')[0].ToUpperInvariant();
        if (stem is "CON" or "PRN" or "AUX" or "NUL" ||
            stem.Length == 4 && (stem.StartsWith("COM") || stem.StartsWith("LPT")) && stem[3] is >= '0' and <= '9')
            throw new ArgumentException("Enter a valid map filename without a folder path.");
    }

    public static string Target(string directory, string name)
    {
        ValidateName(name);
        if (string.IsNullOrWhiteSpace(directory)) throw new ArgumentException("Choose an output folder.");
        string folder = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        for (string? parent = folder; parent != null; parent = Path.GetDirectoryName(parent))
            if (File.Exists(parent)) throw new ArgumentException("The output folder or one of its parents is a file. Choose another folder.");
        return Path.Combine(folder, FileName(name));
    }

    public static string AvailableName(string directory, string name)
    {
        string stem = name.EndsWith(".map", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;
        bool Available(string candidate)
        {
            string path = Target(directory, candidate);
            return !File.Exists(path) && !System.IO.Directory.Exists(path);
        }
        if (Available(stem)) return stem;
        long next = 2;
        int separator = stem.LastIndexOf('_');
        if (separator > 0 && int.TryParse(stem.AsSpan(separator + 1), NumberStyles.None, CultureInfo.InvariantCulture, out int suffix) && suffix >= 1)
        {
            stem = stem[..separator]; next = (long)suffix + 1;
        }
        for (int offset = 0; offset < 9999; offset++)
        {
            string candidate = stem + "_" + (next + offset).ToString(CultureInfo.InvariantCulture);
            if (Available(candidate)) return candidate;
        }
        return stem + "_" + Guid.NewGuid().ToString("N")[..8];
    }
}
