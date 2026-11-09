namespace CrystalStudio;

public sealed partial class MapDocument
{
    internal MapDocument Expand(int left, int top, int right, int bottom)
    {
        if (!MapDimensions.CanExpand(Width, Height, left, top, right, bottom))
            throw new ArgumentException("Enter non-negative even expansion amounts within the map size limits.");
        var result = new MapDocument(Width + left + right, Height + top + bottom)
            { SourceFormat = SourceFormat, MetadataWarning = MetadataWarning, PluginId = PluginId, PluginData = (byte[])PluginData.Clone() };
        foreach (var pair in cells) result.Set(pair.Key % Width + left, pair.Key / Width + top, pair.Value.Copy());
        result.RestoreStamps(placedStamps.Select(s => s with { X = s.X + left, Y = s.Y + top }));
        return result;
    }
}
