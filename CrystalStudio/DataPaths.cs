using System.IO;

namespace CrystalStudio;

/// <summary>Resolves editor content and normalizes client folder paths for catalogue tooling.</summary>
public static class DataPaths
{
    /// <summary>Converts legacy Data selections to the owning client root; standalone libraries remain valid.</summary>
    public static string GetClientRoot(string folder)
    {
        string full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
        for (var directory = new DirectoryInfo(full); directory != null; directory = directory.Parent)
            if (directory.Name.Equals("Data", StringComparison.OrdinalIgnoreCase) && directory.Parent is { } parent)
                return parent.FullName;
        var selected = new DirectoryInfo(full);
        if (selected.Name.Equals("Map", StringComparison.OrdinalIgnoreCase) && selected.Parent is { } client &&
            Directory.Exists(Path.Combine(client.FullName, "Data"))) return client.FullName;
        return full;
    }

    public static string GetRoot(string folder)
    {
        string full = ResolveLibraryFolder(folder);
        // Settings also accepts Data/Map and its library family subfolders.
        for (var directory = new DirectoryInfo(full); directory != null; directory = directory.Parent)
            if (directory.Name.Equals("Data", StringComparison.OrdinalIgnoreCase)) return directory.FullName;
        return full;
    }

    // 根目录中的 Data 优先用于素材；兼容原先直接选择 Data、Data/Map 或平铺素材目录。
    public static string ResolveLibraryFolder(string folder)
    {
        string full = Path.GetFullPath(folder);
        string data = Path.Combine(full, "Data");
        return Directory.Exists(data) ? data : full;
    }

    public static string? FindMapFolder(string folder)
    {
        string full = Path.GetFullPath(folder);
        string map = Path.Combine(full, "Map");
        if (Directory.Exists(Path.Combine(full, "Data")))
            return Directory.Exists(map) ? map : null;

        // 旧设置选择 Data 或其子目录时，地图位于 Data 的同级 Map，避免误扫素材库。
        string data = GetRoot(full);
        var directory = new DirectoryInfo(data);
        if (directory.Name.Equals("Data", StringComparison.OrdinalIgnoreCase) && directory.Parent is { } parent)
        {
            string sibling = Path.Combine(parent.FullName, "Map");
            if (Directory.Exists(sibling)) return sibling;
        }
        if (new DirectoryInfo(full).Name.Equals("Map", StringComparison.OrdinalIgnoreCase) && Directory.Exists(full))
            return full;
        return Directory.Exists(map) ? map : null;
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
