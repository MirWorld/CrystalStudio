namespace CrystalStudio;

internal static class EditorPaths
{
    public static string ProfileDirectory => Environment.GetEnvironmentVariable("CRYSTALSTUDIO_PROFILE") is { Length: > 0 } profile
        ? Path.GetFullPath(profile)
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CrystalStudio");
}
