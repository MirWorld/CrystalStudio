using System.IO;

namespace CrystalStudio;

/// <summary>Resolves editor content and normalizes client folder paths for catalogue tooling.</summary>
public static class DataPaths
{
    public static string GetRoot(string folder)
    {
        string full = Path.GetFullPath(folder);
        // Settings also accepts Data/Map and its library family subfolders.
        for (var directory = new DirectoryInfo(full); directory != null; directory = directory.Parent)
            if (directory.Name.Equals("Data", StringComparison.OrdinalIgnoreCase)) return directory.FullName;
        return full;
    }

    /// <summary>The editor root owns catalogue and Objects folders, independently of the selected client data folder.</summary>
    public static string EditorRoot { get; } = FindEditorRoot();

    private static string FindEditorRoot()
    {
        // A published package owns its data even when it is launched from inside a checkout.
        string executableRoot = Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory);
        if (Directory.Exists(Path.Combine(executableRoot, "Objects")) &&
            File.Exists(Path.Combine(executableRoot, "catalogue", "palette.json"))) return executableRoot;
        for (var directory = new DirectoryInfo(executableRoot); directory != null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "CrystalStudio", "CrystalStudio.csproj")) &&
                Directory.Exists(Path.Combine(directory.FullName, "catalogue"))) return directory.FullName;
        return executableRoot;
    }

    public static string ResolveEditor(params string[] parts) =>
        Path.GetFullPath(Path.Combine(new[] { EditorRoot }.Concat(parts).ToArray()));
}
