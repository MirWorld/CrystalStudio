using Avalonia.Platform.Storage;

namespace CrystalStudio;

internal abstract class EditorFileDialog
{
    public string Title { get; set; } = "";
    public string Filter { get; set; } = "All files|*.*";
    public string FileName { get; set; } = "";
    public string[] FileNames { get; protected set; } = [];
    public string? InitialDirectory { get; set; }
    public string DefaultExt { get; set; } = "";
    public bool AddExtension { get; set; } = true;
    public bool OverwritePrompt { get; set; } = true;
    protected List<FilePickerFileType> Types()
    {
        var parts = Filter.Split('|'); var types = new List<FilePickerFileType>();
        for (int i = 0; i + 1 < parts.Length; i += 2)
            types.Add(new FilePickerFileType(parts[i]) { Patterns = parts[i + 1].Split(';') });
        return types;
    }
    protected async Task<IStorageFolder?> Folder(EditorWindow owner) => string.IsNullOrWhiteSpace(InitialDirectory)
        ? null : await owner.StorageProvider.TryGetFolderFromPathAsync(InitialDirectory);
    public bool? ShowDialog(EditorWindow? owner = null) => Modal.Run(Select(owner ?? Modal.Owner ?? throw new InvalidOperationException("No dialog owner.")));
    protected abstract Task<bool> Select(EditorWindow owner);
}

internal sealed class OpenFileDialog : EditorFileDialog
{
    public bool Multiselect { get; set; }
    protected override async Task<bool> Select(EditorWindow owner)
    {
        var items = await owner.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        { Title = Title, AllowMultiple = Multiselect, FileTypeFilter = Types(), SuggestedStartLocation = await Folder(owner) });
        FileNames = items.Select(f => f.TryGetLocalPath()).OfType<string>().ToArray();
        FileName = FileNames.FirstOrDefault() ?? "";
        return FileNames.Length > 0;
    }
}

internal sealed class SaveFileDialog : EditorFileDialog
{
    protected override async Task<bool> Select(EditorWindow owner)
    {
        var file = await owner.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        { Title = Title, FileTypeChoices = Types(), SuggestedStartLocation = await Folder(owner), SuggestedFileName = FileName,
            DefaultExtension = DefaultExt.TrimStart('.'), ShowOverwritePrompt = OverwritePrompt });
        FileName = file?.TryGetLocalPath() ?? "";
        return FileName.Length > 0;
    }
}

internal sealed class OpenFolderDialog
{
    public string Title { get; set; } = "Choose folder";
    public string? InitialDirectory { get; set; }
    public string FolderName { get; private set; } = "";
    public bool? ShowDialog(EditorWindow owner) => Modal.Run(Select(owner));
    private async Task<bool> Select(EditorWindow owner)
    {
        var location = string.IsNullOrWhiteSpace(InitialDirectory) ? null : await owner.StorageProvider.TryGetFolderFromPathAsync(InitialDirectory);
        var folders = await owner.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        { Title = Title, SuggestedStartLocation = location, AllowMultiple = false });
        FolderName = folders.FirstOrDefault()?.TryGetLocalPath() ?? "";
        return FolderName.Length > 0;
    }
}

internal enum MessageBoxButton { OK, OKCancel, YesNo, YesNoCancel }
internal enum MessageBoxImage { None, Warning, Question, Error, Information }
internal enum MessageBoxResult { None, OK, Cancel, Yes, No }

internal static class MessageBox
{
    public static MessageBoxResult Show(string message, string title = "Crystal Studio") => Show(Modal.Owner, message, title);
    public static MessageBoxResult Show(EditorWindow? owner, string message, string title = "Crystal Studio",
        MessageBoxButton buttons = MessageBoxButton.OK, MessageBoxImage image = MessageBoxImage.None)
    {
        var dialog = new EditorWindow { Title = title, Width = 520, SizeToContent = SizeToContent.Height,
            CanResize = false, WindowStartupLocation = WindowStartupLocation.CenterOwner, Owner = owner, ShowInTaskbar = false };
        var content = new StackPanel { Margin = new Thickness(22), Spacing = 18 };
        content.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, MaxHeight = 440 });
        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8 };
        var result = MessageBoxResult.Cancel;
        var options = buttons switch
        {
            MessageBoxButton.YesNoCancel => new[] { MessageBoxResult.Yes, MessageBoxResult.No, MessageBoxResult.Cancel },
            MessageBoxButton.YesNo => [MessageBoxResult.Yes, MessageBoxResult.No],
            MessageBoxButton.OKCancel => [MessageBoxResult.OK, MessageBoxResult.Cancel],
            _ => [MessageBoxResult.OK]
        };
        foreach (var option in options)
        {
            var button = new Button { Content = option.ToString(), MinWidth = 76, IsDefault = option == options[0], IsCancel = option == MessageBoxResult.Cancel };
            button.Click += (_, _) => { result = option; dialog.Close(); }; row.Children.Add(button);
        }
        content.Children.Add(row); dialog.Content = content; dialog.ShowDialog(); return result;
    }
}
