using System.IO;

namespace CrystalStudio;

internal sealed partial class NewMapWindow
{
    private void RefreshPresets(string? selected = null)
    {
        if (preferences == null) return;
        bool wasApplying = applyingSelection; applyingSelection = true;
        PresetBox.ItemsSource = new object[] { new LastSettingsChoice() }.Concat(preferences.Data.Presets.Keys.OrderBy(name => name).Cast<object>()).ToList();
        PresetBox.SelectedItem = selected; applyingSelection = wasApplying;
    }

    private Button BuildPresetActions()
    {
        var actions = new MenuFlyout();
        foreach (var label in new[] { "Rename preset", "Duplicate preset", "Delete preset", "Import preset…", "Export preset…" })
        {
            var button = new MenuItem { Header = LocalText.T(label) };
            bool needsSelection = label != "Import preset…";
            void Update() => button.IsEnabled = !needsSelection || PresetBox.SelectedIndex > 0;
            PresetBox.SelectionChanged += (_, _) => Update(); Update();
            button.Click += (_, _) => ManagePreset(label); actions.Items.Add(button);
        }
        return new Button { Content = LocalText.T("Manage presets"), Flyout = actions, Margin = new Thickness(6, 0, 0, 0) };
    }

    private void ManagePreset(string action)
    {
        if (preferences == null) return;
        string? selected = PresetBox.SelectedIndex > 0 ? PresetBox.SelectedItem as string : null;
        try
        {
            if (action == "Import preset…")
            {
                var picker = new OpenFileDialog { Title = LocalText.T("Import preset…"), Filter = "Map preset (*.json)|*.json" };
                if (picker.ShowDialog(this) != true) return;
                string imported = preferences.ImportPreset(picker.FileName); RefreshPresets(imported); ApplyPreset(preferences.Data.Last ?? new());
            }
            else if (selected != null && action == "Export preset…")
            {
                string safeName = string.Concat(selected.Select(character => Path.GetInvalidFileNameChars().Contains(character) ? '_' : character));
                var picker = new SaveFileDialog { Title = LocalText.T("Export preset…"), Filter = "Map preset (*.json)|*.json", FileName = safeName + ".json", DefaultExt = ".json", AddExtension = true, OverwritePrompt = true };
                if (picker.ShowDialog(this) == true) preferences.ExportPreset(selected, picker.FileName);
            }
            else if (selected != null && action == "Delete preset")
            {
                if (MessageBox.Show(this, LocalText.Format("Delete preset '{0}'? Existing maps are not affected.", selected), "Map preset", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
                preferences.DeletePreset(selected); RefreshPresets();
            }
            else if (selected != null)
            {
                string? name = AskPresetName(action, action == "Duplicate preset" ? selected + " (2)" : selected);
                if (name == null) return;
                if (action == "Rename preset") preferences.RenamePreset(selected, name); else preferences.DuplicatePreset(selected, name);
                RefreshPresets(name);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or System.Text.Json.JsonException or NotSupportedException)
        {
            StudioLog.Error("MapCreation.Preset", exception);
            MessageBox.Show(this, LocalText.T(exception.Message), "Map preset", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private string? AskPresetName(string title, string initial)
    {
        var input = new TextBox { Text = initial, MaxLength = 80 };
        Avalonia.Automation.AutomationProperties.SetName(input, LocalText.T("Preset name"));
        var confirm = new Button { Content = LocalText.T("OK"), IsDefault = true };
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8,
            Children = { new Button { Content = LocalText.T("Cancel"), IsCancel = true }, confirm } };
        var prompt = new EditorWindow { Owner = this, Title = LocalText.T(title), Width = 420, SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, Content = new StackPanel { Margin = new Thickness(20), Spacing = 12,
                Children = { new TextBlock { Text = LocalText.T("Preset name") }, input, actions } } };
        string? result = null;
        input.TextChanged += (_, _) => confirm.IsEnabled = !string.IsNullOrWhiteSpace(input.Text);
        confirm.Click += (_, _) => { if (!string.IsNullOrWhiteSpace(input.Text)) { result = input.Text.Trim(); prompt.DialogResult = true; } };
        return prompt.ShowDialog() == true ? result : null;
    }
}
