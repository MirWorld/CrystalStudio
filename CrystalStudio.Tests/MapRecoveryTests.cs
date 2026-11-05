using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace CrystalStudio.Tests;

public sealed class MapRecoveryTests
{
    [AvaloniaFact]
    public async Task RecoveredMapsOpenAsDirtyCopiesAndKeepTheirSourceFormat()
    {
        string root = Path.Combine(TestApplication.Root, "recover-ui-" + Guid.NewGuid().ToString("N"));
        Fixture.CreateLibraries(Path.Combine(root, "Data")); Directory.CreateDirectory(Path.Combine(root, "Map"));
        string target = Path.Combine(root, "Map", "original.map");
        var original = new MapDocument(16, 16); original.Set(0, 0, new Cell { MiddleImage = 7 }); original.Save(target);
        string profile = Path.Combine(root, "profile");
        using var abandoned = new MapRecoveryStore(Path.Combine(profile, "recovery"));
        var snapshot = MapRecoveryStore.Capture(original); snapshot.SourceFormat = AssetSourceFormat.Sanxiu;
        snapshot.Set(0, 0, new Cell { MiddleLibrary = 1001, MiddleImage = 8 });
        abandoned.Write(Guid.NewGuid(), snapshot, target, "original.map", null); abandoned.Dispose();
        var settings = new SettingsStore(Path.Combine(profile, "settings.json")); settings.Save(new(root, "en-US"));
        var window = new MainWindow(new AssetHistoryStore(Path.Combine(profile, "assets.json")), new RecentMapStore(Path.Combine(profile, "recent.json")), settings);
        window.Show();
        try
        {
            for (int attempt = 0; attempt < 500 && (window.FindControl<MapViewport>("Viewport")!.Assets == null || !window.FindControl<MenuItem>("NewButton")!.IsEnabled); attempt++)
            { await Task.Delay(20); Dispatcher.UIThread.RunJobs(); }
            using var dialogs = Window.WindowOpenedEvent.AddClassHandler<Window>((dialog, _) =>
            {
                if (dialog.Title != LocalText.T("Recover Unsaved Maps")) return;
                Dispatcher.UIThread.Post(() => dialog.GetVisualDescendants().OfType<Button>()
                    .Single(button => Equals(button.Content, LocalText.T("Recover selected map")))
                    .RaiseEvent(new RoutedEventArgs(Button.ClickEvent)));
            });
            typeof(MainWindow).GetMethod("RecoverMapsClick", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                .Invoke(window, [window, new RoutedEventArgs()]);
            var viewport = window.FindControl<MapViewport>("Viewport")!;
            for (int attempt = 0; attempt < 500 && !viewport.History.Dirty; attempt++)
            { await Task.Delay(20); Dispatcher.UIThread.RunJobs(); }
            Assert.True(viewport.History.Dirty); Assert.Equal(AssetSourceFormat.Sanxiu, viewport.Document.SourceFormat);
            Assert.Equal((ushort)8, viewport.Document.Get(0, 0).MiddleImage);
            Assert.Equal((ushort)7, MapDocument.Load(target).Get(0, 0).MiddleImage);
            await window.SaveRecoverySnapshotsAsync();
            using var reader = new MapRecoveryStore(Path.Combine(profile, "recovery"));
            Assert.Empty(reader.List());
        }
        finally { window.FindControl<MapViewport>("Viewport")!.History.Saved(); window.Close(); }
    }

    [Fact]
    public void RecoveryStoreIsAvailable()
    {
        Assert.NotNull(typeof(MapDocument).Assembly.GetType("CrystalStudio.MapRecoveryStore"));
    }

    [Fact]
    public void SnapshotsRemainIndependentAndActiveSessionsAreNotRecovered()
    {
        string root = Path.Combine(Path.GetTempPath(), "CrystalStudio-recovery-" + Guid.NewGuid().ToString("N"));
        try
        {
            string target = Path.Combine(root, "original.map");
            var original = new MapDocument(16, 16) { SourceFormat = AssetSourceFormat.Sanxiu };
            original.Set(0, 0, new Cell { MiddleLibrary = 1001, MiddleImage = 7 }); original.Save(target);
            using var first = new MapRecoveryStore(Path.Combine(root, "snapshots"));
            using var second = new MapRecoveryStore(Path.Combine(root, "snapshots"));
            Guid id = Guid.NewGuid();
            var snapshot = MapRecoveryStore.Capture(original); snapshot.Set(0, 0, new Cell { MiddleLibrary = 1001, MiddleImage = 8 });
            first.Write(id, snapshot, target, "Edited", null);
            Assert.Empty(second.List());
            first.Dispose();
            var entry = Assert.Single(second.List());
            Assert.Equal(target, entry.OriginalPath);
            var restored = second.Load(entry);
            Assert.Equal(AssetSourceFormat.Sanxiu, restored.SourceFormat);
            Assert.Equal((ushort)8, restored.Get(0, 0).MiddleImage);
            Assert.Equal((ushort)7, MapDocument.Load(target, AssetSourceFormat.Sanxiu).Get(0, 0).MiddleImage);
            second.Delete(entry); Assert.Empty(second.List());
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public void ReplacementKeepsOneCommittedSnapshotAndSkipsCorruptEntries()
    {
        string root = Path.Combine(Path.GetTempPath(), "CrystalStudio-recovery-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var writer = new MapRecoveryStore(root);
            var document = new MapDocument(16, 16); Guid id = Guid.NewGuid();
            writer.Write(id, document, null, "First", null);
            document.Set(1, 2, new Cell { MiddleImage = 9 }); writer.Write(id, document, null, "Latest", null);
            string folder = Path.Combine(root, writer.SessionId.ToString("N"));
            File.WriteAllText(Path.Combine(folder, Guid.NewGuid().ToString("N") + ".json"), "not json");
            Assert.Single(Directory.GetFiles(folder, "*.map")); writer.Dispose();
            using var reader = new MapRecoveryStore(root);
            var entry = Assert.Single(reader.List()); Assert.Equal("Latest", entry.DisplayName);
            Assert.Equal((ushort)9, reader.Load(entry).Get(1, 2).MiddleImage);
            Assert.Throws<InvalidDataException>(() => reader.Load(entry with { SnapshotFile = "../original.map" }));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public void CaptureDoesNotChangeWhenTheOriginalIsEditedAgain()
    {
        var original = new MapDocument(16, 16); original.Set(0, 0, new Cell { MiddleImage = 7 });
        var snapshot = MapRecoveryStore.Capture(original);
        original.Get(0, 0).MiddleImage = 8;
        Assert.Equal((ushort)7, snapshot.Get(0, 0).MiddleImage);
    }
}
