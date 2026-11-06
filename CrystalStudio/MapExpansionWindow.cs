namespace CrystalStudio;

internal sealed class MapExpansionWindow : EditorWindow
{
    internal TextBox LeftInput { get; } = new() { Text = "0" };
    internal TextBox TopInput { get; } = new() { Text = "0" };
    internal TextBox RightInput { get; } = new() { Text = "0" };
    internal TextBox BottomInput { get; } = new() { Text = "0" };
    internal Button ApplyButton { get; } = new() { Content = LocalText.T("Expand Map"), IsDefault = true };
    internal int LeftAmount => int.Parse(LeftInput.Text!);
    internal int TopAmount => int.Parse(TopInput.Text!);
    internal int RightAmount => int.Parse(RightInput.Text!);
    internal int BottomAmount => int.Parse(BottomInput.Text!);
    private readonly TextBlock summary = new() { TextWrapping = TextWrapping.Wrap };
    private readonly MapDocument document;

    internal MapExpansionWindow(MapDocument document)
    {
        this.document = document;
        Title = LocalText.T("Expand Map"); Width = 440; CanResize = false;
        SizeToContent = SizeToContent.Height; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var panel = new StackPanel { Margin = new(24), Spacing = 10 }; Content = panel;
        panel.Children.Add(new TextBlock { Text = LocalText.T("Add blank cells on each side in steps of 2. Existing artwork is preserved."), TextWrapping = TextWrapping.Wrap });
        foreach (var (label, input) in new[] { ("Add left (cells)", LeftInput), ("Add top (cells)", TopInput),
            ("Add right (cells)", RightInput), ("Add bottom (cells)", BottomInput) })
        {
            panel.Children.Add(new TextBlock { Text = LocalText.T(label) }); panel.Children.Add(input);
            input.PropertyChanged += (_, e) => { if (e.Property == TextBox.TextProperty) Validate(); };
        }
        panel.Children.Add(summary);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 10 };
        buttons.Children.Add(new Button { Content = LocalText.T("Cancel"), IsCancel = true }); buttons.Children.Add(ApplyButton); panel.Children.Add(buttons);
        ApplyButton.Click += (_, _) => { if (Validate()) DialogResult = true; };
        Validate();
    }

    private bool Validate()
    {
        bool valid = int.TryParse(LeftInput.Text, out int left) && int.TryParse(TopInput.Text, out int top) &&
            int.TryParse(RightInput.Text, out int right) && int.TryParse(BottomInput.Text, out int bottom) &&
            MapDimensions.CanExpand(document.Width, document.Height, left, top, right, bottom);
        ApplyButton.IsEnabled = valid;
        summary.Text = valid ? LocalText.Format("Expanded map: {0} × {1} cells", document.Width + LeftAmount + RightAmount, document.Height + TopAmount + BottomAmount)
            : LocalText.T("Enter non-negative even amounts, with at least one side greater than 0. The result must fit the map size limits.");
        return valid;
    }
}
