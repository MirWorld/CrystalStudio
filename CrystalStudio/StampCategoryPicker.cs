namespace CrystalStudio;

// Checkbox dropdown for catalogue fields that allow more than one category.
internal sealed class StampCategoryPicker : StackPanel
{
    private readonly List<CheckBox> choices = [];
    public List<string> SelectedValues => choices.Where(c => c.IsChecked == true).Select(c => (string)c.Tag!).ToList();

    public StampCategoryPicker(IEnumerable<string> options, IEnumerable<string> selected)
    {
        var values = selected.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var toggle = new ToggleButton { HorizontalContentAlignment = HorizontalAlignment.Left, Padding = new Thickness(5), MinHeight = 26 };
        var list = new StackPanel();
        var popup = new Popup { PlacementTarget = toggle, Placement = PlacementMode.Bottom, IsLightDismissEnabled = true };
        var border = new Border { BorderThickness = new Thickness(1), Padding = new Thickness(4), Child = new ScrollViewer { Content = list, MaxHeight = 220, VerticalScrollBarVisibility = ScrollBarVisibility.Auto } };
        border.SetResource(Border.BackgroundProperty, "Theme1C2028");
        border.SetResource(Border.BorderBrushProperty, "Line");
        popup.Child = border;
        void UpdateLabel() { var current = SelectedValues; toggle.Content = string.Concat(current.Count == 0 ? LocalText.T("None") : string.Concat(current.Select((value, index) => string.Concat((index == 0 ? "" : ", "), LocalText.T(value))).ToArray()), ("  ▾")); }
        foreach (string option in options)
        {
            var check = new CheckBox { Tag = option, Content = LocalText.T(option), IsChecked = values.Contains(option), Margin = new Thickness(4), Padding = new Thickness(2) };
            check.SetResource(TemplatedControl.ForegroundProperty, "ThemeE5E9F0");
            choices.Add(check); list.Children.Add(check);
            check.Checked += (_, _) => UpdateLabel(); check.Unchecked += (_, _) => UpdateLabel();
        }
        toggle.Checked += (_, _) => { border.MinWidth = toggle.Bounds.Width; popup.IsOpen = true; };
        toggle.Unchecked += (_, _) => popup.IsOpen = false;
        popup.Closed += (_, _) => toggle.IsChecked = false;
        Children.Add(toggle); Children.Add(popup); UpdateLabel();
    }
}
