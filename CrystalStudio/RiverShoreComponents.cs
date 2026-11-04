using System.IO;

namespace CrystalStudio;

/// <summary>Reusable native banks and small boundary pieces; no saved shoreline courses.</summary>
public sealed record RiverShoreComponents(RiverCoastPieces Banks, RiverBoundaryComponent EvenEdge, RiverBoundaryComponent OddEdge, RiverHorizontalBoundaryComponent? HorizontalEdge=null)
{
    public static RiverShoreComponents Load(AssetStore assets, CancellationToken cancellation = default)
    {
        var banks = TerrainCoastObjects.LoadWindingPieces(assets, cancellation, bankOnly:true);
        string directory = DataPaths.ResolveEditor("catalogue", "river-coast-layouts", "edges");
        var even = RiverBoundaryComponent.Load(banks, Path.Combine(directory, "south-diagonal-bank-edge.X"));
        var odd = RiverBoundaryComponent.Load(banks, Path.Combine(directory, "south-diagonal-bank-edge-odd.X"));
        var horizontal=RiverHorizontalBoundaryComponent.Load(banks,Path.Combine(directory,"north-bank-horizontal-edge.X"));
        return new(banks.WithBoundary(even).WithBoundary(odd).WithBoundary(horizontal), even, odd, horizontal);
    }
}
