namespace CrystalStudio;

internal sealed class MinimapExportWindow : EditorWindow
{
    internal ComboBox Preset { get; } = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
    internal TextBox WidthInput { get; } = new();
    internal TextBox HeightInput { get; } = new();
    internal Button ExportButton { get; } = new() { Content = LocalText.T("Export"), IsDefault = true };
    private readonly TextBlock error = new() { TextWrapping = TextWrapping.Wrap };
    internal int OutputWidth => int.Parse(WidthInput.Text!);
    internal int OutputHeight => int.Parse(HeightInput.Text!);

    internal MinimapExportWindow(MapDocument document)
    {
        Title = LocalText.T("Export Minimap"); Width = 420;
        SizeToContent = SizeToContent.Height; CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var panel = new StackPanel { Margin = new(24), Spacing = 10 };
        Content = panel;
        panel.Children.Add(new TextBlock { Text = LocalText.T("Size preset") });
        Preset.ItemsSource = new[] { LocalText.T("Small (¼)"), LocalText.T("Medium (½)"), LocalText.T("Large (1:1)") };
        panel.Children.Add(Preset);
        panel.Children.Add(new TextBlock { Text = LocalText.T("Width (pixels)") });
        panel.Children.Add(WidthInput);
        panel.Children.Add(new TextBlock { Text = LocalText.T("Height (pixels)") });
        panel.Children.Add(HeightInput);
        panel.Children.Add(error);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 10 };
        var cancel = new Button { Content = LocalText.T("Cancel"), IsCancel = true };
        buttons.Children.Add(cancel); buttons.Children.Add(ExportButton); panel.Children.Add(buttons);
        WidthInput.PropertyChanged += (_, e) => { if (e.Property == TextBox.TextProperty) Validate(); };
        HeightInput.PropertyChanged += (_, e) => { if (e.Property == TextBox.TextProperty) Validate(); };
        Preset.SelectionChanged += (_, _) =>
        {
            if (Preset.SelectedIndex is < 0 or > 2) return;
            int divisor = new[] { 4, 2, 1 }[Preset.SelectedIndex];
            WidthInput.Text = checked(document.Width * 48 / divisor).ToString();
            HeightInput.Text = checked(document.Height * 32 / divisor).ToString();
        };
        ExportButton.Click += (_, _) => { if (Validate()) DialogResult = true; };
        Preset.SelectedIndex = 0;
    }

    private bool Validate()
    {
        bool valid = int.TryParse(WidthInput.Text, out int width) && int.TryParse(HeightInput.Text, out int height) &&
            MinimapExporter.ValidSize(width, height);
        ExportButton.IsEnabled = valid;
        error.Text = valid ? "" : LocalText.T("Enter positive whole-number dimensions. If the image is too large, reduce its width or height.");
        error.IsVisible = !valid;
        return valid;
    }
}
