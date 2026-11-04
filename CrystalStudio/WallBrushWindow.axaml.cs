namespace CrystalStudio;

public partial class WallBrushWindow : EditorWindow
{
    private readonly AssetStore? assets;
    private readonly DispatcherTimer previewTimer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private readonly Dictionary<(string Id, bool Interior), WallFamily> modes = [];
    private bool ready;
    private WallFamily? draft;
    private readonly string? catalogueError;
    public WallFamily? SelectedFamily => draft;

    public WallBrushWindow(IReadOnlyList<WallFamily> families, AssetStore? assets, WallFamily? current = null, string? catalogueError = null)
    {
        InitializeComponent();
        this.assets = assets;
        this.catalogueError = catalogueError;
        foreach (var family in families)
        {
            modes[(family.Id, false)] = family.WithMode(false);
            if (family.InnerVariants.Count > 0) modes[(family.Id, true)] = family.WithMode(true);
        }
        WallTypeList.ItemsSource = families;
        WallTypeList.SelectedItem = families.FirstOrDefault(f => f.Id == current?.Id) ?? families.FirstOrDefault();
        InnerMode.IsChecked = current?.DrawInterior ?? true;
        OuterMode.IsChecked = InnerMode.IsChecked != true;
        ready = true;
        previewTimer.Tick += (_, _) => RefreshPreview();
        Closed += (_, _) => previewTimer.Stop();
        UpdateSelection();
    }

    private void SelectionChanged(object sender, SelectionChangedEventArgs e) { if (ready) UpdateSelection(); }
    private void ModeChanged(object sender, RoutedEventArgs e) { if (ready) UpdateSelection(); }

    private void UpdateSelection()
    {
        previewTimer.Stop();
        SectionPreview.Source = null;
        draft = null;
        if (WallTypeList.SelectedItem is not WallFamily family)
        { PreviewHint.Text = (catalogueError ?? LocalText.T("No wall types are available.")); UseBrushButton.IsEnabled = false; return; }
        FacingModes.IsVisible = family.HasFacingModes ? true : false;
        draft = modes[(family.Id, family.HasFacingModes && InnerMode.IsChecked == true)];
        UseBrushButton.IsEnabled = true;
        string? unavailable = draft.Availability(assets);
        AvailabilityHint.Text = (unavailable ?? "");
        AvailabilityHint.IsVisible = unavailable == null ? false : true;
        PreviewHint.Text = (unavailable ?? LocalText.T("Loading section preview…"));
        if (unavailable == null) { RefreshPreview(); if (SectionPreview.Source == null) previewTimer.Start(); }
    }

    private void RefreshPreview()
    {
        if (draft == null || assets == null) return;
        var bitmap = StampPreview.Create(draft.BrushPreview(), assets, 620, 370);
        if (bitmap == null) return;
        SectionPreview.Source = bitmap;
        PreviewHint.Text = ("");
        previewTimer.Stop();
    }

    private void UseClick(object sender, RoutedEventArgs e)
    {
        if (draft == null) return;
        draft.RequestedDoorCount = null;
        DialogResult = true;
    }
}
