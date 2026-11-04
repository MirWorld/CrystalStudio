namespace CrystalStudio;

public enum SpecialBrushKind { Tree, Bush, Rock }

public static class SpecialBrushKinds
{
    public static string Singular(this SpecialBrushKind kind) => kind.ToString().ToLowerInvariant();
    public static string Plural(this SpecialBrushKind kind) => kind == SpecialBrushKind.Bush ? "bushes" : kind.Singular() + "s";
    public static string BrushName(this SpecialBrushKind kind) => kind + " Brush";
    public static string ListExtension(this SpecialBrushKind kind) => "." + kind.Singular() + "list";
    public static bool Matches(this SpecialBrushKind kind, CategorizedStamp stamp) => kind switch
    {
        SpecialBrushKind.Bush => string.Equals(stamp.Type, "Bush", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(stamp.Type, "Plant", StringComparison.OrdinalIgnoreCase) &&
            stamp.Subtype?.Contains("bush", StringComparison.OrdinalIgnoreCase) == true,
        _ => string.Equals(stamp.Type, kind.ToString(), StringComparison.OrdinalIgnoreCase)
    };
}

public sealed record SpecialBrushState(TreeBrushOptions Options, int Size, IReadOnlyList<BrushTree> Variants,
    IReadOnlyList<TreeBrushSource> List, string ListName, string Search = "", IReadOnlyList<string>? SelectedPaths = null);
