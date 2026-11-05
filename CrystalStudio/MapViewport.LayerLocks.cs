namespace CrystalStudio;

public sealed partial class MapViewport
{
    private readonly bool[] layerLocks = new bool[5];
    public bool IsLayerLocked(MapLayer layer) => layerLocks[(int)layer];
    public bool AnyLayerLocked => layerLocks.Any(value => value);
    public void SetLayerLocked(MapLayer layer, bool locked)
    {
        if (IsLayerLocked(layer) == locked) return;
        CancelInteraction();
        layerLocks[(int)layer] = locked;
        Changed?.Invoke();
    }
    private bool AllowEdit(MapLayer? layer = null)
    {
        if (layer.HasValue ? !IsLayerLocked(layer.Value) : !AnyLayerLocked) return true;
        Message?.Invoke(LocalText.T(layer.HasValue ? "This layer is locked." : "Unlock all layers before editing multiple layers."));
        return false;
    }
    private void RequireAllLayersEditable()
    {
        if (AnyLayerLocked) throw new InvalidOperationException(LocalText.T("Unlock all layers before editing multiple layers."));
    }
}
