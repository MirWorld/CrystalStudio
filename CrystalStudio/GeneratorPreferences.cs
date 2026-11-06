using System.Globalization;
using Avalonia.LogicalTree;

namespace CrystalStudio;

internal static class GeneratorPreferences
{
    private static string Choice(object value) => value switch
    {
        TerrainSet terrain => "terrain:" + terrain.Id,
        ProvinceStyle province => "province:" + province.WallId,
        TempleDefinition temple => "temple:" + temple.WallId,
        ComboBoxItem item => "item:" + (item.Tag ?? item.Content),
        _ => value.ToString() ?? ""
    };

    public static Dictionary<string, string> Capture(EditorWindow window)
    {
        var values = new Dictionary<string, string>();
        foreach (var control in window.GetLogicalDescendants().OfType<Control>().Where(control => control.Name != null && !control.Name.StartsWith("QuickMap", StringComparison.Ordinal)))
        {
            string? value = control switch
            {
                ComboBox { SelectedItem: ComboBoxItem } choice => "index:" + choice.SelectedIndex.ToString(CultureInfo.InvariantCulture),
                ComboBox { SelectedItem: { } item } => Choice(item),
                Slider slider => slider.Value.ToString(CultureInfo.InvariantCulture),
                CheckBox toggle => (toggle.IsChecked == true).ToString(),
                TextBox { Name: "SeedBox" } seed => seed.Text,
                _ => null
            };
            if (value != null) values[control.Name!] = value;
        }
        return values;
    }

    public static void Restore(EditorWindow window, IReadOnlyDictionary<string, string>? values)
    {
        if (values == null) return;
        foreach (var control in window.GetLogicalDescendants().OfType<Control>().Where(control => control.Name != null).ToArray())
        {
            if (!values.TryGetValue(control.Name!, out string? value)) continue;
            switch (control)
            {
                case ComboBox choice:
                    if (value.StartsWith("index:", StringComparison.Ordinal) && int.TryParse(value.AsSpan(6), out int index))
                    { if (index >= 0 && index < choice.Items.Count) choice.SelectedIndex = index; break; }
                    var match = choice.Items.Cast<object>().FirstOrDefault(item => Choice(item) == value);
                    if (match != null) choice.SelectedItem = match;
                    break;
                case Slider slider when double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double number) && double.IsFinite(number):
                    slider.Value = Math.Clamp(number, slider.Minimum, slider.Maximum); break;
                case CheckBox toggle when bool.TryParse(value, out bool enabled):
                    toggle.IsChecked = enabled && toggle.IsEnabled; break;
                case TextBox { Name: "SeedBox" } seed when int.TryParse(value, out _):
                    seed.Text = value; break;
            }
        }
    }
}
