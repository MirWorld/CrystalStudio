namespace CrystalStudio;

public partial class MainWindow
{
    private sealed class OpenMapTab(MapEditorState state, string? path, string name)
    {
        public MapEditorState State = state;
        public string? Path = path;
        public string Name = name;
        public bool FitOnFirstDisplay = true;
        public bool KeepSourceFormat;
        public MapCreationRecipe? Recipe;
        public readonly Guid RecoveryId = Guid.NewGuid();
        public long RecoveryRevision = -1;
        public int RecoveryVersion;
        public MapRecoveryEntry? RecoveryOrigin;
        public TextBlock Label = new(); public bool IsStamp; public int StampOriginX, StampOriginY; public CategorizedStamp? Category; public bool CategoryDirty;
    }

    private readonly TabControl mapTabs = new() { Name = "MapTabs" };
    private OpenMapTab? activeMap;
    private bool switchingMap;
    private int untitledNumber;

    private void InitializeMapTabs()
    {
        var canvas = (Grid)Viewport.Parent;
        canvas.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        canvas.RowDefinitions.Add(new RowDefinition());
        foreach (Control child in canvas.Children) Grid.SetRow(child, 1);
        // Only the headers belong to the tab control. All maps share one renderer below it.
        mapTabs.ItemsPanel = new FuncTemplate<Panel?>(() => new StackPanel { Orientation = Orientation.Horizontal });
        mapTabs.Template = new FuncControlTemplate<TabControl>((control, scope) =>
            new ScrollViewer { HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Content = new ItemsPresenter { Name = "PART_ItemsPresenter", ItemsPanel = control.ItemsPanel } });
        mapTabs.SetResource(TemplatedControl.BackgroundProperty, "Theme1C2028");
        mapTabs.ItemContainerTheme = (Style)this.FindResource("PaletteTabItem");
        mapTabs.SelectionChanged += (_, e) =>
        {
            if (e.Source != mapTabs || switchingMap || mapTabs.SelectedItem is not TabItem tab) return;
            SwitchMap((OpenMapTab)tab.Tag);
        };
        canvas.Children.Add(mapTabs);
        InitializeMapTabDragging();
        Viewport.SizeChanged += (_, _) => FitNewMap();
        AddMapTab(Viewport.Document); InitializeStampEditing(canvas);
        Viewport.Changed += UpdateMapTabLabels;
    }

    private void AddMapTab(MapDocument document, string? path = null)
    {
        var map = new OpenMapTab(new(document, new(), 1, new Point(60, 70), null), path, $"Untitled {++untitledNumber}");
        var header = new StackPanel { Orientation = Orientation.Horizontal };
        map.Label.Margin = new Thickness(4, 2, 8, 2);
        map.Label.VerticalAlignment = VerticalAlignment.Center;
        header.Children.Add(map.Label);
        var close = new Button { Content = ("\u00d7"), Padding = new Thickness(4, 0, 4, 0), Background = Brushes.Transparent, BorderThickness = new Thickness(0) };
        ToolTip.SetTip(close, LocalText.T("Close Map")); close.Theme = (Style)this.FindResource("QuietButton");
        var tab = new TabItem { Header = header, Tag = map };
        tab.ContextMenu = CreateMapTabMenu(tab);
        close.Click += (_, e) => { e.Handled = true; CloseMapTab(tab); };
        header.Children.Add(close);
        mapTabs.Items.Add(tab);
        mapTabs.SelectedItem = tab;
        UpdateMapTabLabels();
    }

    private void SwitchMap(OpenMapTab map)
    {
        if (ReferenceEquals(activeMap, map)) return;
        switchingMap = true;
        try
        {
            if (activeMap != null) activeMap.State = Viewport.CaptureEditorState();
            activeMap = map; stampProperties.IsVisible = map.IsStamp ? true : false;
            Viewport.RestoreEditorState(map.State);
            SyncLayerLocks();
            FitNewMap();
            inspectorCell = null;
            UpdateCellInspector(-1, -1);
            Coordinates.Text = ("- , -");
            Status.Text = MapDisplayName(map);
            UpdateLabels();
        }
        finally { switchingMap = false; }
        UpdateMapTabLabels();
    }

    private void FitNewMap()
    {
        if (activeMap is not { FitOnFirstDisplay: true } map || Viewport.Bounds.Width <= 0 || Viewport.Bounds.Height <= 0) return;
        map.FitOnFirstDisplay = false;
        Viewport.Fit();
    }

    private bool CloseMapTab(TabItem tab)
    {
        if (busy || !mapTabs.Items.Contains(tab)) return false;
        mapTabs.SelectedItem = tab;
        if (!ConfirmDiscard()) return false;
        ForgetRecovery((OpenMapTab)tab.Tag!);
        switchingMap = true;
        try { activeMap = null; mapTabs.Items.Remove(tab); }
        finally { switchingMap = false; }
        if (mapTabs.Items.Count == 0) AddMapTab(new(256, 256));
        else
        {
            if (mapTabs.SelectedItem is not TabItem) mapTabs.SelectedIndex = 0;
            SwitchMap((OpenMapTab)((TabItem)mapTabs.SelectedItem).Tag);
        }
        return true;
    }

    private void UpdateMapTabLabels()
    {
        foreach (TabItem tab in mapTabs.Items)
        {
            var map = (OpenMapTab)tab.Tag;
            bool dirty = map.CategoryDirty || (ReferenceEquals(map, activeMap) ? Viewport.History.Dirty : map.State.History.Dirty);
            (map.Label).Text = string.Concat(MapDisplayName(map), (dirty ? " *" : ""));
            ToolTip.SetTip(tab, map.Path == null ? MapDisplayName(map) : (map.Path));
        }
    }

    private static string MapDisplayName(OpenMapTab map)
    {
        if (map.Path != null) return (System.IO.Path.GetFileName(map.Path));
        string prefix = map.IsStamp ? "Untitled stamp " : "Untitled ";
        return map.Name.StartsWith(prefix, StringComparison.Ordinal) && int.TryParse(map.Name[prefix.Length..], out int number)
            ? LocalText.Format(map.IsStamp ? "Untitled stamp {0}" : "Untitled {0}", number) : (map.Name);
    }
}
