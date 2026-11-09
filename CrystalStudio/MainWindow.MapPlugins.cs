namespace CrystalStudio;

public partial class MainWindow
{
    private void MapPluginsClick(object sender, RoutedEventArgs e)
    {
        if (busy) return;
        var registry = MapFormatPlugins.Default;
        var dialog = new EditorWindow { Title = LocalText.T("Map format plugins"), Width = 680, Height = 440,
            Owner = this, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var panel = new StackPanel { Margin = new(22), Spacing = 12 };
        var list = new TextBlock { TextWrapping = TextWrapping.Wrap };
        var status = new TextBlock { TextWrapping = TextWrapping.Wrap };
        void Refresh() => list.Text = string.Join("\n\n", registry.Plugins.Select(p =>
            $"{p.Name} [{p.Id}] · {string.Join(", ", p.Extensions)}\n" +
            LocalText.Format("Read: {0} · Write: {1}", p.CanRead ? LocalText.T("Yes") : LocalText.T("No"), p.CanWrite ? LocalText.T("Yes") : LocalText.T("No")) + $"\n{p.FilePath}"));
        panel.Children.Add(new TextBlock { Text = LocalText.T("Load a trusted native DLL matching the editor architecture. Supported in .NET and NativeAOT builds. Restart to update a loaded DLL."), TextWrapping = TextWrapping.Wrap });
        var load = new Button { Content = LocalText.T("Load DLL…") };
        load.Click += (_, _) =>
        {
            var picker = new OpenFileDialog { Filter = "Native map plugin (*.dll)|*.dll" };
            if (picker.ShowDialog(dialog) != true) return;
            try { var plugin = registry.Load(picker.FileName); Refresh(); status.Text = LocalText.Format("Loaded {0}", plugin.Name); }
            catch (Exception error) { StudioLog.Error("MapPlugins.Load", error); status.Text = error.Message; }
        };
        panel.Children.Add(load); panel.Children.Add(list); panel.Children.Add(status);
        Refresh(); status.Text = string.Join("\n", registry.Errors);
        dialog.Content = new ScrollViewer { Content = panel }; dialog.ShowDialog();
    }

    private bool ChooseMapSaveFormat(out string? pluginId)
    {
        var formats = MapFormatPlugins.Default.Plugins.Where(p => p.CanWrite).ToArray();
        pluginId = null;
        if (formats.Length == 0) return true;
        var dialog = new EditorWindow { Title = LocalText.T("Save map format"), Width = 420, SizeToContent = SizeToContent.Height,
            CanResize = false, Owner = this, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var panel = new StackPanel { Margin = new(24), Spacing = 12 };
        var choice = new ComboBox { ItemsSource = new[] { "Crystal (*.map)" }.Concat(formats.Select(p => $"{p.Name} ({string.Join(", ", p.Extensions)})")).ToArray(),
            SelectedIndex = Array.FindIndex(formats, p => p.Id == Viewport.Document.PluginId) + 1, HorizontalAlignment = HorizontalAlignment.Stretch };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 10 };
        var cancel = new Button { Content = LocalText.T("Cancel"), IsCancel = true };
        var next = new Button { Content = LocalText.T("Continue"), IsDefault = true };
        cancel.Click += (_, _) => dialog.Close(); next.Click += (_, _) => dialog.DialogResult = true;
        buttons.Children.Add(cancel); buttons.Children.Add(next);
        panel.Children.Add(new TextBlock { Text = LocalText.T("Save map format") }); panel.Children.Add(choice); panel.Children.Add(buttons); dialog.Content = panel;
        if (dialog.ShowDialog() != true) return false;
        pluginId = choice.SelectedIndex > 0 ? formats[choice.SelectedIndex - 1].Id : null;
        return true;
    }
}
