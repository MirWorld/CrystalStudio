using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using CrystalStudio.Rendering;

namespace CrystalStudio.Tests;

public sealed class MapCreationInteractionTests
{
    [AvaloniaTheory]
    [InlineData(MapCreationKind.Terrain, "CoverageSlider", .63)]
    [InlineData(MapCreationKind.Province, "VegetationSlider", 73)]
    [InlineData(MapCreationKind.Dungeon, "RoomsSlider", 17)]
    public async Task ReturningToResizePreservesThePreviewDraftWithoutSaving(MapCreationKind kind, string sliderName, double value)
    {
        string root = Path.Combine(TestApplication.Root, "interaction-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var settings = new SettingsStore(Path.Combine(root, "profile", "settings.json"));
        settings.Save(new(Path.Combine(TestApplication.Root, "Data"), "en-US"));
        var window = new MainWindow(new AssetHistoryStore(Path.Combine(root, "assets.json")), new RecentMapStore(Path.Combine(root, "recent.json")), settings);
        window.Show();
        try
        {
            await Until(() => window.FindControl<MapViewport>("Viewport")!.Assets != null && window.FindControl<MenuItem>("NewButton")!.IsEnabled);
            var original = window.FindControl<MapViewport>("Viewport")!.Document;
            int forms = 0, previews = 0;
            string? restoredSeed = null;
            double restoredValue = -1;
            using var dialogs = Window.WindowOpenedEvent.AddClassHandler<Window>((dialog, _) =>
            {
                if (dialog is NewMapWindow wizard) Dispatcher.UIThread.Post(() =>
                {
                    forms++;
                    wizard.KindBox.SelectedItem = kind;
                    wizard.WidthBox.Text = forms == 1 ? "128" : "256";
                    wizard.HeightBox.Text = wizard.WidthBox.Text;
                    wizard.SaveToggle.IsChecked = false;
                    wizard.CreateButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                });
                else if (dialog is TerrainGeneratorWindow or ProvinceGeneratorWindow or TempleGeneratorWindow)
                {
                    var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
                    timer.Tick += (_, _) =>
                    {
                        timer.Stop(); previews++;
                        if (previews == 1)
                        {
                            dialog.FindControl<TextBox>("SeedBox")!.Text = "424242";
                            dialog.FindControl<Slider>(sliderName)!.Value = value;
                            dialog.GetLogicalDescendants().OfType<Button>().Single(button => button.Name == "QuickMapBack").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        }
                        else
                        {
                            restoredSeed = dialog.FindControl<TextBox>("SeedBox")!.Text;
                            restoredValue = dialog.FindControl<Slider>(sliderName)!.Value;
                            dialog.Close(false);
                        }
                    };
                    timer.Start();
                }
            });
            typeof(MainWindow).GetMethod("QuickCreateClick", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(window, [window, new RoutedEventArgs()]);
            await Until(() => previews == 2 && window.FindControl<MenuItem>("NewButton")!.IsEnabled);
            Assert.Equal(2, forms);
            Assert.Equal("424242", restoredSeed);
            Assert.Equal(value, restoredValue, 5);
            Assert.Same(original, window.FindControl<MapViewport>("Viewport")!.Document);
            Assert.False(File.Exists(Path.Combine(root, "profile", "map-creation.json")));
        }
        finally { window.FindControl<MapViewport>("Viewport")!.History.Saved(); window.Close(); }
    }

    [AvaloniaFact]
    public void ProvinceSceneRoundTripsKeepEachScenesAdjustmentsAndTheSharedSeed()
    {
        using var assets = AssetStore.Load(Path.Combine(TestApplication.Root, "Data"));
        var window = new ProvinceGeneratorWindow(new MapDocument(256, 256), assets);
        try
        {
            var scene = window.FindControl<ComboBox>("SceneList")!;
            var vegetation = window.FindControl<Slider>("VegetationSlider")!;
            var forest = window.FindControl<Slider>("ForestSlider")!;
            window.FindControl<TextBox>("SeedBox")!.Text = "424242";
            forest.Value = 31; vegetation.Value = 73;
            scene.SelectedIndex = 2;
            Assert.Equal(85, vegetation.Value);
            vegetation.Value = 91;
            scene.SelectedIndex = 1;
            Assert.Equal(31, forest.Value);
            Assert.Equal(73, vegetation.Value);
            scene.SelectedIndex = 2;
            Assert.Equal(91, vegetation.Value);
            Assert.False(window.FindControl<ComboBox>("StyleList")!.IsEnabled);
            Assert.Equal("424242", window.FindControl<TextBox>("SeedBox")!.Text);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void DungeonThemeRoundTripsKeepAdjustmentsWithoutChangingThemeConstraints()
    {
        using var assets = AssetStore.Load(Path.Combine(TestApplication.Root, "Data"));
        var window = (TempleGeneratorWindow)Preview(MapCreationKind.Dungeon, assets);
        try
        {
            var theme = window.FindControl<ComboBox>("ThemeList")!;
            var rooms = window.FindControl<Slider>("RoomsSlider")!;
            var size = window.FindControl<Slider>("SizeSlider")!;
            var width = window.FindControl<ComboBox>("WidthList")!;
            rooms.Value = 17; size.Value = 3; width.SelectedIndex = 0;
            theme.SelectedIndex = 1;
            Assert.Equal(9, rooms.Value);
            rooms.Value = 6; size.Value = 4;
            theme.SelectedIndex = 0;
            Assert.Equal(17, rooms.Value);
            Assert.Equal(3, size.Value);
            Assert.Equal(0, width.SelectedIndex);
            theme.SelectedIndex = 1;
            Assert.Equal(6, rooms.Value);
            Assert.Equal(4, size.Value);
            Assert.False(window.FindControl<ComboBox>("TempleList")!.IsEnabled);
            Assert.False(window.FindControl<ComboBox>("DungeonTypeList")!.IsEnabled);
        }
        finally { window.Close(); }
    }

    [AvaloniaTheory]
    [InlineData(MapCreationKind.Terrain, "CoverageSlider", 63, .63)]
    [InlineData(MapCreationKind.Province, "VegetationSlider", 73, 73)]
    [InlineData(MapCreationKind.Dungeon, "RoomsSlider", 17, 17)]
    [InlineData(MapCreationKind.Dungeon, "SizeSlider", 2, 3)]
    public void PreciseEditorsStaySynchronizedWithTheExistingSliderRules(MapCreationKind kind, string sliderName, double input, double expected)
    {
        using var assets = AssetStore.Load(Path.Combine(TestApplication.Root, "Data"));
        var window = Preview(kind, assets);
        try
        {
            var slider = window.FindControl<Slider>(sliderName)!;
            var editor = window.GetLogicalDescendants().OfType<NumericUpDown>().SingleOrDefault(control => control.Name == sliderName + "Value");
            Assert.NotNull(editor);
            editor.Value = (decimal)input;
            Assert.Equal(expected, slider.Value, 5);
            double multiplier = sliderName == "CoverageSlider" ? 100 : 1;
            slider.Value = slider.Maximum;
            Assert.Equal((decimal)(slider.Maximum * multiplier), editor.Value);
            slider.IsEnabled = false;
            Assert.False(editor.IsEnabled);
            Assert.DoesNotContain(sliderName + "Value", GeneratorPreferences.Capture(window).Keys);
        }
        finally { window.Close(); }
    }

    [AvaloniaTheory]
    [InlineData(MapCreationKind.Terrain)]
    [InlineData(MapCreationKind.Province)]
    [InlineData(MapCreationKind.Dungeon)]
    public void PreviewNavigationDoesNotChangeGeneratorParameters(MapCreationKind kind)
    {
        using var assets = AssetStore.Load(Path.Combine(TestApplication.Root, "Data"));
        using var image = RasterImage.Create(128, 128, 96, 96, RasterFormats.Bgra32, null, new byte[128 * 128 * 4], 128 * 4);
        var window = Preview(kind, assets);
        try
        {
            window.FindControl<Image>("PreviewImage")!.Source = image;
            var navigator = window.FindControl<Control>("PreviewNavigator");
            Assert.NotNull(navigator);
            var controls = navigator.GetLogicalDescendants().OfType<Control>().ToArray();
            var zoom = controls.OfType<TextBlock>().Single(control => control.Name == "PreviewZoomText");
            var original = GeneratorPreferences.Capture(window);
            var revision = window.GetType().GetField("revision", BindingFlags.Instance | BindingFlags.NonPublic)!;
            int initialRevision = (int)revision.GetValue(window)!;
            controls.OfType<Button>().Single(control => control.Name == "PreviewZoomIn").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal("125%", zoom.Text);
            controls.OfType<Button>().Single(control => control.Name == "PreviewFit").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal("100%", zoom.Text);
            Assert.Equal(original.OrderBy(pair => pair.Key), GeneratorPreferences.Capture(window).OrderBy(pair => pair.Key));
            Assert.Equal(initialRevision, (int)revision.GetValue(window)!);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void NumericTypingCommitsValidValuesAndDoesNotApplyInvalidText()
    {
        var slider = new Slider { Name = "PrecisionSlider", Minimum = 0, Maximum = 100, Value = 55, TickFrequency = 1, IsSnapToTickEnabled = true };
        var editor = new GeneratorValueEditor { Slider = slider };
        var other = new TextBox();
        var panel = new StackPanel(); panel.Children.Add(slider); panel.Children.Add(editor); panel.Children.Add(other);
        var window = new Window { Width = 400, Height = 240, Content = panel };
        window.Show();
        try
        {
            Dispatcher.UIThread.RunJobs(); editor.Focus();
            editor.Text = "73"; other.Focus(); Dispatcher.UIThread.RunJobs();
            Assert.Equal(73, slider.Value);
            editor.Focus(); editor.Text = "invalid"; other.Focus(); Dispatcher.UIThread.RunJobs();
            Assert.Equal(73, slider.Value);
            editor.Value = 999;
            Assert.Equal(100, slider.Value);
            editor.Value = -10;
            Assert.Equal(0, slider.Value);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void PreviewPanZoomFitAndSourceChangesRemainBounded()
    {
        using var image = RasterImage.Create(800, 500, 96, 96, RasterFormats.Bgra32, null, new byte[800 * 500 * 4], 800 * 4);
        using var alternate = RasterImage.Create(800, 500, 96, 96, RasterFormats.Bgra32, null, new byte[800 * 500 * 4], 800 * 4);
        using var detail = RasterImage.Create(128, 128, 96, 96, RasterFormats.Bgra32, null, new byte[128 * 128 * 4], 128 * 4);
        var preview = new GeneratorPreview { Image = new Image { Source = image, Stretch = Avalonia.Media.Stretch.Uniform } };
        var window = new Window { Width = 800, Height = 600, Content = preview };
        window.Show();
        try
        {
            Dispatcher.UIThread.RunJobs();
            var controls = preview.GetLogicalDescendants().OfType<Control>().ToArray();
            var zoomIn = controls.OfType<Button>().Single(control => control.Name == "PreviewZoomIn");
            var zoomOut = controls.OfType<Button>().Single(control => control.Name == "PreviewZoomOut");
            for (int count = 0; count < 30; count++) zoomIn.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal(8, preview.Zoom);
            Assert.False(zoomIn.IsEnabled);
            var before = preview.Pan;
            window.MouseDown(new Point(300, 200), MouseButton.Left);
            window.MouseMove(new Point(380, 240));
            window.MouseUp(new Point(380, 240), MouseButton.Left);
            Assert.NotEqual(before, preview.Pan);
            var released = preview.Pan;
            window.MouseMove(new Point(400, 260));
            Assert.Equal(released, preview.Pan);
            preview.Image!.Source = alternate;
            Assert.Equal(8, preview.Zoom);
            Assert.Equal(released, preview.Pan);
            preview.Image.Source = detail;
            Assert.Equal(1, preview.Zoom);
            Assert.Equal(default, preview.Pan);
            Assert.False(zoomOut.IsEnabled);
            var surface = controls.Single(control => control.Name == "PreviewSurface");
            surface.Focus();
            window.KeyPressQwerty(PhysicalKey.Equal, RawInputModifiers.None);
            window.KeyReleaseQwerty(PhysicalKey.Equal, RawInputModifiers.None);
            Assert.True(preview.Zoom > 1);
            window.KeyPressQwerty(PhysicalKey.Home, RawInputModifiers.None);
            window.KeyReleaseQwerty(PhysicalKey.Home, RawInputModifiers.None);
            Assert.Equal(1, preview.Zoom);
            preview.Image.Source = null;
            Assert.False(zoomIn.IsEnabled);
        }
        finally { window.Close(); }
    }

    [AvaloniaTheory]
    [InlineData(MapCreationKind.Terrain)]
    [InlineData(MapCreationKind.Province)]
    [InlineData(MapCreationKind.Dungeon)]
    public void SavedSeedIsVisibleEvenBeforeArtworkLoads(MapCreationKind kind)
    {
        using var assets = AssetStore.Load(Path.Combine(TestApplication.Root, "Data"));
        var window = Preview(kind, assets, new Dictionary<string, string> { ["SeedBox"] = "424242" });
        try { Assert.Equal("424242", window.FindControl<TextBox>("SeedBox")!.Text); }
        finally { window.Close(); }
    }

    [AvaloniaTheory]
    [InlineData(MapCreationKind.Terrain)]
    [InlineData(MapCreationKind.Province)]
    [InlineData(MapCreationKind.Dungeon)]
    public async Task LoadingArtworkDoesNotOverwriteEditsToRestoredParameters(MapCreationKind kind)
    {
        using var assets = AssetStore.Load(Path.Combine(TestApplication.Root, "Data"));
        var window = Preview(kind, assets, new Dictionary<string, string> { ["SeedBox"] = "17" });
        try
        {
            window.FindControl<TextBox>("SeedBox")!.Text = "424242";
            window.Show();
            await Until(() => window switch
            {
                TerrainGeneratorWindow terrain => terrain.PreferencesRestored,
                ProvinceGeneratorWindow province => province.PreferencesRestored,
                TempleGeneratorWindow dungeon => dungeon.PreferencesRestored,
                _ => false
            });
            Assert.Equal("424242", window.FindControl<TextBox>("SeedBox")!.Text);
        }
        finally { window.Close(); }
    }

    private static EditorWindow Preview(MapCreationKind kind, AssetStore assets, IReadOnlyDictionary<string, string>? parameters = null) => kind switch
    {
        MapCreationKind.Terrain => new TerrainGeneratorWindow(new MapDocument(256, 256), assets, TerrainCatalogue.Load(), parameters),
        MapCreationKind.Province => new ProvinceGeneratorWindow(new MapDocument(256, 256), assets, parameters: parameters),
        _ => new TempleGeneratorWindow(new MapDocument(256, 256), assets, WallCatalogue.Read(WallCatalogue.ResolvePath()), TerrainCatalogue.Load(), parameters)
    };

    private static async Task Until(Func<bool> condition)
    {
        for (int attempt = 0; attempt < 500; attempt++)
        {
            Dispatcher.UIThread.RunJobs();
            if (condition()) return;
            await Task.Delay(20);
        }
        Assert.True(condition(), "Timed out waiting for map creation interaction.");
    }
}
