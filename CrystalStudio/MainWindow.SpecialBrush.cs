namespace CrystalStudio;

public partial class MainWindow
{
    private SpecialBrushWindow? specialBrushWindow;
    private IReadOnlyDictionary<SpecialBrushKind, SpecialBrushState> specialBrushStates = new Dictionary<SpecialBrushKind, SpecialBrushState>();

    private void SpecialBrushClick(object sender, RoutedEventArgs e)
    {
        if (busy) return;
        if (specialBrushWindow != null) { specialBrushWindow.Activate(); return; }
        var kind = Viewport.SpecialBrushKind;
        var previous = specialBrushStates.GetValueOrDefault(kind);
        var dialog = new SpecialBrushWindow(assets, Viewport.TreeOptions,
            Viewport.Trees.Count == 0 ? 9 : Viewport.BrushSize, Viewport.Trees, previous?.List, previous?.ListName ?? "My " + kind.Plural(),
            kind, specialBrushStates) { Owner = this };
        specialBrushWindow = dialog;
        try
        {
            if (dialog.ShowDialog() != true) { UpdateWorkspace(); return; }
            Viewport.TreeOptions = dialog.Options; Viewport.Trees = dialog.Trees;
            Viewport.SpecialBrushKind = dialog.Kind; specialBrushStates = dialog.BrushStates;
            LayerList.SelectedIndex = (int)MapLayer.Objects;
            ObjectsVisible.IsChecked = true;
            BrushSize.Value = dialog.BrushSize;
            SetTool(EditorTool.SpecialBrush);
            Status.Text = $"Special Brush · {dialog.Kind.BrushName()} · {dialog.Trees.Count} variant(s) · {dialog.Options.Density}% density · Click or drag to place";
            if (dialog.UnavailableTrees > 0) Status.Text += $" · {dialog.UnavailableTrees} unavailable variants skipped";
            Viewport.Focus();
        }
        finally { specialBrushWindow = null; }
    }
}
