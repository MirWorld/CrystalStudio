namespace CrystalStudio;

public sealed partial class MapViewport
{
    // Keep the expensive sprite scene independent of hover/selection and other UI redraws.
    private readonly SceneVisual mapScene = new();
    private readonly SceneVisual wallPreviewScene = new();
    private readonly SceneVisual presentationScene = new();
    private readonly SceneVisual interactionScene = new();
    private bool sceneDirty = true;
    private SceneState sceneState;
    private int sceneBuilds;
    private readonly SceneLayer mapSurface = new(true);
    private readonly SceneLayer wallSurface = new(false);
    private readonly SceneLayer presentationSurface = new(false);
    private readonly SceneLayer interactionSurface = new(false);
    internal int MapSceneBuilds => sceneBuilds;
    internal int MapRenderSubmissions => mapSurface.Submissions;
    internal int MapCameraUpdates => mapSurface.CameraUpdates;
    internal int MapWorldRecordings => mapSurface.RecordingBuilds;
    private readonly record struct SceneState(Point Origin, double Zoom, double Width, double Height,
        int Layers, bool Grid, bool LightCells, bool DoorCells, MapLighting Lighting, bool DoorsOpen, long Revision, Brush Paper);
    private void InitializeScene()
    {
        AddSceneLayer(mapSurface); AddSceneLayer(wallSurface);
        AddSceneLayer(presentationSurface); AddSceneLayer(interactionSurface);
    }

    private void UpdateMapScene()
    {
        int layers = RenderLayerSettings;
        var next = new SceneState(origin, Zoom, ActualWidth, ActualHeight, layers, GridVisible,
            LightCellsVisible, DoorCellsVisible, Lighting, DoorsOpen, Document.PickupRevision, paper);
        if (!sceneDirty && next == sceneState) return;
        
        using (var dc = mapScene.RenderOpen()) DrawMapScene(dc);
        using (var dc = presentationScene.RenderOpen()) DrawPresentation(dc);
        sceneState = next; sceneDirty = false; sceneBuilds++;
        object drawingKey = Zoom < .25 ? (object?)overviewResult?.Scene ?? Document : (object?)detailScene ?? Document;
        mapSurface.SetWorldScene(mapScene.Drawing, drawingKey, paper, origin, Zoom, VisibleWorldBounds);
        presentationSurface.SetScene(presentationScene.Drawing);
    }

    private void UpdateInteractionScene()
    {
        using(var preview=wallPreviewScene.RenderOpen()) { }
        using var dc = interactionScene.RenderOpen();
        DrawInteraction(dc);
        wallSurface.SetScene(wallPreviewScene.Drawing);
        interactionSurface.SetScene(interactionScene.Drawing);
    }

    private void RefreshPointerScene()
    {
        if (sceneDirty || panning) InvalidateVisual();
        else UpdateInteractionScene();
    }
}
