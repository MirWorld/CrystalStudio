using System.IO;

namespace CrystalStudio;

internal sealed record ModuleProblem(string Code, string Message, string? ModuleId = null,
    string? ConnectionId = null, int? X = null, int? Y = null);

internal static class ModuleDiagnostics
{
    private const string Key = "CrystalStudio.ModuleProblem";
    internal static InvalidDataException Error(string code, string message, ModuleConnection? point = null, int? x = null, int? y = null)
    {
        var error = new InvalidDataException(message);
        error.Data[Key] = new ModuleProblem(code, message, ConnectionId: point?.Id, X: x ?? point?.X, Y: y ?? point?.Y);
        return error;
    }
    internal static ModuleProblem? Read(Exception? error) => error == null ? null : error.Data[Key] as ModuleProblem ?? Read(error.InnerException);
    internal static void Identify(Exception error, string moduleId) =>
        error.Data[Key] = (Read(error) ?? new ModuleProblem("module", error.Message)) with { ModuleId = moduleId };
    internal static ModuleProblem? Check(ModuleConnections connections, IReadOnlyList<StampCell> cells,
        IReadOnlyDictionary<(long, long), Cell>? cachedCells = null, ModuleConnectionRules.Bounds? cachedBounds = null)
    {
        try { ModuleConnectionRules.Validate(connections, cells, cachedCells, cachedBounds); return null; }
        catch (InvalidDataException error) { return Read(error) ?? new("module", error.Message); }
    }
}
