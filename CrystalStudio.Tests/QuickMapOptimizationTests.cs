using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace CrystalStudio.Tests;

public sealed class QuickMapOptimizationTests
{
    [AvaloniaTheory]
    [InlineData("")]
    [InlineData("invalid\0folder")]
    public void UnsavedMapsDoNotRequireAValidOutputFolder(string directory)
    {
        var window = Wizard();
        try
        {
            window.FolderBox.Text = directory;
            Dispatcher.UIThread.RunJobs();
            Assert.True(window.CreateButton.IsEnabled);
            Assert.False(window.FolderBox.IsEffectivelyEnabled);
            window.SaveToggle.IsChecked = true;
            Assert.False(window.CreateButton.IsEnabled);
            window.SaveToggle.IsChecked = false;
            window.NameBox.Text = "../escape";
            Dispatcher.UIThread.RunJobs();
            Assert.False(window.CreateButton.IsEnabled);
        }
        finally { window.Close(); }
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void SavingRejectsAFileInTheOutputDirectoryPath(bool nested)
    {
        string root = NewRoot();
        string file = Path.Combine(root, "file");
        File.WriteAllText(file, "existing content");
        var window = Wizard();
        try
        {
            window.FolderBox.Text = nested ? Path.Combine(file, "child") : file;
            window.SaveToggle.IsChecked = true;
            Dispatcher.UIThread.RunJobs();
            Assert.False(window.CreateButton.IsEnabled);
            Assert.Equal("existing content", File.ReadAllText(file));
            window.SaveToggle.IsChecked = false;
            Assert.True(window.CreateButton.IsEnabled);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void ChangingMapTypeMarksThePresetCustomAndKeepsEachTypesParameters()
    {
        var window = Wizard(WithPreset());
        try
        {
            window.PresetBox.SelectedIndex = 1;
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(1, window.PresetBox.SelectedIndex);
            Assert.Equal("11", window.SelectedParameters!["SeedBox"]);
            window.KindBox.SelectedItem = MapCreationKind.Terrain;
            Assert.Empty(window.SelectedParameters!);
            window.KindBox.SelectedItem = MapCreationKind.Province;
            Assert.Equal(-1, window.PresetBox.SelectedIndex);
            Assert.Equal("11", window.SelectedParameters!["SeedBox"]);
            window.PresetBox.SelectedIndex = 1;
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(1, window.PresetBox.SelectedIndex);
            Assert.Equal("11", window.SelectedParameters!["SeedBox"]);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void EditingPresetDimensionsKeepsItsGeneratorParametersButMarksTheSelectionCustom()
    {
        var window = Wizard(WithPreset());
        try
        {
            window.PresetBox.SelectedIndex = 1;
            window.WidthBox.Text = "256";
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(-1, window.PresetBox.SelectedIndex);
            Assert.Equal("11", window.SelectedParameters!["SeedBox"]);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void CustomPresetNamesAreNotTranslated()
    {
        string originalLanguage = LocalText.Language;
        var settings = new SettingsStore(Path.Combine(NewRoot(), "language.json"));
        settings.Save(new(null, "zh-CN")); LocalText.Initialize(settings);
        var window = Wizard(WithPreset());
        try
        {
            window.Show(); Dispatcher.UIThread.RunJobs();
            var template = window.PresetBox.ItemTemplate;
            Assert.NotNull(template);
            Assert.Equal("Outdoor", Assert.IsType<TextBlock>(template.Build(window.PresetBox.Items[1])).Text);
            Assert.Equal(LocalText.T("Last used settings"), Assert.IsType<TextBlock>(template.Build(window.PresetBox.Items[0])).Text);
        }
        finally
        {
            window.Close(); settings.Save(new(null, originalLanguage)); LocalText.Initialize(settings);
        }
    }

    [Theory]
    [InlineData("NewMap", "NewMap_3")]
    [InlineData("NewMap_2", "NewMap_3")]
    [InlineData("NewMap_2.map", "NewMap_3")]
    [InlineData("NewMap_2147483647", "NewMap_2147483648")]
    public void AutomaticNamesIncrementAnExistingNumericSuffix(string previous, string expected)
    {
        string root = NewRoot();
        File.WriteAllText(Path.Combine(root, "NewMap.map"), "existing");
        File.WriteAllText(Path.Combine(root, "NewMap_2.map"), "existing");
        File.WriteAllText(Path.Combine(root, MapCreationPaths.FileName(previous)), "existing");
        Assert.Equal(expected, MapCreationPaths.AvailableName(root, previous));
        Assert.Equal("Other_2", MapCreationPaths.AvailableName(root, "Other_2"));
    }

    [AvaloniaTheory]
    [InlineData(MapCreationKind.Terrain, 16, false)]
    [InlineData(MapCreationKind.Province, 128, false)]
    [InlineData(MapCreationKind.Dungeon, 64, false)]
    [InlineData(MapCreationKind.Terrain, 16, true)]
    [InlineData(MapCreationKind.Province, 128, true)]
    [InlineData(MapCreationKind.Dungeon, 64, true)]
    public async Task PreviewCanReturnToMapSettingsAndThenCancelOrCreate(MapCreationKind kind, int size, bool complete)
    {
        string root = NewRoot();
        Fixture.CreateLibraries(Path.Combine(root, "Data"));
        var settings = new SettingsStore(Path.Combine(root, "profile", "settings.json")); settings.Save(new(root, "en-US"));
        var window = new MainWindow(new AssetHistoryStore(Path.Combine(root, "assets.json")), new RecentMapStore(Path.Combine(root, "recent.json")), settings);
        window.Show();
        try
        {
            await Until(() => window.FindControl<MapViewport>("Viewport")!.Assets != null && window.FindControl<MenuItem>("NewButton")!.IsEnabled);
            var document = window.FindControl<MapViewport>("Viewport")!.Document;
            var tabs = window.GetVisualDescendants().OfType<TabControl>().Single(control => control.Name == "MapTabs");
            int wizardCount = 0; bool previewOpened = false; bool restored = false;
            Window? activePreview = null;
            using var dialogs = Window.WindowOpenedEvent.AddClassHandler<Window>((dialog, _) =>
            {
                if (dialog is NewMapWindow wizard) Dispatcher.UIThread.Post(() =>
                {
                    wizardCount++;
                    if (wizardCount == 1)
                    {
                        wizard.KindBox.SelectedItem = kind; wizard.WidthBox.Text = size.ToString(); wizard.HeightBox.Text = size.ToString();
                        wizard.NameBox.Text = "Retained"; wizard.FolderBox.Text = ""; wizard.SaveToggle.IsChecked = false;
                        wizard.PresetNameBox.Text = "Draft preset";
                        wizard.CreateButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        if (wizard.Result == null) wizard.Close(false);
                    }
                    else
                    {
                        restored = Equals(wizard.KindBox.SelectedItem, kind) && wizard.WidthBox.Text == size.ToString() &&
                            wizard.HeightBox.Text == size.ToString() && wizard.NameBox.Text == "Retained" && wizard.FolderBox.Text == "" &&
                            wizard.SaveToggle.IsChecked == false && wizard.PresetNameBox.Text == "Draft preset";
                        if (complete)
                        {
                            wizard.KindBox.SelectedItem = MapCreationKind.Blank;
                            wizard.CreateButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        }
                        else
                        {
                            wizard.Close(false);
                            Dispatcher.UIThread.Post(() => activePreview?.Close(false));
                        }
                    }
                });
                else if (dialog is TerrainGeneratorWindow or ProvinceGeneratorWindow or TempleGeneratorWindow)
                {
                    previewOpened = true; activePreview = dialog;
                    Dispatcher.UIThread.Post(() =>
                    {
                        var back = dialog.GetLogicalDescendants().OfType<Button>().SingleOrDefault(button => button.Name == "QuickMapBack");
                        if (back == null) dialog.Close(false);
                        else back.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    });
                }
            });
            typeof(MainWindow).GetMethod("QuickCreateClick", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                .Invoke(window, [window, new RoutedEventArgs()]);
            await Until(() => wizardCount == 2 && window.FindControl<MenuItem>("NewButton")!.IsEnabled && (!complete || tabs.Items.Count == 2));
            Assert.True(previewOpened); Assert.Equal(2, wizardCount); Assert.True(restored);
            if (complete)
            {
                Assert.NotSame(document, window.FindControl<MapViewport>("Viewport")!.Document);
                Assert.Equal(2, tabs.Items.Count);
                var preferences = new MapCreationStore(Path.Combine(root, "profile", "map-creation.json")); preferences.Load();
                Assert.Equal("Retained", preferences.Data.Last!.Name);
                Assert.Contains("Draft preset", preferences.Data.Presets.Keys);
            }
            else
            {
                Assert.Same(document, window.FindControl<MapViewport>("Viewport")!.Document);
                Assert.Single(tabs.Items);
                Assert.False(File.Exists(Path.Combine(root, "profile", "map-creation.json")));
            }
        }
        finally { window.FindControl<MapViewport>("Viewport")!.History.Saved(); window.Close(); }
    }

    private static NewMapWindow Wizard(MapCreationStore? store = null) => new(NewRoot(),
        Enum.GetValues<MapCreationKind>().ToDictionary(kind => kind, _ => (string?)null),
        new(MapCreationKind.Blank, 256, 256, "NewMap", null, false), store);

    private static MapCreationStore WithPreset()
    {
        var store = new MapCreationStore(Path.Combine(NewRoot(), "preferences.json"));
        store.Data.Presets["Outdoor"] = new(new(MapCreationKind.Province, 128, 128),
            new Dictionary<string, string> { ["SeedBox"] = "11" });
        return store;
    }

    private static string NewRoot()
    {
        string root = Path.Combine(TestApplication.Root, "quick-map-optimization-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root); return root;
    }

    private static async Task Until(Func<bool> condition)
    {
        for (int attempt = 0; attempt < 500; attempt++)
        {
            Dispatcher.UIThread.RunJobs(); if (condition()) return; await Task.Delay(20);
        }
        Assert.True(condition(), "Timed out waiting for quick map creation.");
    }
}
