namespace CrystalStudio;

public partial class MainWindow
{
    private Point? cellHoverPointer;

    private void InitializeCellHover()
    {
        Viewport.PointerMoved += (_, e) =>
        {
            cellHoverPointer = e.GetPosition(Viewport);
            RefreshCellHover();
        };
        Viewport.PointerExited += (_, _) => HideCellHover();
        Viewport.Hover += (_, _) => RefreshCellHover();
        Viewport.Changed += RefreshCellHover;
        Deactivated += (_, _) => HideCellHover();
        SizeChanged += (_, _) => RefreshCellHover();
    }

    private void HideCellHover()
    {
        cellHoverPointer = null;
        CellHoverInfo.IsVisible = false;
    }

    private void CellHoverClick(object? sender, RoutedEventArgs e) => RefreshCellHover();

    private void RefreshCellHover()
    {
        if (CellHoverToggle.IsChecked != true || cellHoverPointer is not Point pointer ||
            !new Rect(Viewport.Bounds.Size).Contains(pointer))
        {
            CellHoverInfo.IsVisible = false;
            return;
        }
        var (x, y) = Viewport.CellAt(pointer);
        if (!Viewport.Document.Contains(x, y))
        {
            CellHoverInfo.IsVisible = false;
            return;
        }
        UpdateCellInspector(x, y);
        CellHoverInfo.IsVisible = true;
        CellHoverInfo.MaxHeight = Math.Max(1, CellHoverOverlay.Bounds.Height - 16);
        CellHoverInfo.Measure(new Size(CellHoverOverlay.Bounds.Width, CellHoverInfo.MaxHeight));
        if (Viewport.TranslatePoint(pointer, CellHoverOverlay) is not Point position) return;
        double width = CellHoverInfo.DesiredSize.Width, height = CellHoverInfo.DesiredSize.Height;
        double left = position.X + 16;
        // Keep the information readable at the window edges.
        if (left + width > CellHoverOverlay.Bounds.Width - 8) left = position.X - width - 16;
        Canvas.SetLeft(CellHoverInfo, Math.Clamp(left, 8, Math.Max(8, CellHoverOverlay.Bounds.Width - width - 8)));
        Canvas.SetTop(CellHoverInfo, Math.Clamp(position.Y + 8, 8, Math.Max(8, CellHoverOverlay.Bounds.Height - height - 8)));
    }

    private void UpdateCellHoverContent(int x, int y, Cell cell, List<(string Title, string Detail, string Hint)> groups)
    {
        CellHoverCoordinates.Text = LocalText.Format("Cell {0:N0}, {1:N0}", x, y);
        bool blocked = (cell.BackImage & 0x60000000) != 0;
        CellHoverWalkability.Text = LocalText.T(blocked ? "Not walkable" : "Walkable");
        CellHoverWalkability.Foreground = blocked ? Brushes.Red : ThemeService.Brush("ThemeE5E9F0");
        CellHoverDetails.Text = string.Join("\n", groups.Select(group => $"{LocalText.T(group.Title)}: {group.Detail}"));
    }
}
