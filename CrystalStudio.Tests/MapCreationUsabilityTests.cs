using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Headless;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace CrystalStudio.Tests;

public sealed class MapCreationUsabilityTests
{
    private static Dictionary<MapCreationKind, string?> Available() =>
        Enum.GetValues<MapCreationKind>().ToDictionary(kind => kind, _ => (string?)null);

    [AvaloniaFact]
    public void UnsavedDraftDoesNotRequireAnOutputFolder()
    {
        var window = new NewMapWindow(Path.GetTempPath(), Available(), new(MapCreationKind.Blank));
        try
        {
            window.SaveToggle.IsChecked = false;
            window.FolderBox.Text = "";
            Dispatcher.UIThread.RunJobs();
            Assert.True(window.CreateButton.IsEnabled);
            window.SaveToggle.IsChecked = true;
            Dispatcher.UIThread.RunJobs();
            Assert.False(window.CreateButton.IsEnabled);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void ApplyingANamedPresetKeepsTheCurrentNameAndLocalOutput()
    {
        string root = Path.Combine(TestApplication.Root, Guid.NewGuid().ToString("N"));
        var store = new MapCreationStore(Path.Combine(root, "profile.json"));
        store.SavePreset("Shared", new(MapCreationKind.Blank, 128, 256, "OtherName", Path.Combine(root, "OtherComputer"), true));
        var window = new NewMapWindow(root, Available(), new(MapCreationKind.Blank, Name: "MyDraft", Directory: root, SaveImmediately: false), store);
        try
        {
            window.PresetBox.SelectedIndex = 1;
            Assert.Equal("128", window.WidthBox.Text);
            Assert.Equal("256", window.HeightBox.Text);
            Assert.Equal("MyDraft", window.NameBox.Text);
            Assert.Equal(root, window.FolderBox.Text);
            Assert.False(window.SaveToggle.IsChecked);
        }
        finally { window.Close(); }
    }

    [Fact]
    public void MetadataChangesRetainGenerationButSizeAndSeedChangesInvalidateIt()
    {
        var settings = new MapCreationSettings(MapCreationKind.Terrain);
        var session = new MapCreationSession(settings, new Dictionary<string, string> { ["SeedBox"] = "572" });
        Assert.False(session.Update(settings with { Name = "Renamed", Directory = "another-folder", SaveImmediately = false }, session.Parameters, "Preset"));
        Assert.True(session.Update(session.Settings with { Width = 512 }, session.Parameters, "Preset"));
        Assert.Equal("572", session.Parameters["SeedBox"]);
        Assert.True(session.Update(session.Settings, new Dictionary<string, string> { ["SeedBox"] = "573" }, "Preset"));
    }

    [Fact]
    public void SizeSuggestionsAndPurposeTemplatesRespectGeneratorLimits()
    {
        foreach (var kind in Enum.GetValues<MapCreationKind>())
        {
            var sizes = MapCreationTemplates.Sizes(kind);
            Assert.NotEmpty(sizes);
            Assert.All(sizes, size => Assert.Null(MapDimensions.CreationError(size.Width, size.Height, kind)));
        }
        Assert.DoesNotContain(MapCreationTemplates.Sizes(MapCreationKind.Province), size => size.Width == 64 || size.Width == 1024);
        Assert.Contains(MapCreationTemplates.Sizes(MapCreationKind.Dungeon), size => size.Width == 64);
        Assert.Equal(4, MapCreationTemplates.All.Count);
        Assert.All(MapCreationTemplates.All, template => Assert.Null(MapDimensions.CreationError(template.Width, template.Height, template.Kind)));
    }

    [AvaloniaFact]
    public void DimensionsCanBeSwappedAndTheirRatioLocked()
    {
        var window = new NewMapWindow(Path.GetTempPath(), Available(), new(MapCreationKind.Blank, 256, 128));
        try
        {
            window.LockRatioToggle.IsChecked = true;
            window.WidthBox.Text = "512"; Dispatcher.UIThread.RunJobs();
            Assert.Equal("256", window.HeightBox.Text);
            window.SwapDimensionsButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal("256", window.WidthBox.Text);
            Assert.Equal("512", window.HeightBox.Text);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void ATemplateKeepsLocalOutputAndProvidesItsOwnGenerationParameters()
    {
        string root = Path.Combine(TestApplication.Root, Guid.NewGuid().ToString("N"));
        var window = new NewMapWindow(root, Available(), new(MapCreationKind.Blank, Name: "Draft", Directory: root, SaveImmediately: false));
        try
        {
            window.TemplateBox.SelectedItem = MapCreationTemplates.All.Single(template => template.Id == "small-dungeon");
            Assert.Equal(MapCreationKind.Dungeon, window.KindBox.SelectedItem);
            Assert.Equal("Draft", window.NameBox.Text);
            Assert.Equal(root, window.FolderBox.Text);
            Assert.False(window.SaveToggle.IsChecked);
            Assert.Equal("6", window.SelectedParameters!["RoomsSlider"]);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("small-dungeon", ((MapCreationTemplate)window.TemplateBox.SelectedItem!).Id);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void BackNavigationRetainsLivePreviewForMetadataChanges()
    {
        var apply = new Button { Name = "ApplyButton", Content = "Replace Map" };
        var seed = new TextBox { Name = "SeedBox", Text = "572" };
        var window = new EditorWindow { Content = new StackPanel { Children = { seed, new DockPanel { Children = { apply } } } } };
        var session = new MapCreationSession(new(MapCreationKind.Terrain, SaveImmediately: false));
        var navigation = QuickMapPreview.Configure(window, session, _ =>
        {
            session.Update(session.Settings with { Name = "Renamed" }, session.Parameters, "");
            return true;
        });
        Assert.Equal(LocalText.T("Create Map"), apply.Content);
        window.GetLogicalDescendants().OfType<Button>().Single(button => button.Name == "QuickMapBack").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.False(navigation.RestartRequested);
        Assert.Equal("Renamed", session.Settings.Name);
        Assert.Equal("572", session.Parameters["SeedBox"]);
        window.Close();
    }

    [AvaloniaFact]
    public void QuickDensityControlsRetainAdvancedParametersWithoutPersistingUiChoices()
    {
        var coverage = new Slider { Name = "CoverageSlider", Minimum = 0, Maximum = 1, Value = .2 };
        var settings = new StackPanel { Children = { new TextBlock(), new ComboBox(), new TextBlock(), new ComboBox(), coverage } };
        var window = new EditorWindow { Content = new StackPanel { Children =
        {
            new ScrollViewer { Content = settings }, new DockPanel { Children = { new Button { Name = "ApplyButton" } } }
        } } };
        try
        {
            QuickMapPreview.Configure(window, new MapCreationSession(new(MapCreationKind.Terrain)), _ => false);
            var controls = window.GetLogicalDescendants().ToArray();
            var advanced = Assert.Single(controls.OfType<Expander>());
            Assert.False(advanced.IsExpanded);
            var density = controls.OfType<ComboBox>().Single(choice => choice.Name == "QuickMapDensity");
            density.SelectedIndex = 3;
            Assert.Equal(.7, coverage.Value);
            var parameters = GeneratorPreferences.Capture(window);
            Assert.Equal("0.7", parameters["CoverageSlider"]);
            Assert.DoesNotContain("QuickMapDensity", parameters.Keys);
            coverage.Value = .33;
            Assert.Equal(0, density.SelectedIndex);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task ReturningToSetupKeepsTheLiveGeneratorUnlessDimensionsChange()
    {
        foreach (bool resize in new[] { false, true })
        {
            string root = Path.Combine(TestApplication.Root, "navigate-" + Guid.NewGuid().ToString("N"));
            Fixture.CreateLibraries(Path.Combine(root, "Data"));
            var settings = new SettingsStore(Path.Combine(root, "profile", "settings.json")); settings.Save(new(root, "en-US"));
            var store = new MapCreationStore(Path.Combine(root, "profile", "map-creation.json"));
            store.SetParameters(MapCreationKind.Terrain, new Dictionary<string, string> { ["SeedBox"] = "572" }); store.Save();
            var window = new MainWindow(new AssetHistoryStore(Path.Combine(root, "assets.json")), new RecentMapStore(Path.Combine(root, "recent.json")), settings);
            window.Show();
            try
            {
                await Until(() => window.FindControl<MapViewport>("Viewport")!.Assets != null && window.FindControl<MenuItem>("NewButton")!.IsEnabled);
                var document = window.FindControl<MapViewport>("Viewport")!.Document;
                int setupCount = 0, previewCount = 0; bool retained = false, seedRetained = false, resized = false;
                using var dialogs = Window.WindowOpenedEvent.AddClassHandler<Window>((dialog, _) =>
                {
                    if (dialog is NewMapWindow setup)
                    {
                        setupCount++;
                        Dispatcher.UIThread.Post(() =>
                        {
                            if (setupCount == 1)
                            {
                                setup.KindBox.SelectedItem = MapCreationKind.Terrain;
                                setup.WidthBox.Text = "16"; setup.HeightBox.Text = "16";
                                setup.SaveToggle.IsChecked = false;
                            }
                            else
                            {
                                seedRetained = setup.SelectedParameters?.GetValueOrDefault("SeedBox") == "572";
                                setup.NameBox.Text = "Renamed";
                                if (resize) setup.WidthBox.Text = "32";
                            }
                            setup.CreateButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        });
                    }
                    else if (dialog is TerrainGeneratorWindow preview)
                    {
                        previewCount++;
                        Dispatcher.UIThread.Post(() =>
                        {
                            if (previewCount == 1)
                            {
                                preview.GetLogicalDescendants().OfType<Button>().Single(button => button.Name == "QuickMapBack")
                                    .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                                retained = preview.IsVisible;
                                if (!resize) preview.Close(false);
                            }
                            else
                            {
                                resized = preview.GetLogicalDescendants().OfType<TextBlock>().Any(text => text.Text?.Contains("32 × 16", StringComparison.Ordinal) == true);
                                preview.Close(false);
                            }
                        });
                    }
                });
                typeof(MainWindow).GetMethod("QuickCreateClick", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                    .Invoke(window, [window, new RoutedEventArgs()]);
                await Until(() => setupCount == 2 && window.FindControl<MenuItem>("NewButton")!.IsEnabled);
                Assert.Equal(resize ? 2 : 1, previewCount);
                Assert.Equal(!resize, retained); Assert.True(seedRetained);
                if (resize) Assert.True(resized);
                Assert.Same(document, window.FindControl<MapViewport>("Viewport")!.Document);
            }
            finally { window.FindControl<MapViewport>("Viewport")!.History.Saved(); window.Close(); }
        }
    }

    private static async Task Until(Func<bool> condition)
    {
        for (int attempt = 0; attempt < 500; attempt++)
        { Dispatcher.UIThread.RunJobs(); if (condition()) return; await Task.Delay(20); }
        Assert.True(condition(), "Timed out waiting for quick-map navigation.");
    }

    [Fact]
    public void PortablePresetExportDoesNotIncludeMachineOutputSettings()
    {
        string root = Path.Combine(TestApplication.Root, Guid.NewGuid().ToString("N"));
        var store = new MapCreationStore(Path.Combine(root, "profile.json"));
        store.SavePreset("Shared", new(MapCreationKind.Terrain, 256, 128, "PrivateName", "Z:\\PrivateOutput", false),
            new Dictionary<string, string> { ["SeedBox"] = "42" });
        string path = Path.Combine(root, "shared.json");
        store.ExportPreset("Shared", path);
        string json = File.ReadAllText(path);
        Assert.DoesNotContain("PrivateOutput", json);
        Assert.DoesNotContain("PrivateName", json);
        var imported = new MapCreationStore(Path.Combine(root, "other-profile.json"));
        Assert.Equal("Shared", imported.ImportPreset(path));
        Assert.Equal(128, imported.Data.Presets["Shared"].Generation.Height);
        Assert.Equal("42", imported.Data.Presets["Shared"].Parameters["SeedBox"]);
        Assert.Throws<InvalidOperationException>(() => imported.ImportPreset(path));
        imported.RenamePreset("Shared", "Renamed");
        imported.DuplicatePreset("Renamed", "Copy");
        Assert.True(imported.DeletePreset("Renamed"));
        Assert.Single(imported.Data.Presets);
    }

    [Fact]
    public void LegacyPresetsAreMigratedWithoutLosingLastLocalSettings()
    {
        string root = Path.Combine(TestApplication.Root, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string path = Path.Combine(root, "legacy.json");
        File.WriteAllText(path, """
            {"Version":1,"Last":{"Kind":0,"Width":128,"Height":128,"Name":"Local","Directory":"D:\\Local","SaveImmediately":false},
             "Presets":{"Old":{"Settings":{"Kind":1,"Width":256,"Height":128,"Name":"Private","Directory":"E:\\Other"},"Parameters":{"SeedBox":"123"}}},"Generators":{}}
            """);
        var store = new MapCreationStore(path); store.Load();
        Assert.Equal(2, store.Data.Version);
        Assert.Equal("D:\\Local", store.Data.Last!.Directory);
        Assert.False(store.Data.Last.SaveImmediately);
        Assert.Equal(MapCreationKind.Terrain, store.Data.Presets["Old"].Generation.Kind);
        Assert.Equal("123", store.Data.Presets["Old"].Parameters["SeedBox"]);
        store.Save();
        Assert.DoesNotContain("E:\\\\Other", File.ReadAllText(path));
    }

    [Fact]
    public void InvalidImportsAndFailedWritesDoNotReplaceExistingPresets()
    {
        string root = Path.Combine(TestApplication.Root, Guid.NewGuid().ToString("N"));
        var store = new MapCreationStore(Path.Combine(root, "profile.json"));
        store.SavePreset("Keep", new(MapCreationKind.Blank));
        string original = File.ReadAllText(store.FilePath);
        string invalid = Path.Combine(root, "invalid.json");
        File.WriteAllText(invalid, """{"Version":1,"Name":"Bad","Preset":{"Generation":{"Kind":2,"Width":64,"Height":64},"Parameters":{}}}""");
        Assert.Throws<InvalidDataException>(() => store.ImportPreset(invalid));
        Assert.Single(store.Data.Presets); Assert.Equal(original, File.ReadAllText(store.FilePath));
        var unwritable = new MapCreationStore(root);
        var failure = Record.Exception(() => unwritable.SavePreset("Fail", new(MapCreationKind.Blank)));
        Assert.True(failure is IOException or UnauthorizedAccessException);
        Assert.Empty(unwritable.Data.Presets);
    }

    [Fact]
    public void PresetGrowthCannotSaveAProfileThatWouldBeRejectedOnReload()
    {
        string root = Path.Combine(TestApplication.Root, Guid.NewGuid().ToString("N"));
        var store = new MapCreationStore(Path.Combine(root, "profile.json"));
        var parameters = Enumerable.Range(0, 50).ToDictionary(index => "Parameter" + index, _ => new string('x', 4096));
        for (int index = 0; index < 5; index++) store.SavePreset("Preset" + index, new(MapCreationKind.Blank), parameters);
        string original = File.ReadAllText(store.FilePath);
        Assert.Throws<InvalidDataException>(() => store.SavePreset("Too large", new(MapCreationKind.Blank), parameters));
        Assert.Equal(5, store.Data.Presets.Count); Assert.Equal(original, File.ReadAllText(store.FilePath));
        var restored = new MapCreationStore(store.FilePath); restored.Load(); Assert.Equal(5, restored.Data.Presets.Count);
    }

    [AvaloniaFact]
    public async Task ChineseQuickCreationKeepsActionsVisibleAtSmallWindowHeights()
    {
        string root = Path.Combine(TestApplication.Root, "screens-" + Guid.NewGuid().ToString("N"));
        var language = new SettingsStore(Path.Combine(root, "settings.json"));
        string previousLanguage = LocalText.Language;
        language.Save(new(Path.Combine(TestApplication.Root, "Data"), "zh-CN")); LocalText.Initialize(language);
        var store = new MapCreationStore(Path.Combine(root, "profile.json"));
        store.SavePreset("测试方案", new(MapCreationKind.Blank));
        var window = new NewMapWindow(root, Available(), new(MapCreationKind.Province, Name: "Forest"), store);
        try
        {
            window.TemplateBox.SelectedItem = MapCreationTemplates.All.Single(template => template.Id == "outdoor-exploration");
            window.Show(); await Task.Delay(80); Dispatcher.UIThread.RunJobs();
            Assert.Equal("快速创建地图", window.Title);
            Assert.Equal("下一步：预览", window.CreateButton.Content);
            Assert.Equal("野外地图", MapCreationTemplates.KindLabel(MapCreationKind.Province));
            foreach (int height in new[] { 780, 480 })
            {
                window.Height = height; Dispatcher.UIThread.RunJobs();
                var position = window.CreateButton.TranslatePoint(default, window)!.Value;
                Assert.True(position.Y >= 0 && position.Y + window.CreateButton.Bounds.Height <= window.ClientSize.Height);
                string folder = Path.Combine(AppContext.BaseDirectory, "QuickMapScreenshots"); Directory.CreateDirectory(folder);
                using var frame = window.CaptureRenderedFrame(); Assert.NotNull(frame);
                using var stream = File.Create(Path.Combine(folder, $"setup-zh-{height}.png")); frame.Save(stream);
            }
        }
        finally
        {
            window.Close(); language.Save(new(Path.Combine(TestApplication.Root, "Data"), previousLanguage)); LocalText.Initialize(language);
        }
    }

    [AvaloniaFact]
    public async Task EachGeneratorKeepsAdvancedControlsAndClearCreationActions()
    {
        using var assets = AssetStore.Load(Path.Combine(TestApplication.Root, "Data"));
        var terrain = TerrainCatalogue.Load(); var walls = WallCatalogue.Read(WallCatalogue.ResolvePath());
        foreach (var kind in new[] { MapCreationKind.Terrain, MapCreationKind.Province, MapCreationKind.Dungeon })
        {
            var document = new MapDocument(128, 128);
            EditorWindow window = kind switch
            {
                MapCreationKind.Terrain => new TerrainGeneratorWindow(document, assets, terrain),
                MapCreationKind.Province => new ProvinceGeneratorWindow(document, assets, walls),
                _ => new TempleGeneratorWindow(document, assets, walls, terrain)
            };
            try
            {
                QuickMapPreview.Configure(window, new MapCreationSession(new(kind, 128, 128, "Preview", SaveImmediately: false)), _ => false);
                window.Show(); await Task.Delay(80); Dispatcher.UIThread.RunJobs();
                var controls = window.GetLogicalDescendants().ToArray();
                Assert.Single(controls.OfType<ComboBox>(), choice => choice.Name == "QuickMapDensity");
                Assert.False(Assert.Single(controls.OfType<Expander>()).IsExpanded);
                var apply = controls.OfType<Button>().Single(button => button.Name == "ApplyButton");
                Assert.Equal(LocalText.T("Create Map"), apply.Content);
                var back = controls.OfType<Button>().Single(button => button.Name == "QuickMapBack");
                var position = back.TranslatePoint(default, window)!.Value;
                Assert.True(position.X >= 0 && position.X + back.Bounds.Width <= window.ClientSize.Width);
                string folder = Path.Combine(AppContext.BaseDirectory, "QuickMapScreenshots"); Directory.CreateDirectory(folder);
                using var frame = window.CaptureRenderedFrame(); Assert.NotNull(frame);
                using var stream = File.Create(Path.Combine(folder, $"preview-{kind}.png")); frame.Save(stream);
            }
            finally { window.Close(); }
        }
    }
}
