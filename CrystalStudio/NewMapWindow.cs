using System.IO;
using Avalonia.Controls.Templates;
using Avalonia.LogicalTree;

namespace CrystalStudio;

internal sealed partial class NewMapWindow : EditorWindow
{
    private sealed record LastSettingsChoice
    {
        public override string ToString() => LocalText.T("Last used settings");
    }
    internal readonly ComboBox KindBox = new() { Name = "MapKind" };
    internal readonly ComboBox TemplateBox = new() { Name = "MapTemplate", PlaceholderText = LocalText.T("Custom settings") };
    internal readonly TextBox WidthBox = new() { Name = "MapWidth" };
    internal readonly TextBox HeightBox = new() { Name = "MapHeight" };
    internal readonly TextBox NameBox = new() { Name = "MapName" };
    internal readonly TextBox FolderBox = new() { Name = "MapOutputFolder" };
    internal readonly CheckBox SaveToggle = new() { Content = LocalText.T("Save the map after creation") };
    internal readonly CheckBox LockRatioToggle = new() { Content = LocalText.T("Lock aspect ratio") };
    internal readonly Button SwapDimensionsButton = new() { Content = LocalText.T("Swap width / height") };
    internal readonly Button CreateButton = new() { Name = "CreateQuickMap", IsDefault = true };
    internal readonly ComboBox PresetBox = new() { Name = "MapPreset", PlaceholderText = LocalText.T("Current draft") };
    internal readonly TextBox PresetNameBox = new() { Name = "SaveMapPreset", Watermark = LocalText.T("Optional preset name") };
    private readonly TextBlock feedback = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10) };
    private readonly TextBlock setupSummary = new() { Name = "QuickMapSetupSummary", TextWrapping = TextWrapping.Wrap, MaxLines = 2, Margin = new Thickness(0, 8, 0, 0) };
    private readonly Dictionary<Control, TextBlock> fieldErrors = [];
    private readonly Button locateError = new() { Name = "QuickMapLocateError", Content = LocalText.T("Go to first error"), IsVisible = false };
    private Control? firstError;
    private readonly TextBlock sizeSummary = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock templateHint = new() { TextWrapping = TextWrapping.Wrap };
    private readonly WrapPanel sizeChoices = new();
    private readonly StackPanel outputPanel = new() { Spacing = 6 };
    private readonly IReadOnlyDictionary<MapCreationKind, string?> availability;
    private readonly MapCreationStore? preferences;
    private readonly Dictionary<MapCreationKind, Dictionary<string, string>> draftParameters = [];
    private bool updatingDimensions, applyingSelection;
    private double lockedRatio = 1;
    private MapCreationKind currentKind;
    public MapCreationSettings? Result { get; private set; }
    public Dictionary<string, string>? SelectedParameters { get; private set; }

    public NewMapWindow(string directory, IReadOnlyDictionary<MapCreationKind, string?> availability,
        MapCreationSettings? previous = null, MapCreationStore? store = null, MapCreationSession? session = null, IReadOnlyDictionary<string, string>? initialParameters = null)
    {
        this.availability = availability; preferences = store;
        Title = LocalText.T("Quick Create Map"); Width = 600; Height = 780; MinWidth = 500; MinHeight = 480;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var root = new Grid { Margin = new Thickness(24), RowDefinitions = new RowDefinitions("*,Auto,Auto,Auto") };
        var panel = new StackPanel { Spacing = 6 };
        panel.Children.Add(new TextBlock { Text = LocalText.T("Step 1 of 2 · Choose a starting point"), FontSize = 18, FontWeight = Avalonia.Media.FontWeight.SemiBold });
        panel.Children.Add(new TextBlock { Text = LocalText.T("Use a purpose template, or choose your own map settings. Blank maps can be created immediately."), TextWrapping = TextWrapping.Wrap });
        if (store != null)
        {
            PresetBox.ItemTemplate = new FuncDataTemplate<object>((choice, _) => new TextBlock { Text = choice?.ToString() });
            RefreshPresets();
            var presetRow = new DockPanel(); var manage = BuildPresetActions();
            DockPanel.SetDock(manage, Dock.Right); presetRow.Children.Add(manage); presetRow.Children.Add(PresetBox);
            Avalonia.Automation.AutomationProperties.SetName(PresetBox, LocalText.T("Saved preset"));
            AddField(panel, "Saved preset", presetRow);
        }
        TemplateBox.ItemsSource = MapCreationTemplates.All;
        AddField(panel, "Purpose template", TemplateBox); panel.Children.Add(templateHint);
        KindBox.ItemsSource = Enum.GetValues<MapCreationKind>();
        KindBox.ItemTemplate = new FuncDataTemplate<MapCreationKind>((kind, _) => new TextBlock { Text = MapCreationTemplates.KindLabel(kind) });
        AddField(panel, "Map type", KindBox);
        AddField(panel, "Suggested sizes", sizeChoices);
        var dimensions = new Grid { ColumnDefinitions = new ColumnDefinitions("*,12,*") };
        WidthBox.Watermark = LocalText.T("Width"); HeightBox.Watermark = LocalText.T("Height");
        Avalonia.Automation.AutomationProperties.SetName(WidthBox, LocalText.T("Map width in cells"));
        Avalonia.Automation.AutomationProperties.SetName(HeightBox, LocalText.T("Map height in cells"));
        dimensions.Children.Add(WidthBox); Grid.SetColumn(HeightBox, 2); dimensions.Children.Add(HeightBox);
        AddField(panel, "Width × height in cells", dimensions);
        AddError(panel, WidthBox);
        panel.Children.Add(new WrapPanel { Children = { LockRatioToggle, SwapDimensionsButton } }); panel.Children.Add(sizeSummary);
        AddField(panel, "Map name", NameBox); AddError(panel, NameBox); panel.Children.Add(SaveToggle);
        var folder = new DockPanel();
        var browse = new Button { Content = LocalText.T("Browse…"), Margin = new Thickness(6, 0, 0, 0) };
        DockPanel.SetDock(browse, Dock.Right); folder.Children.Add(browse); folder.Children.Add(FolderBox);
        browse.Click += (_, _) =>
        {
            var picker = new OpenFolderDialog { Title = LocalText.T("Choose an output folder."), InitialDirectory = FolderBox.Text };
            if (picker.ShowDialog(this) == true) FolderBox.Text = picker.FolderName;
        };
        AddField(outputPanel, "Output folder", folder); AddError(outputPanel, FolderBox); panel.Children.Add(outputPanel);
        if (store != null)
        {
            var advanced = new StackPanel { Spacing = 6, Margin = new Thickness(0, 8, 0, 0) };
            AddField(advanced, "Save/update preset after creation (optional)", PresetNameBox);
            AddError(advanced, PresetNameBox);
            advanced.Children.Add(new TextBlock { Text = LocalText.T("Presets share generation settings only. Names and output folders stay on this computer."), TextWrapping = TextWrapping.Wrap });
            panel.Children.Add(new Expander { Header = LocalText.T("Advanced creation options"), Content = advanced, HorizontalAlignment = HorizontalAlignment.Stretch });
        }
        root.Children.Add(new ScrollViewer { Content = panel, VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto });
        Grid.SetRow(setupSummary, 1); root.Children.Add(setupSummary);
        var feedbackPanel = new StackPanel { Children = { feedback, locateError } };
        var feedbackScroll = new ScrollViewer { Content = feedbackPanel, MaxHeight = 84, VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto };
        Grid.SetRow(feedbackScroll, 2); root.Children.Add(feedbackScroll);
        locateError.Click += (_, _) =>
        {
            if (firstError == null) return;
            foreach (var expander in firstError.GetLogicalAncestors().OfType<Expander>()) expander.IsExpanded = true;
            firstError.Focus(); firstError.BringIntoView();
        };
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8 };
        actions.Children.Add(new Button { Content = LocalText.T(session == null ? "Cancel" : "Return to preview"), IsCancel = true }); actions.Children.Add(CreateButton);
        Grid.SetRow(actions, 3); root.Children.Add(actions); Content = root;
        var initial = session?.Settings ?? previous ?? new(availability.GetValueOrDefault(MapCreationKind.Province) == null ? MapCreationKind.Province : MapCreationKind.Blank);
        currentKind = Enum.IsDefined(initial.Kind) ? initial.Kind : MapCreationKind.Blank;
        KindBox.SelectedItem = currentKind;
        SetDimensions(initial.Width, initial.Height);
        FolderBox.Text = initial.Directory ?? directory;
        try { NameBox.Text = session != null || !initial.SaveImmediately ? initial.Name : MapCreationPaths.AvailableName(FolderBox.Text, initial.Name); }
        catch (ArgumentException) { NameBox.Text = "NewMap"; }
        SaveToggle.IsChecked = initial.SaveImmediately;
        SelectedParameters = session == null ? initialParameters == null ? store?.Parameters(currentKind) : new(initialParameters) : new(session.Parameters);
        PresetNameBox.Text = session?.PresetName;
        if (session == null && store != null) PresetBox.SelectedIndex = 0;
        PresetBox.SelectionChanged += (_, _) => ApplyPreset(initial);
        TemplateBox.SelectionChanged += (_, _) => ApplyPurposeTemplate();
        KindBox.SelectionChanged += (_, _) =>
        {
            if (KindBox.SelectedItem is not MapCreationKind kind) return;
            if (SelectedParameters != null) draftParameters[currentKind] = new(SelectedParameters);
            currentKind = kind;
            SelectedParameters = draftParameters.TryGetValue(kind, out var values) ? new(values) : preferences?.Parameters(kind);
            if (!applyingSelection) ClearStartingPoint();
            UpdateSizeChoices(); Validate();
        };
        WidthBox.PropertyChanged += (_, args) => { if (args.Property == TextBox.TextProperty) DimensionsChanged(true); };
        HeightBox.PropertyChanged += (_, args) => { if (args.Property == TextBox.TextProperty) DimensionsChanged(false); };
        LockRatioToggle.IsCheckedChanged += (_, _) => RememberRatio();
        SwapDimensionsButton.Click += (_, _) =>
        {
            if (int.TryParse(WidthBox.Text, out int width) && int.TryParse(HeightBox.Text, out int height))
            { SetDimensions(height, width); ClearStartingPoint(); Validate(); }
        };
        foreach (var field in new[] { NameBox, FolderBox, PresetNameBox }) field.TextChanged += (_, _) => Validate();
        SaveToggle.IsCheckedChanged += (_, _) => Validate();
        CreateButton.Click += (_, _) =>
        {
            if (!Validate()) return;
            if (KindBox.SelectedItem is MapCreationKind.Blank && store?.Data.Presets.ContainsKey(PresetNameBox.Text?.Trim() ?? "") == true &&
                MessageBox.Show(this, "Replace the existing preset after creating this map?", "Map preset", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            Result = Read(); DialogResult = true;
        };
        if (session != null) CreateButton.SetTip(LocalText.T("Name and output changes keep the current preview. Size, type or parameter changes rebuild it."));
        UpdateSizeChoices(); Validate();
    }

    private void ClearStartingPoint()
    {
        applyingSelection = true;
        PresetBox.SelectedIndex = -1; TemplateBox.SelectedIndex = -1;
        templateHint.Text = ""; applyingSelection = false;
    }

    private void ApplyPurposeTemplate()
    {
        if (applyingSelection || TemplateBox.SelectedItem is not MapCreationTemplate template) return;
        applyingSelection = true; PresetBox.SelectedIndex = -1;
        KindBox.SelectedItem = template.Kind; SetDimensions(template.Width, template.Height);
        SelectedParameters = new(template.Parameters); templateHint.Text = LocalText.T(template.Description);
        applyingSelection = false; Validate();
    }

    private void ApplyPreset(MapCreationSettings initial)
    {
        if (applyingSelection || preferences == null || PresetBox.SelectedIndex < 0) return;
        applyingSelection = true; TemplateBox.SelectedIndex = -1; templateHint.Text = "";
        if (PresetBox.SelectedIndex == 0)
        {
            var last = preferences.Data.Last ?? initial;
            KindBox.SelectedItem = last.Kind; SetDimensions(last.Width, last.Height);
            SelectedParameters = preferences.Parameters(last.Kind);
        }
        else if (PresetBox.SelectedItem is string name && preferences.Data.Presets.TryGetValue(name, out var preset))
        {
            KindBox.SelectedItem = preset.Generation.Kind; SetDimensions(preset.Generation.Width, preset.Generation.Height);
            SelectedParameters = new(preset.Parameters);
        }
        applyingSelection = false; Validate();
    }

    private void UpdateSizeChoices()
    {
        sizeChoices.Children.Clear();
        foreach (var size in MapCreationTemplates.Sizes(currentKind))
        {
            var button = new Button { Content = $"{size.Width} × {size.Height}", Margin = new Thickness(0, 0, 6, 6) };
            button.SetTip(LocalText.T(size.Purpose));
            button.Click += (_, _) => { SetDimensions(size.Width, size.Height); ClearStartingPoint(); Validate(); };
            sizeChoices.Children.Add(button);
        }
    }

    private void SetDimensions(int width, int height)
    {
        updatingDimensions = true; WidthBox.Text = width.ToString(); HeightBox.Text = height.ToString();
        updatingDimensions = false; RememberRatio();
    }

    private void RememberRatio()
    {
        if (int.TryParse(WidthBox.Text, out int width) && int.TryParse(HeightBox.Text, out int height) && width > 0 && height > 0)
            lockedRatio = (double)width / height;
    }

    private void DimensionsChanged(bool widthChanged)
    {
        if (updatingDimensions) return;
        if (LockRatioToggle.IsChecked == true && int.TryParse(widthChanged ? WidthBox.Text : HeightBox.Text, out int value) && value > 0)
        {
            double counterpart = Math.Round(widthChanged ? value / lockedRatio : value * lockedRatio, MidpointRounding.AwayFromZero);
            if (counterpart is >= 1 and <= int.MaxValue)
            {
                updatingDimensions = true;
                if (widthChanged) HeightBox.Text = ((int)counterpart).ToString(); else WidthBox.Text = ((int)counterpart).ToString();
                updatingDimensions = false;
            }
        }
        if (!applyingSelection) ClearStartingPoint();
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
        firstError = null;
        foreach (var field in fieldErrors.Values) { field.Text = ""; field.IsVisible = false; }
        bool validWidth = int.TryParse(WidthBox.Text, out int width), validHeight = int.TryParse(HeightBox.Text, out int height);
        string? error = !validWidth || !validHeight
            ? "Enter dimensions between 16 and 4096." : MapDimensions.CreationError(width, height, kind);
        SetError(!validWidth ? WidthBox : !validHeight ? HeightBox : WidthBox, error, WidthBox);
        sizeSummary.Text = width > 0 && height > 0 ? LocalText.Format("{0:N0} cells · about {1:N0} base ground tiles (2 × 2 cells each).", (long)width * height, ((long)width + 1) / 2 * (((long)height + 1) / 2)) : "";
        error ??= availability.GetValueOrDefault(kind);
        string? presetError = (PresetNameBox.Text?.Trim().Length ?? 0) > 80 ? "Enter a preset name (1–80 characters)." : null;
        if (!string.IsNullOrWhiteSpace(PresetNameBox.Text) && preferences?.Data.Presets.Count >= 50 &&
            !preferences.Data.Presets.ContainsKey(PresetNameBox.Text.Trim())) presetError ??= "At most 50 map presets can be saved.";
        SetError(PresetNameBox, presetError); error ??= presetError;
        bool save = SaveToggle.IsChecked == true; outputPanel.IsVisible = save; outputPanel.IsEnabled = save;
        string? nameError = null, folderError = null;
        try { MapCreationPaths.ValidateName(NameBox.Text ?? ""); }
        catch (ArgumentException exception) { nameError = exception.Message; }
        try
        {
            if (save && nameError == null)
            {
                string target = MapCreationPaths.Target(FolderBox.Text ?? "", NameBox.Text ?? "");
                if (File.Exists(target) || Directory.Exists(target)) nameError = "A map with this name already exists. Choose another name.";
            }
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        { folderError = exception.Message; }
        SetError(NameBox, nameError); SetError(FolderBox, folderError);
        error ??= nameError ?? folderError;
        setupSummary.Text = $"{NameBox.Text} · {WidthBox.Text} × {HeightBox.Text} · {MapCreationTemplates.KindLabel(kind)}\n" +
            (save ? LocalText.Format("Save after creation: {0}", MapCreationPaths.FileName(NameBox.Text ?? "")) : LocalText.T("Create an unsaved map; choose its location with Save As."));
        setupSummary.SetTip(setupSummary.Text + (save ? "\n" + FolderBox.Text : ""));
        locateError.IsVisible = firstError != null;
        CreateButton.Content = LocalText.T(kind == MapCreationKind.Blank ? "Create Map" : "Next: Preview");
        CreateButton.IsEnabled = error == null;
        feedback.Text = LocalText.T(error ?? (kind == MapCreationKind.Blank ? "A blank map needs no game artwork."
            : "The next window checks artwork and previews the result. Cancelling leaves your maps unchanged."));
        return error == null;
    }

    private void AddError(StackPanel panel, Control field)
    {
        var message = new TextBlock { Name = field.Name + "FieldError", TextWrapping = TextWrapping.Wrap, IsVisible = false };
        message.SetResource(TextBlock.ForegroundProperty, "ThemeD1AD77");
        fieldErrors[field] = message; panel.Children.Add(message);
    }

    private void SetError(Control field, string? error, Control? messageField = null)
    {
        if (error == null) return;
        firstError ??= field;
        if (fieldErrors.TryGetValue(messageField ?? field, out var message)) { message.Text = LocalText.T(error); message.IsVisible = true; }
    }
}
