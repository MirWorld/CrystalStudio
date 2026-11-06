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
        return Path.Combine(Path.GetFullPath(directory), FileName(name));
    }

    public static string AvailableName(string directory, string name)
    {
        string stem = name.EndsWith(".map", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;
        for (int number = 1; number < 10000; number++)
        {
            string candidate = number == 1 ? stem : $"{stem}_{number}";
            string path = Target(directory, candidate);
            if (!File.Exists(path) && !System.IO.Directory.Exists(path)) return candidate;
        }
        return stem + "_" + Guid.NewGuid().ToString("N")[..8];
    }
}
