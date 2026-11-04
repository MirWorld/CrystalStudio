namespace CrystalStudio;

internal static class PopupHeight
{
    internal static void Fit(EditorWindow window)
    {
        if (window is MainWindow) return;
        var screen = window.Screens.ScreenFromWindow(window);
        if (screen == null) return;
        double limit = screen.WorkingArea.Height / window.RenderScaling - 48;
        window.MinHeight = Math.Min(window.MinHeight, limit);
        window.MaxHeight = Math.Min(window.MaxHeight, limit);
        if (window.Content is StackPanel content)
        {
            window.Content = null;
            window.Content = new ScrollViewer { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        }
    }
}
