using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace CrystalStudio;

// This ABI uses only C functions, fixed-width integers and plugin-owned buffers.
// No runtime assembly loading or generated marshalling is needed by NativeAOT.
internal sealed partial class NativeMapPlugin
{
    [StructLayout(LayoutKind.Sequential)]
    private struct Buffer { public IntPtr Data; public ulong Size; }
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate uint Version();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr Info();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int Probe(IntPtr data, ulong size);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int Transform(IntPtr data, ulong size, out Buffer output, out Buffer error);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void Free(ref Buffer buffer);
    private readonly IntPtr handle;
    private readonly Probe? probe;
    private readonly Transform? read, write;
    private readonly Free free;
    private readonly object gate = new();
    internal const int MaximumBytes = 1_000_000_000;
    internal string Id { get; }
    internal string Name { get; }
    internal string[] Extensions { get; }
    internal bool CanRead => read != null;
    internal bool CanWrite => write != null;
    internal string FilePath { get; }
    public override string ToString() => Name;

    internal NativeMapPlugin(string path)
    {
        FilePath = Path.GetFullPath(path);
        handle = OperatingSystem.IsWindows() ? LoadLibraryEx(FilePath, IntPtr.Zero, 0x00000100 | 0x00001000) : NativeLibrary.Load(FilePath);
        if (handle == IntPtr.Zero)
        {
            int error = Marshal.GetLastPInvokeError();
            throw new System.ComponentModel.Win32Exception(error, $"Cannot load map plugin: {FilePath}\n" + new System.ComponentModel.Win32Exception(error).Message);
        }
        try
        {
            if (Export<Version>("cs_map_api_version")() != 1) throw new InvalidDataException("Unsupported map plugin ABI version.");
            using var info = JsonDocument.Parse(Marshal.PtrToStringUTF8(Export<Info>("cs_map_info")()) ?? throw new InvalidDataException("Missing plugin information."));
            var root = info.RootElement;
            Id = root.GetProperty("id").GetString()!; Name = root.GetProperty("name").GetString()!;
            Extensions = root.GetProperty("extensions").EnumerateArray().Select(e => e.GetString()!).ToArray();
            if (string.IsNullOrWhiteSpace(Id) || Id == "crystal" || Id.Length > 128 || string.IsNullOrWhiteSpace(Name) || Name.Contains('|') ||
                Extensions.Length == 0 || Extensions.Any(e => string.IsNullOrEmpty(e) || e[0] != '.' || e.Length < 2 || e.Skip(1).Any(c => !char.IsAsciiLetterOrDigit(c))))
                throw new InvalidDataException("Invalid plugin ID, name or file extensions.");
            free = Export<Free>("cs_map_free");
            if (root.GetProperty("canRead").GetBoolean()) { probe = Export<Probe>("cs_map_probe"); read = Export<Transform>("cs_map_read"); }
            if (root.GetProperty("canWrite").GetBoolean()) write = Export<Transform>("cs_map_write");
            if (!CanRead && !CanWrite) throw new InvalidDataException("Plugin has no read or write capability.");
        }
        catch { NativeLibrary.Free(handle); throw; }
    }

    private T Export<T>(string name) where T : Delegate => Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(handle, name));
    [LibraryImport("kernel32.dll", EntryPoint = "LoadLibraryExW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial IntPtr LoadLibraryEx(string path, IntPtr file, uint flags);
    internal void ReleaseRejected() => NativeLibrary.Free(handle);

    internal unsafe bool Matches(byte[] bytes)
    {
        if (probe == null) return false;
        lock (gate) fixed (byte* data = bytes)
        {
            int result = probe((IntPtr)data, (ulong)bytes.Length);
            if (result is not (0 or 1)) throw new InvalidDataException($"{Name}: format recognition failed ({result}).");
            return result == 1;
        }
    }

    internal byte[] Read(byte[] bytes) => Convert(bytes, read ?? throw new NotSupportedException($"{Name}: reading is not supported."));
    internal byte[] Write(byte[] bytes) => Convert(bytes, write ?? throw new NotSupportedException($"{Name}: writing is not supported. Use Save As to choose another format."));

    private unsafe byte[] Convert(byte[] bytes, Transform transform)
    {
        lock (gate) fixed (byte* data = bytes)
        {
            Buffer output = default, error = default;
            try
            {
                int result = transform((IntPtr)data, (ulong)bytes.Length, out output, out error);
                if (result != 0) throw new InvalidDataException($"{Name}: " + (error.Size == 0 ? $"operation failed ({result})." : Encoding.UTF8.GetString(Copy(error))));
                return Copy(output);
            }
            finally { free(ref output); free(ref error); }
        }
    }

    private static byte[] Copy(Buffer buffer)
    {
        if (buffer.Size > MaximumBytes || (buffer.Size != 0 && buffer.Data == IntPtr.Zero)) throw new InvalidDataException("Invalid or excessive plugin buffer.");
        var bytes = new byte[(int)buffer.Size];
        if (bytes.Length != 0) Marshal.Copy(buffer.Data, bytes, 0, bytes.Length);
        return bytes;
    }
}
