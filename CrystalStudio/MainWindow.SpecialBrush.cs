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
            Status.Text = LocalText.Format("Special Brush · {0} · {1} variant(s) · {2}% density · Click or drag to place",dialog.Kind.BrushName(),dialog.Trees.Count,dialog.Options.Density);
            if (dialog.UnavailableTrees > 0) Status.Text += $" · {dialog.UnavailableTrees} unavailable variants skipped";
            Viewport.Focus();
        }
        finally { specialBrushWindow = null; }
    }
}
