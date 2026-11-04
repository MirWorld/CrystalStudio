namespace CrystalStudio;

public sealed class SettingsWindow : EditorWindow
{
    public SettingsWindow(string? currentFolder, Func<string, Task> apply, bool firstSetup = false)
    {
        
        this.Title = (firstSetup ? LocalText.T("Welcome to Crystal Studio") : LocalText.T("Crystal Studio Settings"));
        Width = 660; SizeToContent = SizeToContent.Height; CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var panel = new StackPanel { Margin = new(24) };
        Content = panel;
        panel.Children.Add(new TextBlock { Text = LocalText.T("Language"), Margin = new(0, 0, 0, 5) });
        int savedLanguageIndex = LocalText.PreferredLanguage() == "zh-CN" ? 0 : 1;
        var languages = new ComboBox { Name = "LanguageSelector", ItemsSource = new[] { "简体中文", "English" }, SelectedIndex = savedLanguageIndex };
        panel.Children.Add(languages);
        var restartHint = new TextBlock { Name = "LanguageRestartHint", Text = LocalText.T("Language changes take effect after restarting Crystal Studio."), TextWrapping = TextWrapping.Wrap, Margin = new(0, 5, 0, 14) };
        panel.Children.Add(restartHint);
        panel.Children.Add(new TextBlock { Text = (firstSetup ? LocalText.T("Choose Your Libraries Once") : LocalText.T("Library Location")), FontSize = 21, Margin = new(0, 0, 0, 12) });
        panel.Children.Add(new TextBlock { Text = LocalText.T("Crystal Data / Map Folder"), Margin = new(0, 0, 0, 5) });
        var folderRow = new DockPanel();
        var browse = new Button { Content = LocalText.T("Browse\u2026")};
        DockPanel.SetDock(browse, Dock.Right); folderRow.Children.Add(browse);
        var folder = new TextBox { Text = currentFolder ?? "", IsReadOnly = true, VerticalContentAlignment = VerticalAlignment.Center };
        folderRow.Children.Add(folder); panel.Children.Add(folderRow);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = new Button { Content = (firstSetup ? LocalText.T("Set Up Later") : LocalText.T("Cancel")), IsCancel = true };
        var save = new Button { Theme = (Style)this.FindResource("PrimaryButton"), Content = (firstSetup ? LocalText.T("Save and Start") : LocalText.T("Save Changes")), IsDefault = true, IsEnabled = !string.IsNullOrWhiteSpace(currentFolder) };
        buttons.Children.Add(cancel); buttons.Children.Add(save); panel.Children.Add(buttons);
        var error = new TextBlock { Foreground = new ImmutableSolidColorBrush(Color.FromRgb(228, 158, 151)), TextWrapping = TextWrapping.Wrap, Margin = new(3, 8, 3, 0), IsVisible = false }; panel.Children.Add(error);
        bool changingLanguage = false;
        languages.SelectionChanged += (_, _) =>
        {
            if (changingLanguage) return;
            try
            {
                LocalText.SaveLanguage(languages.SelectedIndex == 0 ? "zh-CN" : "en-US");
                savedLanguageIndex = languages.SelectedIndex;
                restartHint.Text = LocalText.T("Language saved. Restart Crystal Studio to apply.");
            }
            catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
            {
                changingLanguage = true; languages.SelectedIndex = savedLanguageIndex; changingLanguage = false;
                error.Text = LocalText.Format("Could not save language preference: {0}",ex.Message); error.IsVisible = true;
            }
        };
        bool saving = false;
        Closing += (_, e) => { if (saving) e.Cancel = true; };
        browse.Click += (_, _) =>
        {
            var picker = new OpenFolderDialog { Title = "Select Crystal Data or Map Folder" };
            if (!string.IsNullOrWhiteSpace(folder.Text) && System.IO.Directory.Exists(folder.Text)) picker.InitialDirectory = folder.Text;
            if (picker.ShowDialog(this) == true) { folder.Text = picker.FolderName; save.IsEnabled = true; }
        };
        save.Click += async (_, _) =>
        {
            error.IsVisible = false; saving = true; save.IsEnabled = false; browse.IsEnabled = false; cancel.IsEnabled = false; languages.IsEnabled = false; save.Content = LocalText.T("Checking Libraries\u2026");
            try { await apply(folder.Text); saving = false; DialogResult = true; }
            catch (Exception ex)
            {
                error.Text = (ex.Message); error.IsVisible = true;
                saving = false; save.IsEnabled = true; browse.IsEnabled = true; cancel.IsEnabled = true; languages.IsEnabled = true; save.Content = (firstSetup ? LocalText.T("Save and Start") : LocalText.T("Save Changes"));
            }
        };
    }
}


