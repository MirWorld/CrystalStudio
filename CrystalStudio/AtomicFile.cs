using System.IO;
using System.Text.Json;

namespace CrystalStudio;

internal static class AtomicFile
{
    public static void Write(string path, Action<Stream> write, bool backup = false)
    {
        path = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                write(stream);
                stream.Flush(flushToDisk: true);
            }
            if (backup && File.Exists(path)) File.Replace(temporary, path, path + ".bak");
            else File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    public static void WriteJson<T>(string path, T value) =>
        Write(path, stream => StudioJson.Serialize(stream, value));
}
