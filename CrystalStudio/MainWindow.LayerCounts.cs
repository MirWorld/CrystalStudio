namespace CrystalStudio;

public partial class MainWindow
{
    private void UpdateLayerCounts()
    {
        var document = Viewport.Document;
        GroundLayerCount.Text = $" ({document.LayerCount(MapLayer.Ground):N0})";
        MiddleLayerCount.Text = $" ({document.LayerCount(MapLayer.Middle):N0})";
        ObjectsLayerCount.Text = $" ({document.LayerCount(MapLayer.Objects):N0})";
        LowCollisionLayerCount.Text = $" ({document.LayerCount(MapLayer.LowCollision):N0})";
        HighCollisionLayerCount.Text = $" ({document.LayerCount(MapLayer.HighCollision):N0})";
    }
}
