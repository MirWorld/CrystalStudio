using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;

namespace CrystalStudio.Tests;

public sealed class MapCreationStoreTests
{
    [AvaloniaFact]
    public void WizardBlocksUnavailableModesAndRestoresNamedAndLastPresets()
    {
        string root = Path.Combine(Path.GetTempPath(), "CrystalStudio-wizard-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new MapCreationStore(Path.Combine(root, "preferences.json"));
            store.Data.Last = new(MapCreationKind.Blank, 256, 256, "Last", root, false);
            store.SavePreset("Small", new(MapCreationKind.Blank, 128, 128, "Small", root, false));
            var availability = Enum.GetValues<MapCreationKind>().ToDictionary(kind => kind, kind => kind == MapCreationKind.Blank ? (string?)null : "Missing artwork");
            var window = new NewMapWindow(root, availability, store.Data.Last, store);
            window.PresetBox.SelectedIndex = 1; Assert.Equal("128", window.WidthBox.Text);
            window.PresetBox.SelectedIndex = 0; Assert.Equal("256", window.WidthBox.Text);
            window.KindBox.SelectedItem = MapCreationKind.Province; Assert.False(window.CreateButton.IsEnabled);
            window.KindBox.SelectedItem = MapCreationKind.Blank; Assert.True(window.CreateButton.IsEnabled);
            window.WidthBox.Text = "4096"; window.HeightBox.Text = "4096"; Dispatcher.UIThread.RunJobs(); Assert.False(window.CreateButton.IsEnabled);
            window.Close();
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [AvaloniaFact]
    public void StaticGeneratorChoicesSurviveALanguageChange()
    {
        var narrow = new ComboBoxItem { Content = "Narrow" };
        var wide = new ComboBoxItem { Content = "Wide" };
        var choice = new ComboBox { Name = "WidthList", Items = { narrow, wide }, SelectedIndex = 0 };
        var window = new EditorWindow { Content = choice };
        var parameters = GeneratorPreferences.Capture(window);
        narrow.Content = "窄"; wide.Content = "宽"; choice.SelectedIndex = 1;
        GeneratorPreferences.Restore(window, parameters);
        Assert.Equal(0, choice.SelectedIndex); window.Close();
    }

    [Fact]
    public void PresetsAndGeneratorParametersRoundTripUsingSourceGeneratedJson()
    {
        string root = Path.Combine(Path.GetTempPath(), "CrystalStudio-creation-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new MapCreationStore(Path.Combine(root, "preferences.json"));
            var settings = new MapCreationSettings(MapCreationKind.Province, 512, 256, "Forest", root, false);
            store.SetParameters(settings.Kind, new Dictionary<string, string> { ["SeedBox"] = "572", ["StyleList"] = "province:bichon-province" });
            store.Data.Last = settings; store.SavePreset("Outdoor", settings);
            var restored = new MapCreationStore(store.FilePath); restored.Load();
            Assert.Equal(settings, restored.Data.Last);
            Assert.Equal("572", restored.Data.Presets["Outdoor"].Parameters["SeedBox"]);
            Assert.Equal("572", restored.Parameters(settings.Kind)["SeedBox"]);
            var copy = restored.Parameters(settings.Kind); copy["SeedBox"] = "1";
            Assert.Equal("572", restored.Parameters(settings.Kind)["SeedBox"]);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData("../map")]
    [InlineData("CON")]
    [InlineData("AUX.map")]
    [InlineData("map.")]
    [InlineData(" map")]
    public void MapNamesCannotEscapeTheirOutputFolder(string name)
    {
        Assert.Throws<ArgumentException>(() => MapCreationPaths.Target(Path.GetTempPath(), name));
    }

    [Fact]
    public void SaveNewNeverOverwritesAnExistingMap()
    {
        string root = Path.Combine(Path.GetTempPath(), "CrystalStudio-new-map-" + Guid.NewGuid().ToString("N"));
        string target = Path.Combine(root, "map.map");
        try
        {
            var original = new MapDocument(16, 16); original.Set(0, 0, new Cell { MiddleImage = 7 }); original.Save(target);
            Assert.Throws<IOException>(() => new MapDocument(32, 32).SaveNew(target));
            Assert.Equal((ushort)7, MapDocument.Load(target).Get(0, 0).MiddleImage);
            Assert.Equal("map_2", MapCreationPaths.AvailableName(root, "map"));
            Assert.Empty(Directory.GetFiles(root, "*.tmp"));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [AvaloniaFact]
    public void GeneratorPreferencesRestoreOnlyValidAndAvailableChoices()
    {
        var seed = new TextBox { Name = "SeedBox", Text = "1" };
        var slider = new Slider { Name = "Density", Minimum = 0, Maximum = 100, Value = 20 };
        var choice = new ComboBox { Name = "Style", ItemsSource = new[] { "Forest", "Desert" }, SelectedIndex = 0 };
        var toggle = new CheckBox { Name = "Water", IsEnabled = false };
        var window = new EditorWindow { Content = new StackPanel { Children = { seed, slider, choice, toggle } } };
        GeneratorPreferences.Restore(window, new Dictionary<string, string>
        { ["SeedBox"] = "481", ["Density"] = "1000", ["Style"] = "Desert", ["Water"] = "True" });
        Assert.Equal("481", seed.Text); Assert.Equal(100, slider.Value); Assert.Equal("Desert", choice.SelectedItem);
        Assert.False(toggle.IsChecked == true);
        Assert.Equal("481", GeneratorPreferences.Capture(window)["SeedBox"]);
        GeneratorPreferences.Restore(window, new Dictionary<string, string> { ["Density"] = "NaN", ["Style"] = "Missing" });
        Assert.Equal(100, slider.Value); Assert.Equal("Desert", choice.SelectedItem);
        window.Close();
    }
}
