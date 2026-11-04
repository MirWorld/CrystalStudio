namespace CrystalStudio;

internal sealed record MapEditorState(MapDocument Document, EditHistory History, double Zoom, Point Origin, Rect? Selection);
