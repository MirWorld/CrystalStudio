using System.IO.Compression;
using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CrystalStudio.Rendering;
using Path = System.IO.Path;

[assembly: AvaloniaTestApplication(typeof(CrystalStudio.Tests.TestApplication))]

namespace CrystalStudio.Tests;

public static class TestApplication
{
    public static readonly string Root = Path.Combine(Path.GetTempPath(), "CrystalStudio-Avalonia-" + Guid.NewGuid().ToString("N"));
    public static AppBuilder BuildAvaloniaApp()
    {
        Directory.CreateDirectory(Root);
        Environment.SetEnvironmentVariable("CRYSTALSTUDIO_PROFILE", Path.Combine(Root, "profile"));
        Fixture.CreateLibraries(Path.Combine(Root, "Data"));
        new SettingsStore().Save(new LibrarySettings(Path.Combine(Root, "Data"), "en-US"));
        return AppBuilder.Configure<App>().UseSkia().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
    }
}

internal static class Fixture
{
    public static void CreateLibraries(string root)
    {
        string directory = Path.Combine(root, "Map", "WemadeMir2"); Directory.CreateDirectory(directory);
        WriteLibrary(Path.Combine(directory, "Tiles.lib"), 96, 64, 20, 80, 160);
        WriteLibrary(Path.Combine(directory, "Smtiles.lib"), 48, 32, 80, 160, 20);
        WriteLibrary(Path.Combine(directory, "Objects.lib"), 48, 96, 160, 20, 80);
    }
    public static void WriteLibrary(string path, int width, int height, byte b, byte g, byte r)
    {
        byte[] pixels = new byte[width * height * 4];
        for (int i = 0; i < pixels.Length; i += 4) { pixels[i] = b; pixels[i + 1] = g; pixels[i + 2] = r; pixels[i + 3] = 255; }
        using var compressed = new MemoryStream();
        using (var gzip = new GZipStream(compressed, CompressionLevel.SmallestSize, true)) gzip.Write(pixels);
        using var writer = new BinaryWriter(File.Create(path));
        writer.Write(3); writer.Write(1); writer.Write(0); writer.Write(16);
        writer.Write((short)width); writer.Write((short)height); writer.Write((short)0); writer.Write((short)0);
        writer.Write((short)0); writer.Write((short)0); writer.Write((byte)0);
        writer.Write((int)compressed.Length); writer.Write(compressed.ToArray());
    }
    public static byte[] Pixels(RasterImage image)
    {
        byte[] bytes = new byte[image.PixelWidth * image.PixelHeight * 4]; image.CopyPixels(bytes, image.PixelWidth * 4, 0); return bytes;
    }
}

public sealed class MigrationTests
{
    [AvaloniaFact]
    public async Task GridRendersOverMapArtworkWhenZoomedToFitScale()
    {
        ThemeService.Initialize();
        ThemeService.ApplyPalette(false, Color.FromRgb(132, 152, 255));
        using var assets = AssetStore.Load(Path.Combine(TestApplication.Root, "Data"));
        var viewport = new MapViewport { Assets = assets };
        var document = new MapDocument(128, 128);
        for (int y = 0; y < document.Height; y++)
            for (int x = 0; x < document.Width; x++)
                document.Set(x, y, new Cell { BackLibrary = 0, BackImage = 1 });
        viewport.SetDocument(document);
        var window = new Window { Width = 800, Height = 600, Content = viewport };
        window.Show();
        try
        {
            await PumpUntil(() => viewport.Bounds.Width > 0 && viewport.Bounds.Height > 0);
            int builds = viewport.MapSceneBuilds;
            viewport.SetZoom(.3);
            await PumpUntil(() => viewport.MapSceneBuilds > builds);
            await PumpUntil(() => assets.Get(0, 0) != null);
            viewport.RefreshAssets();
            await Task.Delay(60); Dispatcher.UIThread.RunJobs();
            Assert.True(viewport.GridVisible);
            var origin = (Point)typeof(MapViewport).GetField("origin", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(viewport)!;
            using var frame = window.CaptureRenderedFrame()!;
            using var stream = new MemoryStream(); frame.Save(stream); stream.Position = 0;
            using var image = RasterImage.Load(stream);
            int pixelWidth = image.PixelWidth;
            byte[] pixels = Fixture.Pixels(image);
            int xLine = (int)Math.Round(origin.X + 48 * viewport.Zoom);
            int ySample = (int)Math.Round(origin.Y + 16 * viewport.Zoom);
            int line = (ySample * pixelWidth + xLine) * 4;
            int artwork = (ySample * pixelWidth + xLine + 3) * 4;
            Assert.True((pixels[line], pixels[line + 1], pixels[line + 2]) !=
                (pixels[artwork], pixels[artwork + 1], pixels[artwork + 2]),
                $"Zoom={viewport.Zoom}, origin={origin}, sample=({xLine},{ySample}), frame={pixelWidth}x{image.PixelHeight}, asset={assets.Libraries.Count}, cell={document.Get(0, 0).BackImage}");
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task HelpWindowRemainsOpenWithoutBlockingItsOwner()
    {
        var owner = new MainWindow(new AssetHistoryStore(Path.Combine(TestApplication.Root, "help-assets.json")),
            new RecentMapStore(Path.Combine(TestApplication.Root, "help-recent.json")));
        owner.Show();
        try
        {
            await Task.Delay(50);
            Dispatcher.UIThread.RunJobs();
            var openHelp = typeof(MainWindow).GetMethod("ShortcutsClick", BindingFlags.NonPublic | BindingFlags.Instance)!;
            var helpField = typeof(MainWindow).GetField("shortcutsWindow", BindingFlags.NonPublic | BindingFlags.Instance)!;
            openHelp.Invoke(owner, [owner, new RoutedEventArgs()]);
            var help = Assert.IsType<EditorWindow>(helpField.GetValue(owner));
            Assert.True(help.IsVisible);
            Assert.True(owner.IsEnabled);

            openHelp.Invoke(owner, [owner, new RoutedEventArgs()]);
            Assert.Same(help, helpField.GetValue(owner));

            help.Close();
            Assert.Null(helpField.GetValue(owner));
        }
        finally { owner.Close(); }
    }

    [AvaloniaFact]
    public async Task CompactSliderKeepsKeyboardAndPointerValueChanges()
    {
        var slider = new Slider { Minimum = 3, Maximum = 31, Value = 3, TickFrequency = 2, IsSnapToTickEnabled = true, Width = 100, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top };
        var window = new Window { Width = 150, Height = 80, Content = slider }; window.Show();
        try
        {
            await Task.Delay(30); Dispatcher.UIThread.RunJobs();
            Assert.Equal(24, slider.Bounds.Height);
            slider.Focus(); window.KeyPressQwerty(PhysicalKey.ArrowRight, RawInputModifiers.None); window.KeyReleaseQwerty(PhysicalKey.ArrowRight, RawInputModifiers.None);
            Assert.Equal(5, slider.Value);
            window.MouseDown(new Point(85, 12), MouseButton.Left); window.MouseUp(new Point(85, 12), MouseButton.Left);
            Assert.True(slider.Value > 5, string.Join("; ", slider.GetVisualDescendants().OfType<Control>().Select(c => $"{c.GetType().Name}#{c.Name}={c.Bounds}")) + $" Value={slider.Value}, LargeChange={slider.LargeChange}");
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task CategoriesUseVerticalTabsAndGridCardsWithCompleteTooltips()
    {
        var window = await ShowCategoryWindow();
        try
        {
            var tabs = window.FindControl<TabControl>("CategoryTabs")!;
            Assert.Equal(Dock.Left, tabs.TabStripPlacement);
            var firstTab = Assert.IsType<TabItem>(tabs.ContainerFromIndex(0));
            var secondTab = Assert.IsType<TabItem>(tabs.ContainerFromIndex(1));
            Assert.Equal(firstTab.Bounds.X, secondTab.Bounds.X);
            Assert.True(secondTab.Bounds.Y > firstTab.Bounds.Y);
            Assert.DoesNotContain(tabs.GetVisualDescendants(), v => v is TreeView);
            var list = CategoryList(tabs);
            var first = Assert.IsType<ListBoxItem>(list.ContainerFromIndex(0));
            var second = Assert.IsType<ListBoxItem>(list.ContainerFromIndex(1));
            Assert.Equal(first.Bounds.Y, second.Bounds.Y);
            Assert.True(second.Bounds.X > first.Bounds.X);
            var node = Assert.IsType<StampCategoryNode>(list.Items[0]);
            Assert.Equal("Market stall", node.ShortLabel);
            Assert.Equal("Market stall", Assert.IsType<StampCategoryNode>(list.Items[1]).ShortLabel);
            var content = first.GetVisualDescendants().OfType<StackPanel>().Single(p => ToolTip.GetTip(p) is TextBlock);
            var tip = Assert.IsType<TextBlock>(ToolTip.GetTip(content));
            // Initial sprite decoding can replace virtualized cards while the tooltip opens.
            // Reacquire the live card instead of inspecting a detached container after an await.
            for (int attempt = 0; attempt < 150; attempt++)
            {
                first = Assert.IsType<ListBoxItem>(list.ContainerFromIndex(0));
                node = Assert.IsType<StampCategoryNode>(list.Items[0]);
                content = first.GetVisualDescendants().OfType<StackPanel>().Single(p => ToolTip.GetTip(p) is TextBlock);
                tip = Assert.IsType<TextBlock>(ToolTip.GetTip(content));
                ToolTip.SetIsOpen(content, true);
                Dispatcher.UIThread.RunJobs();
                if (content.GetVisualRoot() != null && !string.IsNullOrEmpty(tip.Text)) break;
                await Task.Delay(20);
            }
            Assert.Contains(node.Label, tip.Text);
            Assert.Contains(node.Detail, tip.Text);
            Assert.Contains(node.Stamp!.File, tip.Text);
            ToolTip.SetIsOpen(content, false);
            var caption = content.Children.OfType<TextBlock>().Single();
            var image = content.GetVisualDescendants().OfType<Image>().Single();
            Assert.Equal(node.ShortLabel, caption.Text);
            Assert.Equal(TextTrimming.CharacterEllipsis, caption.TextTrimming);
            Assert.True(caption.TranslatePoint(default, content)!.Value.Y >= image.TranslatePoint(default, content)!.Value.Y + image.Bounds.Height);
            list.SelectedIndex = 0;
            await PumpUntil(() => window.FindControl<MapViewport>("Viewport")!.Stamp != null);
            list = CategoryList(tabs);
            first = Assert.IsType<ListBoxItem>(list.ContainerFromIndex(0));
            Assert.True(first.Focus(), $"The category card must accept keyboard focus. Focusable={first.Focusable}, Visible={first.IsEffectivelyVisible}, Enabled={first.IsEffectivelyEnabled}, Root={first.GetVisualRoot()}, Bounds={first.Bounds}");
            window.KeyPressQwerty(PhysicalKey.ArrowRight, RawInputModifiers.None); window.KeyReleaseQwerty(PhysicalKey.ArrowRight, RawInputModifiers.None);
            Assert.Equal(1, list.SelectedIndex);
            Assert.False(window.FindControl<MapViewport>("Viewport")!.History.Dirty);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task CategorySwitchesRememberPagesAndSelectionWhileSearchAndGroupingRemainAvailable()
    {
        var window = await ShowCategoryWindow();
        try
        {
            var tabs = window.FindControl<TabControl>("CategoryTabs")!;
            var trees = tabs.Items.OfType<StampCategoryGroup>().Single(g => g.Name == "Tree");
            tabs.SelectedItem = trees;
            await PumpUntil(() => CategoryList(tabs).Items.Count == 24);
            window.FindControl<Button>("NextButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await PumpUntil(() => trees.Page == 1 && trees.Items.Count == 24);
            var list = CategoryList(tabs); list.SelectedIndex = 2;
            string selected = ((StampCategoryNode)list.SelectedItem!).Stamp!.File;
            await PumpUntil(() => window.FindControl<MapViewport>("Viewport")!.StampName == ((StampCategoryNode)list.SelectedItem!).Label);
            var scroll = list.GetVisualDescendants().OfType<ScrollViewer>().First();
            scroll.Offset = new Vector(0, 160);
            await PumpUntil(() => trees.ScrollOffset.Y > 0);
            double savedOffset = trees.ScrollOffset.Y;
            tabs.SelectedIndex = 0;
            await PumpUntil(() => tabs.SelectedItem is StampCategoryGroup { Name: "Building" } && CategoryList(tabs).Items.Count == 2);
            tabs.SelectedItem = trees;
            await PumpUntil(() => CategoryList(tabs).SelectedItem is StampCategoryNode node && node.Stamp!.File == selected);
            Assert.Equal(1, trees.Page);
            Assert.Equal(savedOffset, CategoryList(tabs).GetVisualDescendants().OfType<ScrollViewer>().First().Offset.Y);
            Assert.Equal("Page 2 of 3", window.FindControl<TextBlock>("PageLabel")!.Text);
            Assert.All(tabs.Items.OfType<StampCategoryGroup>().Where(g => !ReferenceEquals(g, trees)), g => Assert.Empty(g.Items));
            var search = window.FindControl<TextBox>("AssetSearch")!;
            search.Text = "missing-category-stamp";
            await PumpUntil(() => tabs.Items.Count == 0);
            Assert.True(window.FindControl<StackPanel>("AssetEmpty")!.IsVisible);
            search.Clear();
            await PumpUntil(() => tabs.Items.Count == 3);
            var grouping = (ComboBox)typeof(MainWindow).GetField("categoryGrouping", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(window)!;
            grouping.SelectedItem = "Environment";
            await PumpUntil(() => tabs.Items.OfType<StampCategoryGroup>().Any(g => g.Name == "Outdoor"));
            Assert.Single(tabs.Items);
            Assert.False(window.FindControl<MapViewport>("Viewport")!.History.Dirty);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task LanguageMenuSavesPreferenceWithoutChangingCurrentWindowsOrEditingState()
    {
        var store = new SettingsStore(); var saved = store.Load();
        store.Save(saved with { Language = "en-US" }); LocalText.Initialize(store);
        var window = await ShowCategoryWindow();
        var settings = new SettingsWindow(saved.LibraryFolder, _ => Task.CompletedTask) { Owner = window };
        settings.Show();
        var viewport = window.FindControl<MapViewport>("Viewport")!;
        var document = viewport.Document; var history = viewport.History;
        history.Record(document, 2, 2); document.Set(2, 2, new Cell { BackImage = 1 }); history.Commit();
        try
        {
            var languages = window.FindControl<MenuItem>("ViewMenu")!.Items.OfType<MenuItem>().Single(item => item.Name == "LanguageMenu");
            var chinese = languages.Items.OfType<MenuItem>().Single(item => item.Name == "ChineseLanguage");
            chinese.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent)); Dispatcher.UIThread.RunJobs();
            Assert.Equal("zh-CN", store.Load().Language); Assert.True(chinese.IsChecked);
            Assert.Equal(saved.LibraryFolder, store.Load().LibraryFolder);
            Assert.Equal("en-US", LocalText.Language);
            Assert.Equal("Active Asset", window.FindControl<TextBlock>("SelectedPreviewCaption")!.Text);
            Assert.Equal("Crystal Studio Settings", settings.Title);
            Assert.Equal("Language saved. Restart Crystal Studio to apply.", window.FindControl<TextBlock>("Status")!.Text);
            Assert.Same(document, viewport.Document); Assert.Same(history, viewport.History);
            Assert.True(history.Dirty); Assert.True(history.CanUndo); Assert.Equal(1, document.Get(2, 2).BackImage);
            Assert.Equal(Key.S, window.FindControl<MenuItem>("SaveButton")!.InputGesture!.Key);
            Assert.Equal(KeyModifiers.Control, window.FindControl<MenuItem>("SaveButton")!.InputGesture!.KeyModifiers);
            history.Undo(document); Assert.Equal(0, document.Get(2, 2).BackImage);
        }
        finally { history.Saved(); settings.Close(); window.Close(); store.Save(saved); LocalText.Initialize(store); }
    }

    [AvaloniaFact]
    public async Task ChineseStartupSupportsCategorySearchAndPendingPreferenceKeepsSelectionAndPage()
    {
        var store = new SettingsStore(); var saved = store.Load();
        store.Save(saved with { Language = "zh-CN" }); LocalText.Initialize(store);
        var window = await ShowCategoryWindow();
        try
        {
            Assert.Equal("当前资源", window.FindControl<TextBlock>("SelectedPreviewCaption")!.Text);
            Assert.StartsWith("未命名", window.Title);
            typeof(MainWindow).GetMethod("UpdateCellInspector", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window, [2, 2]);
            Assert.Contains("物体", window.FindControl<TextBlock>("Inspector")!.Text);
            var tabs = window.FindControl<TabControl>("CategoryTabs")!;
            var trees = tabs.Items.OfType<StampCategoryGroup>().Single(group => group.Name == "Tree");
            tabs.SelectedItem = trees;
            window.FindControl<Button>("NextButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await PumpUntil(() => trees.Page == 1);
            var list = CategoryList(tabs); list.SelectedIndex = 2;
            await PumpUntil(() => window.FindControl<MapViewport>("Viewport")!.Stamp != null);
            var selected = list.SelectedItem; var items = trees.Items.ToArray();
            var scroll = list.GetVisualDescendants().OfType<ScrollViewer>().First(); scroll.Offset = new Vector(0, 160);
            await PumpUntil(() => trees.ScrollOffset.Y > 0); double offset = scroll.Offset.Y;
            LocalText.SaveLanguage("en-US", store); Dispatcher.UIThread.RunJobs();
            Assert.Equal("zh-CN", LocalText.Language);
            Assert.Equal("第 2 页，共 3 页", window.FindControl<TextBlock>("PageLabel")!.Text);
            Assert.Same(trees, tabs.SelectedItem); Assert.Same(selected, list.SelectedItem);
            Assert.Equal(items, trees.Items); Assert.Equal(offset, scroll.Offset.Y);
            Assert.Contains(tabs.GetVisualDescendants().OfType<TextBlock>(), text => text.Text == "树木");
            Assert.Equal("Tree", trees.Name);
            var node = (StampCategoryNode)selected!;
            Assert.Equal("Palm tree", node.ShortLabel);
            Assert.Contains(node.Label, node.Description); Assert.Contains(node.Stamp!.File, node.Description);
            Assert.Contains("树木", node.Description);
            var search = window.FindControl<TextBox>("AssetSearch")!; search.Text = "树木";
            await PumpUntil(() => tabs.Items.Count == 1 && tabs.SelectedItem is StampCategoryGroup { Name: "Tree" });
            Assert.Equal(54, ((StampCategoryGroup)tabs.SelectedItem!).Members.Count);
            search.Clear(); await PumpUntil(() => tabs.Items.Count == 3);
            Assert.False(window.FindControl<MapViewport>("Viewport")!.History.Dirty);
        }
        finally { window.Close(); store.Save(saved); LocalText.Initialize(store); }
    }

    [AvaloniaFact]
    public async Task SettingsSavesLanguageWithoutApplyingLibrariesAndNewWindowsUseItAfterStartup()
    {
        var store = new SettingsStore(); var saved = store.Load(); bool librariesApplied = false;
        store.Save(saved with { Language = "en-US" }); LocalText.Initialize(store);
        var window = new SettingsWindow(saved.LibraryFolder, _ => { librariesApplied = true; return Task.CompletedTask; }, true);
        window.Show();
        try
        {
            await PumpUntil(() => window.GetVisualDescendants().OfType<ComboBox>().Any(combo => combo.Name == "LanguageSelector"));
            var selector = window.GetVisualDescendants().OfType<ComboBox>().Single(combo => combo.Name == "LanguageSelector");
            selector.SelectedIndex = 0;
            Assert.Equal("zh-CN", store.Load().Language); Assert.Equal("en-US", LocalText.Language);
            Assert.Equal("Welcome to Crystal Studio", window.Title);
            Assert.False(librariesApplied); Assert.Equal(saved.LibraryFolder, store.Load().LibraryFolder);
            Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(), text => text.Text == "Language saved. Restart Crystal Studio to apply.");
            window.Close(); LocalText.Initialize(store);
            window = new SettingsWindow(saved.LibraryFolder, _ => Task.CompletedTask, true); window.Show();
            await PumpUntil(() => window.GetVisualDescendants().OfType<ComboBox>().Any(combo => combo.Name == "LanguageSelector"));
            Assert.Equal("欢迎使用 Crystal Studio", window.Title);
            selector = window.GetVisualDescendants().OfType<ComboBox>().Single(combo => combo.Name == "LanguageSelector");
            Assert.Equal(0, selector.SelectedIndex);
            Assert.Equal("资源面板", LocalText.T("Palette"));
            Assert.Equal("Custom resource 物体", LocalText.T("Custom resource 物体"));
            Assert.Equal("船只", LocalText.T("Boat"));
            selector.SelectedIndex = 1;
            Assert.Equal("en-US", store.Load().Language); Assert.Equal("zh-CN", LocalText.Language);
            Assert.Equal("欢迎使用 Crystal Studio", window.Title);
            window.Close(); LocalText.Initialize(store);
            window = new SettingsWindow(saved.LibraryFolder, _ => Task.CompletedTask, true); window.Show();
            Assert.Equal("Welcome to Crystal Studio", window.Title);
            Assert.Equal(saved.LibraryFolder, store.Load().LibraryFolder);
            Assert.Throws<ArgumentException>(() => LocalText.SaveLanguage("fr-FR", store));
        }
        finally { window.Close(); store.Save(saved); LocalText.Initialize(store); }
    }

    [AvaloniaFact]
    public async Task ChineseCategoryChoicesKeepCanonicalValuesForSaving()
    {
        var store = new SettingsStore(); var saved = store.Load();
        store.Save(saved with { Language = "zh-CN" }); LocalText.Initialize(store);
        var picker = new StampCategoryPicker(["Boat", "Outdoor"], ["Boat"]);
        var window = new EditorWindow { Content = picker }; window.Show();
        try
        {
            await PumpUntil(() => picker.GetVisualDescendants().OfType<ToggleButton>().Any(button => button.Content?.ToString()?.Contains("船只", StringComparison.Ordinal) == true));
            Assert.Equal(new[] { "Boat" }, picker.SelectedValues);
            var checks = (List<CheckBox>)typeof(StampCategoryPicker).GetField("choices", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(picker)!;
            Assert.Equal("船只", checks[0].Content);
            checks[1].IsChecked = true;
            Assert.Equal(new[] { "Boat", "Outdoor" }, picker.SelectedValues);
            LocalText.SaveLanguage("en-US", store); Dispatcher.UIThread.RunJobs();
            Assert.Equal("船只", checks[0].Content); Assert.Equal(new[] { "Boat", "Outdoor" }, picker.SelectedValues);
        }
        finally { window.Close(); store.Save(saved); LocalText.Initialize(store); }
    }

    [AvaloniaFact]
    public async Task GeneratorWindowsUseStartupLanguageAndRetainCanonicalChoices()
    {
        var store = new SettingsStore(); var saved = store.Load();
        using var assets = AssetStore.Load(Path.Combine(TestApplication.Root, "Data"));
        var document = new MapDocument(256, 256); var terrain = TerrainCatalogue.Load(); var walls = WallCatalogue.Read(WallCatalogue.ResolvePath());
        try
        {
            foreach (string language in new[] { "zh-CN", "en-US" })
            {
                store.Save(saved with { Language = language }); LocalText.Initialize(store);
                var windows = new (EditorWindow Window, string English, string Chinese)[]
                {
                    (new WallBrushWindow(walls.Families, assets), "Wall Brush", "墙体笔刷"),
                    (new SpecialBrushWindow(assets, new TreeBrushOptions(), 3, []), "Special Brush", "特殊笔刷"),
                    (new TownStampWindow(assets, TownCatalogue.Load()), "Generate Town", "生成城镇"),
                    (new TerrainGeneratorWindow(document, assets, terrain), "Generate Terrain", "生成地形"),
                    (new ProvinceGeneratorWindow(document, assets, walls), "Province Generator", "省域生成器"),
                    (new TempleGeneratorWindow(document, assets, walls, terrain), "Generate Dungeon", "生成地牢")
                };
                try
                {
                    foreach (var pair in windows) pair.Window.Show();
                    // Terrain choices load asynchronously. Capture the selection only
                    // after loading, so completion is not mistaken for a language change.
                    await PumpUntil(() => windows.All(pair => pair.Window.GetVisualDescendants().OfType<ComboBox>().Any()) &&
                        windows.Select(pair => pair.Window).OfType<TerrainGeneratorWindow>()
                            .All(window => window.FindControl<ComboBox>("BiomeList")?.SelectedItem != null));
                    foreach (var pair in windows) Assert.Equal(language == "zh-CN" ? pair.Chinese : pair.English, pair.Window.Title);
                    var selected = windows.SelectMany(pair => pair.Window.GetVisualDescendants().OfType<ComboBox>()).ToDictionary(combo => combo, combo => combo.SelectedItem);
                    Assert.NotEmpty(selected);
                    LocalText.SaveLanguage(language == "zh-CN" ? "en-US" : "zh-CN", store); Dispatcher.UIThread.RunJobs();
                    foreach (var pair in windows) Assert.Equal(language == "zh-CN" ? pair.Chinese : pair.English, pair.Window.Title);
                    Assert.All(selected, pair => Assert.Equal(pair.Value, pair.Key.SelectedItem));
                }
                finally { foreach (var pair in windows) pair.Window.Close(); }
            }
        }
        finally { store.Save(saved); LocalText.Initialize(store); }
    }

    [Fact]
    public void BilingualResourcesHaveCompleteMatchingFormatArguments()
    {
        Assert.NotEmpty(LocalText.Resources);
        foreach (var pair in LocalText.Resources)
        {
            Assert.False(string.IsNullOrWhiteSpace(pair.Value.English), pair.Key);
            Assert.False(string.IsNullOrWhiteSpace(pair.Value.Chinese), pair.Key);
            var english = System.Text.CompositeFormat.Parse(pair.Value.English);
            var chinese = System.Text.CompositeFormat.Parse(pair.Value.Chinese);
            Assert.True(english.MinimumArgumentCount == chinese.MinimumArgumentCount, pair.Key);
            static string[] Indices(string value) => System.Text.RegularExpressions.Regex.Matches(value, @"(?<!\{)\{(\d+)(?:[, :][^}]*)?\}(?!\})").Select(match => match.Groups[1].Value).Distinct().Order().ToArray();
            Assert.Equal(Indices(pair.Value.English), Indices(pair.Value.Chinese));
            var arguments = Enumerable.Range(0, english.MinimumArgumentCount).Select(index => (object)index).ToArray();
            _ = string.Format(System.Globalization.CultureInfo.InvariantCulture, english, arguments);
            _ = string.Format(System.Globalization.CultureInfo.InvariantCulture, chinese, arguments);
        }
    }

    [AvaloniaFact]
    public async Task CategoryPreviewsLoadVisibleCardsOnlyAndCancelWhenHidden()
    {
        var started = new System.Collections.Concurrent.ConcurrentDictionary<int, byte>();
        var cancelled = new System.Collections.Concurrent.ConcurrentDictionary<int, byte>();
        var nodes = Enumerable.Range(0, 24).Select(index => new StampCategoryNode
        {
            Label = "Preview " + index, LoadWhenVisible = true,
            LivePreviewLoader = async token =>
            {
                started.TryAdd(index, 0);
                try { await Task.Delay(Timeout.Infinite, token); return null; }
                finally { cancelled.TryAdd(index, 0); }
            }
        }).ToArray();
        var list = new ListBox
        {
            ItemsSource = nodes,
            ItemsPanel = new Avalonia.Controls.Templates.FuncTemplate<Panel?>(() => new ResponsivePanel { ItemMinWidth = 100 }),
            ItemTemplate = (Avalonia.Controls.Templates.IDataTemplate)Application.Current!.FindResource("StampCategoryCardTemplate")!,
            ItemContainerTheme = (Avalonia.Styling.ControlTheme)Application.Current!.FindResource("CategoryCard")!
        };
        ScrollViewer.SetHorizontalScrollBarVisibility(list, Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled);
        var window = new Window { Width = 240, Height = 150, Content = list }; window.Show();
        try
        {
            await PumpUntil(() => started.Count == 2);
            Assert.All(started.Keys, index => Assert.InRange(index, 0, 3));
            var scroll = list.GetVisualDescendants().OfType<ScrollViewer>().First();
            scroll.Offset = new Vector(0, 800);
            await PumpUntil(() => started.Keys.Any(index => index >= 12));
            Assert.True(cancelled.ContainsKey(0));
            Assert.InRange(started.Count, 3, 6);
            window.Content = null;
            await PumpUntil(() => cancelled.Count == started.Count);
        }
        finally { window.Close(); }
    }

    private static ListBox CategoryList(TabControl tabs) => tabs.GetVisualDescendants().OfType<ListBox>().Single(l => l.Name == "CategoryItems");

    private static async Task PumpUntil(Func<bool> condition)
    {
        for (int i = 0; i < 150 && !condition(); i++) { await Task.Delay(20); Dispatcher.UIThread.RunJobs(); }
        Assert.True(condition(), "The expected category UI state did not become available.");
        await Task.Delay(30); Dispatcher.UIThread.RunJobs();
    }

    private static async Task<MainWindow> ShowCategoryWindow()
    {
        string root = Path.Combine(TestApplication.Root, "categories-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var stamps = new List<CategorizedStamp>();
        for (int i = 0; i < 57; i++)
        {
            string file = $"test_{i:00000}.X", type = i < 2 ? "Building" : i < 56 ? "Tree" : "Wall", name = i < 2 ? "Market stall" : "Palm tree";
            CellCodec.SaveStamp(Path.Combine(root, file), [new StampCell(0, 0, new Cell { Foreground = new Front(2, 1) })]);
            string separator = i == 0 ? " \u2014 " : " - ";
            stamps.Add(new CategorizedStamp(file, file, $"{name}{separator}test_{i:00000}", type, name, ["Province"], ["BichonProvince"], ["Outdoor"], [], null, "unknown", null, false, "unreviewed"));
        }
        var window = new MainWindow(new AssetHistoryStore(Path.Combine(root, "assets.json")), new RecentMapStore(Path.Combine(root, "recent.json")));
        window.Show();
        await PumpUntil(() => window.FindControl<MapViewport>("Viewport")!.Assets != null &&
            !(bool)typeof(MainWindow).GetField("stampIndexLoading", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(window)! &&
            !(bool)typeof(MainWindow).GetField("busy", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(window)!);
        typeof(MainWindow).GetField("categoryIndex", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(window, new StampCategoryIndex(1, root, root, stamps));
        typeof(MainWindow).GetField("categoriesAttempted", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(window, true);
        window.GetVisualDescendants().OfType<TabControl>().Single(t => t.Items.Count == 4).SelectedIndex = 3;
        await PumpUntil(() => window.FindControl<TabControl>("CategoryTabs")!.GetVisualDescendants().OfType<ListBox>().Any(l => l.Items.Count > 0));
        return window;
    }

    [AvaloniaFact]
    public async Task CancelButtonClosesModalAndReturnsFalse()
    {
        var owner = new EditorWindow(); owner.Show();
        var button = new Button { Content = "Cancel", IsCancel = true };
        var dialog = new EditorWindow { Content = button };
        var completion = ((Window)dialog).ShowDialog<bool?>(owner);
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.False(dialog.IsVisible); Assert.False(await completion);
        owner.Close();
    }

    [AvaloniaFact]
    public async Task CompactTemplatesRetainOriginalToolLayerAndPaletteDimensions()
    {
        var window = new MainWindow(); window.Show();
        try
        {
            for (int i = 0; i < 30; i++) { await Task.Delay(20); Dispatcher.UIThread.RunJobs(); }
            var tools = window.FindControl<ResponsivePanel>("ToolPanel")!.Children.OfType<RadioButton>().ToArray();
            Assert.Equal(12, tools.Length);
            Assert.All(tools, tool => { Assert.Equal(32, tool.Bounds.Width); Assert.Equal(32, tool.Bounds.Height); });
            var eye = window.FindControl<CheckBox>("GroundVisible")!;
            Assert.Equal(22, eye.Bounds.Width); Assert.Equal(22, eye.Bounds.Height);
            Assert.Contains(eye.GetVisualDescendants(), visual => visual is Avalonia.Controls.Shapes.Path { Name: "Eye" });
            var list = window.FindControl<ListBox>("LayerList")!;
            Assert.All(list.Items.OfType<ListBoxItem>(), row => Assert.InRange(row.Bounds.Height, 25, 28));
            var paletteTabs = window.GetVisualDescendants().OfType<TabControl>().Single(t => t.Items.Count == 4);
            Assert.All(paletteTabs.Items.OfType<TabItem>(), tab => { Assert.Equal(11, tab.FontSize); Assert.InRange(tab.Bounds.Height, 25, 32); });
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task HoverKeepsMapAndMinimapRetainedWhilePanRefreshesThem()
    {
        ThemeService.Initialize();
        using var assets = AssetStore.Load(Path.Combine(TestApplication.Root, "Data"));
        var viewport = new MapViewport { Assets = assets };
        var document = new MapDocument(64, 64);
        for (int y = 0; y < 64; y += 2) for (int x = 0; x < 64; x += 2)
            document.Set(x, y, new Cell { BackLibrary = 0, BackImage = 1 });
        viewport.SetDocument(document);
        var window = new Window { Width = 800, Height = 600, Content = viewport };
        window.Show();
        try
        {
            for (int i = 0; i < 30; i++) { await Task.Delay(20); Dispatcher.UIThread.RunJobs(); }
            viewport.RefreshAssets(); await Task.Delay(40); Dispatcher.UIThread.RunJobs();
            int builds = viewport.MapSceneBuilds, submissions = viewport.MapRenderSubmissions, cameras = viewport.MapCameraUpdates, recordings = viewport.MapWorldRecordings, minimaps = 0;
            byte[] CapturePixels()
            {
                using var frame = window.CaptureRenderedFrame()!; using var stream = new MemoryStream();
                frame.Save(stream); stream.Position = 0; using var image = RasterImage.Load(stream);
                return Fixture.Pixels(image);
            }
            var beforePan = CapturePixels();
            Assert.Equal(new byte[] { 20, 80, 160, 255 }, beforePan.AsSpan((100 * 800 + 75) * 4, 4).ToArray());
            viewport.MinimapInvalidated += () => minimaps++;
            for (int i = 0; i < 80; i++)
            {
                window.MouseMove(new Point(100 + i * 3, 150 + i));
                Dispatcher.UIThread.RunJobs();
            }
            await Task.Delay(40); Dispatcher.UIThread.RunJobs();
            Assert.Equal(builds, viewport.MapSceneBuilds);
            Assert.Equal(submissions, viewport.MapRenderSubmissions);
            Assert.Equal(0, minimaps);
            viewport.PanBy(new Vector(30, 20));
            await Task.Delay(40); Dispatcher.UIThread.RunJobs();
            Assert.True(viewport.MapSceneBuilds > builds);
            Assert.True(viewport.MapCameraUpdates > cameras);
            Assert.Equal(recordings, viewport.MapWorldRecordings);
            Assert.True(minimaps > 0);
            var afterPan = CapturePixels();
            Assert.False(beforePan.AsSpan((100 * 800 + 75) * 4, 4).SequenceEqual(afterPan.AsSpan((100 * 800 + 75) * 4, 4)));
        }
        finally { viewport.StopBackgroundWork(); window.Close(); }
    }

    [Fact]
    public void AtlasBatchesPreserveTransparencyPainterOrderAndIndependentSpriteScaling()
    {
        using var atlas = RasterImage.Create(2, 2, 96, 96, RasterFormats.Bgra32, null,
            [0, 0, 255, 255, 0, 255, 0, 128, 255, 0, 0, 200, 255, 255, 255, 0], 8);
        var sprites = Enumerable.Range(0, 8200).Select(i =>
            (Source: new Rect(i % 2, (i / 2) % 2, 1, 1), Destination: new Rect(i % 101, (i / 101) % 83, 3, 5))).ToArray();
        var expected = new SceneVisual(); var actual = new SceneVisual();
        foreach (var s in sprites) expected.Drawing.Children.Add(new SceneImage(atlas, s.Destination, s.Source));
        actual.Drawing.Children.Add(new SceneAtlasBatch(atlas, sprites));
        var a = new RasterTarget(110, 90, 96, 96, RasterFormats.Pbgra32); a.Render(expected);
        var b = new RasterTarget(110, 90, 96, 96, RasterFormats.Pbgra32); b.Render(actual);
        Assert.Equal(Fixture.Pixels(a), Fixture.Pixels(b));
    }

    [Fact]
    public void OverviewRasterCachePreservesPixelsAtPhysicalResolution()
    {
        var scene = new SceneGroup();
        scene.Children.Add(new SceneRectangle(new Avalonia.Media.Immutable.ImmutableSolidColorBrush(Color.FromRgb(20, 40, 80)), null, new Rect(-128, -128, 1024, 1024)));
        scene.Children.Add(new SceneRectangle(new Avalonia.Media.Immutable.ImmutableSolidColorBrush(Color.FromArgb(128, 220, 40, 10)), null, new Rect(0, 0, 256, 128)));
        using var recorder = new SkiaSharp.SKPictureRecorder();
        scene.Raster(recorder.BeginRecording(new SkiaSharp.SKRect(-128, -128, 896, 896)));
        var recording = new RecordedPicture(recorder.EndRecording(), new Rect(-128, -128, 1024, 1024));
        try
        {
            foreach (float scale in new[] { .125f, .25f, .5f })
            {
                using var expected = new SkiaSharp.SKBitmap(180, 160, SkiaSharp.SKColorType.Bgra8888, SkiaSharp.SKAlphaType.Premul);
                using var actual = new SkiaSharp.SKBitmap(expected.Info);
                using (var dc = new SkiaSharp.SKCanvas(expected)) { dc.Clear(); dc.Translate(20, 10); dc.Scale(scale); dc.DrawPicture(recording.Picture); }
                using (var dc = new SkiaSharp.SKCanvas(actual)) { dc.Clear(); dc.Translate(20, 10); dc.Scale(scale); recording.Draw(dc, null); }
                Assert.Equal(expected.Bytes, actual.Bytes);
            }
        }
        finally { recording.Release(); }
    }

    [Fact]
    public void MapAndStampRoundTripsPreserveArtworkFlagsAndMetadata()
    {
        string directory = Path.Combine(Path.GetTempPath(), "crystal-roundtrip-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        try
        {
            var document = new MapDocument(17, 23);
            var cell = new Cell { BackLibrary = 2, BackImage = 0x60000021, MiddleLibrary = 4, MiddleImage = 123,
                Foreground = new Front(5, 321, 0x83, 2), Door = 1, DoorOffset = 5, MiddleFrames = 3,
                MiddleTick = 2, TileAnimation = 7, TileOffset = 8, TileFrames = 9, Light = 3 };
            document.Set(4, 6, cell);
            string path = Path.Combine(directory, "test.map"); document.Save(path);
            var loaded = MapDocument.Load(path);
            Assert.Equal((17, 23), (loaded.Width, loaded.Height)); Assert.True(cell.SameContents(loaded.Get(4, 6)), $"Expected {cell}; actual {loaded.Get(4, 6)}");
            var stamp = new[] { new StampCell(-2, 4, cell) }; path = Path.Combine(directory, "test.X"); CellCodec.SaveStamp(path, stamp);
            var actual = Assert.Single(CellCodec.LoadStamp(path)); Assert.Equal((-2, 4), (actual.X, actual.Y)); Assert.True(cell.SameContents(actual.Cell));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public void UndoRedoAndSavedStateRetainCells()
    {
        var document = new MapDocument(16, 16); var history = new EditHistory();
        history.Record(document, 2, 3); document.Set(2, 3, new Cell { MiddleImage = 15 }); history.Commit();
        Assert.True(history.Dirty); history.Saved(); Assert.False(history.Dirty);
        history.Undo(document, (_, _) => { }); Assert.Equal(0, document.Get(2, 3).MiddleImage); Assert.True(history.Dirty);
        history.Redo(document, (_, _) => { }); Assert.Equal(15, document.Get(2, 3).MiddleImage); Assert.False(history.Dirty);
    }

    [AvaloniaFact]
    public async Task WorkerRasterizationPreservesAlphaAdditiveBlendingAndCropping()
    {
        var actual = await Task.Run(() =>
        {
            var pixel = RasterImage.Create(1, 1, 96, 96, RasterFormats.Bgra32, null, [100, 50, 20, 128], 4);
            var behind = new SceneGroup();
            behind.Children.Add(new SceneRectangle(new Avalonia.Media.Immutable.ImmutableSolidColorBrush(Color.FromRgb(10, 20, 30)), null,
                new Rect(0, 0, 1, 1)));
            var additive = MapSpriteDrawing.Additive(new Sprite(pixel, 0, 0), new Rect(0, 0, 1, 1), behind);
            return Fixture.Pixels(additive);
        });
        Assert.Equal(new byte[] { 80, 45, 20, 255 }, actual);
    }

    [AvaloniaFact]
    public async Task OverviewAndMinimapExportKeepLayerOrderAndOpaquePng()
    {
        using var assets = AssetStore.Load(Path.Combine(TestApplication.Root, "Data"));
        var document = new MapDocument(4, 6);
        document.Set(0, 0, new Cell { BackImage = 1 });
        document.Set(0, 2, new Cell { Foreground = new Front(2, 1) });
        var result = await Task.Run(() => OverviewBatch.BuildAsync(document.Cells.ToArray(), 4, 6, 7, 2, assets, default));
        Assert.Equal(2, result.Sprites); Assert.Equal(2, result.Scene.Children.Count);
        var image = await Task.Run(() => MinimapExporter.CreateAsync(document, assets));
        Assert.Equal((48, 48), (image.PixelWidth, image.PixelHeight));
        var bytes = Fixture.Pixels(image); Assert.All(Enumerable.Range(0, bytes.Length / 4), i => Assert.Equal(255, bytes[i * 4 + 3]));
        Assert.Equal(new byte[] { 160, 20, 80, 255 }, bytes.Take(4));
        using var output = new MemoryStream(); image.Save(output); output.Position = 0;
        using var decoded = RasterImage.Load(output); Assert.Equal((48, 48), (decoded.PixelWidth, decoded.PixelHeight));
    }

    [AvaloniaFact]
    public void ViewportPointerPaintingSelectionZoomAndPanRemainEditable()
    {
        var viewport = new MapViewport { Tool = EditorTool.Pencil, Layer = MapLayer.Middle, Library = 1, SpriteIndex = 3 };
        viewport.SetDocument(new MapDocument(32, 32));
        var window = new EditorWindow { Width = 800, Height = 600, Content = viewport };
        window.Show(); Dispatcher.UIThread.RunJobs();
        try
        {
            window.MouseDown(new Point(70, 80), MouseButton.Left); window.MouseMove(new Point(170, 80)); window.MouseUp(new Point(170, 80), MouseButton.Left);
            Assert.Equal(4, viewport.Document.Get(0, 0).MiddleImage); Assert.Equal(4, viewport.Document.Get(2, 0).MiddleImage);
            viewport.Undo(); Assert.Equal(0, viewport.Document.Get(0, 0).MiddleImage); viewport.Redo(); Assert.Equal(4, viewport.Document.Get(2, 0).MiddleImage);
            viewport.Tool = EditorTool.Select;
            window.MouseDown(new Point(70, 80), MouseButton.Left); window.MouseMove(new Point(170, 120)); window.MouseUp(new Point(170, 120), MouseButton.Left);
            Assert.NotNull(viewport.Selection);
            viewport.SetZoom(.5); Assert.Equal(.5, viewport.Zoom);
            var before = viewport.VisibleWorldBounds;
            window.MouseDown(new Point(200, 200), MouseButton.Middle); window.MouseMove(new Point(240, 230)); window.MouseUp(new Point(240, 230), MouseButton.Middle);
            Assert.NotEqual(before, viewport.VisibleWorldBounds);
            Dispatcher.UIThread.RunJobs(); using var frame = window.CaptureRenderedFrame(); Assert.NotNull(frame);
        }
        finally { viewport.StopBackgroundWork(); window.Close(); }
    }

    [AvaloniaFact]
    public void AllEditorWindowsLoadNativeAvaloniaControls()
    {
        using var assets = AssetStore.Load(Path.Combine(TestApplication.Root, "Data"));
        var document = new MapDocument(256, 256);
        var terrain = TerrainCatalogue.Load(); var walls = WallCatalogue.Read(WallCatalogue.ResolvePath());
        var windows = new EditorWindow[]
        {
            new MainWindow(new AssetHistoryStore(Path.Combine(TestApplication.Root, "history.json")), new RecentMapStore(Path.Combine(TestApplication.Root, "recent.json"))),
            new SettingsWindow(null, _ => Task.CompletedTask),
            new WallBrushWindow(walls.Families, assets),
            new SpecialBrushWindow(assets, new TreeBrushOptions(), 3, []),
            new TownStampWindow(assets, TownCatalogue.Load()),
            new TerrainGeneratorWindow(document, assets, terrain),
            new ProvinceGeneratorWindow(document, assets, walls),
            new TempleGeneratorWindow(document, assets, walls, terrain),
        };
        foreach (var window in windows)
        {
            Assert.IsAssignableFrom<Window>(window); Assert.NotNull(window.Content);
            Assert.NotEmpty(((Control)window.Content!).GetVisualDescendants().OfType<Control>());
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task MainWindowShowsRendersAndSwitchesPaletteWithoutChangingDocument()
    {
        var window = new MainWindow(new AssetHistoryStore(Path.Combine(TestApplication.Root, "assets-ui.json")), new RecentMapStore(Path.Combine(TestApplication.Root, "recent-ui.json")));
        window.Show();
        try
        {
            for (int i = 0; i < 30; i++) { await Task.Delay(30); Dispatcher.UIThread.RunJobs(); }
            var viewport = window.FindControl<MapViewport>("Viewport")!;
            Assert.False(viewport.History.Dirty);
            var tabs = window.GetVisualDescendants().OfType<TabControl>().First(t => t.Items.Count == 4);
            tabs.SelectedIndex = 1; Dispatcher.UIThread.RunJobs(); tabs.SelectedIndex = 2; Dispatcher.UIThread.RunJobs(); tabs.SelectedIndex = 0;
            Assert.False(viewport.History.Dirty);
            using var frame = window.CaptureRenderedFrame(); Assert.NotNull(frame);
            Assert.NotEmpty(window.GetVisualDescendants().OfType<TabItem>());
        }
        finally { window.Close(); }
    }
}
