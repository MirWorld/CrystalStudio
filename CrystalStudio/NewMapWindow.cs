using System.IO;

namespace CrystalStudio;

internal sealed class NewMapWindow : EditorWindow
{
    internal readonly ComboBox KindBox = new() { Name = "MapKind" };
    internal readonly TextBox WidthBox = new() { Name = "MapWidth" };
    internal readonly TextBox HeightBox = new() { Name = "MapHeight" };
    internal readonly TextBox NameBox = new() { Name = "MapName" };
    internal readonly TextBox FolderBox = new() { Name = "MapOutputFolder" };
    internal readonly CheckBox SaveToggle = new() { Content = LocalText.T("Save the map after creation") };
    internal readonly Button CreateButton = new() { Name = "CreateQuickMap", IsDefault = true };
    internal readonly ComboBox PresetBox = new() { Name = "MapPreset" };
    internal readonly TextBox PresetNameBox = new() { Name = "SaveMapPreset", Watermark = LocalText.T("Optional preset name") };
    private readonly TextBlock feedback = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10) };
    private readonly IReadOnlyDictionary<MapCreationKind, string?> availability;
    private readonly MapCreationStore? preferences;
    public MapCreationSettings? Result { get; private set; }
    public Dictionary<string, string>? SelectedParameters { get; private set; }

    public NewMapWindow(string directory, IReadOnlyDictionary<MapCreationKind, string?> availability, MapCreationSettings? previous = null, MapCreationStore? store = null)
    {
        this.availability = availability; preferences = store;
        Title = LocalText.T("Quick Create Map"); Width = 520; SizeToContent = SizeToContent.Height;
        CanResize = false; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var panel = new StackPanel { Margin = new Thickness(24), Spacing = 8 };
        panel.Children.Add(new TextBlock { Text = LocalText.T("Choose a map type, preview it, then create."), TextWrapping = TextWrapping.Wrap });
        if (store != null)
        {
            PresetBox.ItemsSource = new[] { "Last used settings" }.Concat(store.Data.Presets.Keys.OrderBy(name => name)).ToList();
            PresetBox.SelectedIndex = 0; AddField(panel, "Preset", PresetBox);
        }
        KindBox.ItemsSource = Enum.GetValues<MapCreationKind>();
        AddField(panel, "Map type", KindBox);
        var sizes = new WrapPanel();
        foreach (int size in new[] { 128, 256, 512, 1000 })
        {
            var button = new Button { Content = $"{size} × {size}", Margin = new Thickness(0, 0, 6, 0) };
            button.Click += (_, _) => { WidthBox.Text = size.ToString(); HeightBox.Text = size.ToString(); };
            sizes.Children.Add(button);
        }
        panel.Children.Add(sizes);
        var dimensions = new Grid { ColumnDefinitions = new ColumnDefinitions("*,12,*") };
        dimensions.Children.Add(WidthBox); Grid.SetColumn(HeightBox, 2); dimensions.Children.Add(HeightBox);
        AddField(panel, "Width × height in cells", dimensions);
        AddField(panel, "Map name", NameBox);
        var folder = new DockPanel();
        var browse = new Button { Content = LocalText.T("Browse…"), Margin = new Thickness(6, 0, 0, 0) };
        DockPanel.SetDock(browse, Dock.Right); folder.Children.Add(browse); folder.Children.Add(FolderBox);
        browse.Click += (_, _) =>
        {
            var picker = new OpenFolderDialog { Title = "Choose an output folder.", InitialDirectory = FolderBox.Text };
            if (picker.ShowDialog(this) == true) FolderBox.Text = picker.FolderName;
        };
        AddField(panel, "Output folder", folder);
        panel.Children.Add(SaveToggle); panel.Children.Add(feedback);
        if (store != null) AddField(panel, "Save/update preset after creation (optional)", PresetNameBox);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8 };
        actions.Children.Add(new Button { Content = LocalText.T("Cancel"), IsCancel = true }); actions.Children.Add(CreateButton);
        panel.Children.Add(actions); Content = panel;
        var initial = previous ?? new(availability.GetValueOrDefault(MapCreationKind.Province) == null ? MapCreationKind.Province : MapCreationKind.Blank);
        KindBox.SelectedItem = Enum.IsDefined(initial.Kind) ? initial.Kind : MapCreationKind.Blank;
        WidthBox.Text = initial.Width.ToString(); HeightBox.Text = initial.Height.ToString();
        FolderBox.Text = initial.Directory ?? directory;
        try { NameBox.Text = MapCreationPaths.AvailableName(FolderBox.Text, initial.Name); }
        catch (ArgumentException) { NameBox.Text = "NewMap"; }
        SaveToggle.IsChecked = initial.SaveImmediately;
        PresetBox.SelectionChanged += (_, _) =>
        {
            if (store == null || PresetBox.SelectedIndex < 0) return;
            var setup = store.Data.Last ?? initial;
            Dictionary<string, string>? parameters = null;
            if (PresetBox.SelectedIndex > 0)
            {
                if (PresetBox.SelectedItem is not string name || !store.Data.Presets.TryGetValue(name, out var preset)) return;
                setup = preset.Settings; parameters = new(preset.Parameters);
            }
            KindBox.SelectedItem = setup.Kind; WidthBox.Text = setup.Width.ToString(); HeightBox.Text = setup.Height.ToString();
            FolderBox.Text = setup.Directory ?? directory; SaveToggle.IsChecked = setup.SaveImmediately;
            try { NameBox.Text = MapCreationPaths.AvailableName(FolderBox.Text, setup.Name); }
            catch (ArgumentException) { NameBox.Text = "NewMap"; }
            SelectedParameters = parameters ?? store.Parameters(setup.Kind);
        };
        KindBox.SelectionChanged += (_, _) => { SelectedParameters = null; Validate(); };
        foreach (var field in new[] { WidthBox, HeightBox, NameBox, FolderBox }) field.TextChanged += (_, _) => Validate();
        SaveToggle.IsCheckedChanged += (_, _) => Validate();
        PresetNameBox.TextChanged += (_, _) => Validate();
        CreateButton.Click += (_, _) =>
        {
            if (!Validate()) return;
            if (store?.Data.Presets.ContainsKey(PresetNameBox.Text?.Trim() ?? "") == true &&
                MessageBox.Show(this, "Replace the existing preset after creating this map?", "Map preset", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            Result = Read(); DialogResult = true;
        };
        Validate();
    }

    private static void AddField(StackPanel panel, string label, Control control)
    {
        panel.Children.Add(new TextBlock { Text = LocalText.T(label) }); panel.Children.Add(control);
        Avalonia.Automation.AutomationProperties.SetName(control, LocalText.T(label));
    }

    private MapCreationSettings Read() => new((MapCreationKind)KindBox.SelectedItem!, int.Parse(WidthBox.Text!),
        int.Parse(HeightBox.Text!), NameBox.Text!, FolderBox.Text, SaveToggle.IsChecked == true);

    private bool Validate()
    {
        if (KindBox.SelectedItem is not MapCreationKind kind) return false;
        string? error = !int.TryParse(WidthBox.Text, out int width) || !int.TryParse(HeightBox.Text, out int height)
            ? "Enter dimensions between 16 and 4096." : MapDimensions.CreationError(width, height, kind);
        error ??= availability.GetValueOrDefault(kind);
        if ((PresetNameBox.Text?.Trim().Length ?? 0) > 80) error ??= "Enter a preset name (1–80 characters).";
        if (!string.IsNullOrWhiteSpace(PresetNameBox.Text) && preferences?.Data.Presets.Count >= 50 &&
            !preferences.Data.Presets.ContainsKey(PresetNameBox.Text.Trim())) error ??= "At most 50 map presets can be saved.";
        try
        {
            string target = MapCreationPaths.Target(FolderBox.Text ?? "", NameBox.Text ?? "");
            if (SaveToggle.IsChecked == true && (File.Exists(target) || Directory.Exists(target)))
                error ??= "A map with this name already exists. Choose another name.";
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        { error ??= exception.Message; }
        CreateButton.Content = LocalText.T(kind == MapCreationKind.Blank ? "Create Map" : "Next: Preview");
        CreateButton.IsEnabled = error == null;
        feedback.Text = LocalText.T(error ?? (kind == MapCreationKind.Blank ? "A blank map needs no game artwork."
            : "The next window checks artwork and previews the result. Cancelling leaves your maps unchanged."));
        return error == null;
    }
}
