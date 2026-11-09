using CrystalStudio;

if (args.Length != 1) throw new ArgumentException("Usage: AotSmoke <example.plugin.dll>");
var plugin = new NativeMapPlugin(args[0]);
using var stream = new MemoryStream();
using (var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true))
{
    writer.Write("CSMP"u8); writer.Write(1); writer.Write(8); writer.Write(6);
    writer.Write(0); writer.Write(0); writer.Write(0);
}
byte[] original = stream.ToArray(), encoded = plugin.Write(original);
if (!plugin.Matches(encoded) || !plugin.Read(encoded).AsSpan().SequenceEqual(original))
    throw new InvalidDataException("NativeAOT native plugin round trip failed.");
try { plugin.Read("invalid"u8.ToArray()); throw new Exception("Expected native plugin error."); }
catch (InvalidDataException error) { Console.WriteLine("Expected plugin error: " + error.Message); }
Console.WriteLine("NativeAOT C++ DLL probe/read/write/error/free: PASS");
