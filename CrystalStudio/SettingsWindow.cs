namespace CrystalStudio;

public sealed class SettingsWindow : EditorWindow
{
    public SettingsWindow(string? currentFolder, Func<string, Task> apply, bool firstSetup = false)
    {
        
        Title = firstSetup ? "Welcome to Crystal Studio" : "Crystal Studio Settings";
        Width = 660; SizeToContent = SizeToContent.Height; CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var panel = new StackPanel { Margin = new(24) };
        Content = panel;
        panel.Children.Add(new TextBlock { Text = firstSetup ? "Choose Your Libraries Once" : "Library Location", FontSize = 21, Margin = new(0, 0, 0, 12) });
        panel.Children.Add(new TextBlock { Text = "Crystal Data / Map Folder", Margin = new(0, 0, 0, 5) });
        var folderRow = new DockPanel();
        var browse = new Button { Content = "Browse\u2026" };
        DockPanel.SetDock(browse, Dock.Right); folderRow.Children.Add(browse);
        var folder = new TextBox { Text = currentFolder ?? "", IsReadOnly = true, VerticalContentAlignment = VerticalAlignment.Center };
        folderRow.Children.Add(folder); panel.Children.Add(folderRow);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = new Button { Content = firstSetup ? "Set Up Later" : "Cancel", IsCancel = true };
        var save = new Button { Theme = (Style)this.FindResource("PrimaryButton"), Content = firstSetup ? "Save and Start" : "Save Changes", IsDefault = true, IsEnabled = !string.IsNullOrWhiteSpace(currentFolder) };
        buttons.Children.Add(cancel); buttons.Children.Add(save); panel.Children.Add(buttons);
        var error = new TextBlock { Foreground = new ImmutableSolidColorBrush(Color.FromRgb(228, 158, 151)), TextWrapping = TextWrapping.Wrap, Margin = new(3, 8, 3, 0), IsVisible = false }; panel.Children.Add(error);
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
            error.IsVisible = false; saving = true; save.IsEnabled = false; browse.IsEnabled = false; cancel.IsEnabled = false; save.Content = "Checking Libraries\u2026";
            try { await apply(folder.Text); saving = false; DialogResult = true; }
            catch (Exception ex)
            {
                error.Text = ex.Message; error.IsVisible = true;
                saving = false; save.IsEnabled = true; browse.IsEnabled = true; cancel.IsEnabled = true; save.Content = firstSetup ? "Save and Start" : "Save Changes";
            }
        };
    }
}


