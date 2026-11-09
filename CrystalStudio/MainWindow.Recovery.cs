using System.IO;

namespace CrystalStudio;

public partial class MainWindow
{
    private MapRecoveryStore recoveryStore = null!;
    private readonly DispatcherTimer recoveryTimer = new() { Interval = TimeSpan.FromSeconds(60) };
    private bool recoveryRunning;
    private readonly MenuItem recoveryMenu = new() { Header = LocalText.T("Recover Unsaved Maps…"), Name = "RecoverUnsavedMaps" };

    private void InitializeRecovery(MenuItem? fileMenu)
    {
        recoveryStore = new(Path.Combine(Path.GetDirectoryName(settingsStore.FilePath)!, "recovery"));
        recoveryMenu.Click += RecoverMapsClick; fileMenu?.Items.Insert(2, recoveryMenu);
        recoveryTimer.Tick += async (_, _) => await SaveRecoverySnapshotsAsync();
        Closed += (_, _) =>
        {
            recoveryTimer.Stop();
            foreach (var map in mapTabs.Items.Cast<TabItem>().Select(tab => (OpenMapTab)tab.Tag!)) ForgetRecovery(map);
            recoveryStore.ClearSession();
        };
    }

    private void StartRecovery()
    {
        if (closed) return;
        recoveryTimer.Start();
        try
        {
            int count = recoveryStore.List().Count;
            if (count > 0) Status.Text = LocalText.Format("{0} recovery snapshot(s) available. Use File → Recover Unsaved Maps.", count);
        }
        catch (Exception exception) { StudioLog.Error("Recovery.Startup", exception); }
    }

    internal async Task SaveRecoverySnapshotsAsync()
    {
        if (closed || busy || recoveryRunning) return;
        recoveryRunning = true;
        OpenMapTab? snapshotTarget = null;
        try
        {
            foreach (var map in mapTabs.Items.Cast<TabItem>().Select(tab => (OpenMapTab)tab.Tag!).ToArray())
            {
                if (!mapTabs.Items.Cast<TabItem>().Any(tab => ReferenceEquals(tab.Tag, map))) continue;
                var document = ReferenceEquals(map, activeMap) ? Viewport.Document : map.State.Document;
                var history = ReferenceEquals(map, activeMap) ? Viewport.History : map.State.History;
                if (!history.Dirty && !map.CategoryDirty)
                {
                    if (map.RecoveryRevision >= 0) ForgetRecovery(map);
                    continue;
                }
                if (map.RecoveryRevision == document.PickupRevision && !map.CategoryDirty) continue;
                long revision = document.PickupRevision; int version = map.RecoveryVersion;
                snapshotTarget = map;
                var snapshot = MapRecoveryStore.Capture(document);
                if (map.Path == null && !map.KeepSourceFormat && !map.IsStamp) snapshot.SourceFormat = settings.AssetFormat;
                string? path = map.Path; string name = MapDisplayName(map); var recipe = map.Recipe;
                var metadata = new MapRecoveryEntry(1, recoveryStore.SessionId, map.RecoveryId, "", path, name, DateTime.UtcNow,
                    snapshot.SourceFormat, recipe, map.IsStamp, map.StampOriginX, map.StampOriginY, map.Category, map.CategoryDirty, Design: map.Design.Copy());
                int storeVersion = recoveryStore.Version(map.RecoveryId);
                await Task.Run(() => recoveryStore.Write(map.RecoveryId, snapshot, path, name, recipe, metadata, storeVersion));
                if (closed || map.RecoveryVersion != version)
                { recoveryStore.Delete(map.RecoveryId); if (closed) break; continue; }
                map.RecoveryRevision = revision;
                map.LastRecoveryAt = DateTime.Now;
                map.RecoveryFailed = false;
                UpdateEditingContext();
                if (map.RecoveryOrigin is { } origin) { recoveryStore.Delete(origin); map.RecoveryOrigin = null; }
                if (closed) break;
            }
        }
        catch (Exception exception)
        {
            StudioLog.Error("Recovery.Snapshot", exception);
            if (snapshotTarget != null) snapshotTarget.RecoveryFailed = true;
            if (!closed) UpdateEditingContext();
            if (!closed) Status.Text = LocalText.T("Recovery snapshot failed. Save your map manually.");
        }
        finally { recoveryRunning = false; }
    }

    private void ForgetRecovery(OpenMapTab map)
    {
        map.RecoveryVersion++; map.RecoveryRevision = -1;
        try
        {
            recoveryStore.Delete(map.RecoveryId);
            if (map.RecoveryOrigin is { } origin) recoveryStore.Delete(origin);
            map.RecoveryOrigin = null;
        }
        catch (Exception exception) { StudioLog.Error("Recovery.Forget", exception); }
    }

    private async void RecoverMapsClick(object? sender, RoutedEventArgs args)
    {
        if (busy) return;
        IReadOnlyList<MapRecoveryEntry> entries;
        try { entries = recoveryStore.List(); }
        catch (Exception exception) { StudioLog.Error("Recovery.List", exception); Status.Text = LocalText.T("Could not read recovery snapshots."); return; }
        if (entries.Count == 0) { Status.Text = LocalText.T("No recovery snapshots are available."); return; }
        var dialog = new EditorWindow { Title = LocalText.T("Recover Unsaved Maps"), Width = 640, Height = 430,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, Owner = this };
        var panel = new Grid { Margin = new Thickness(20), RowDefinitions = new RowDefinitions("Auto,*,Auto") };
        panel.Children.Add(new TextBlock { Text = LocalText.T("Recovery opens an unsaved copy. Original map files are never overwritten."), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12) });
        var list = new ListBox { ItemsSource = entries, SelectedIndex = 0, ItemTemplate = new FuncDataTemplate<MapRecoveryEntry>((entry, _) =>
            new TextBlock { Text = entry == null ? "" : $"{entry.DisplayName} · {entry.SavedAtUtc.ToLocalTime():g}\n{entry.OriginalPath}", Margin = new Thickness(6), TextWrapping = TextWrapping.Wrap }) };
        Grid.SetRow(list, 1); panel.Children.Add(list);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8, Margin = new Thickness(0, 12, 0, 0) };
        var recover = new Button { Content = LocalText.T("Recover selected map"), IsDefault = true };
        recover.Click += (_, _) => { if (list.SelectedItem != null) dialog.DialogResult = true; };
        var discard = new Button { Content = LocalText.T("Discard snapshot") };
        discard.Click += (_, _) =>
        {
            if (list.SelectedItem is not MapRecoveryEntry selected || MessageBox.Show(dialog, "Permanently discard this recovery snapshot?", "Discard snapshot", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            try { recoveryStore.Delete(selected); list.ItemsSource = recoveryStore.List(); list.SelectedIndex = 0; }
            catch (Exception exception) { StudioLog.Error("Recovery.Discard", exception); }
        };
        actions.Children.Add(discard); actions.Children.Add(new Button { Content = LocalText.T("Cancel"), IsCancel = true }); actions.Children.Add(recover);
        Grid.SetRow(actions, 2); panel.Children.Add(actions); dialog.Content = panel;
        if (dialog.ShowDialog() != true || list.SelectedItem is not MapRecoveryEntry entry) return;
        if (mapTabs.Items.Cast<TabItem>().Any(tab => ((OpenMapTab)tab.Tag!).RecoveryOrigin is { } existing && existing.SessionId == entry.SessionId && existing.Id == entry.Id)) return;
        await Run(async () =>
        {
            var document = await Task.Run(() => recoveryStore.Load(entry));
            if (closed) return;
            AddMapTab(document); activeMap!.Name = Path.GetFileNameWithoutExtension(entry.DisplayName) + LocalText.T(" (recovered)");
            activeMap.KeepSourceFormat = true; activeMap.Recipe = entry.Recipe; activeMap.RecoveryOrigin = entry;
            activeMap.Design = entry.Design?.Copy() ?? entry.Recipe?.Design?.Copy() ?? new();
            activeMap.IsStamp = entry.IsStamp; activeMap.StampOriginX = entry.OriginX; activeMap.StampOriginY = entry.OriginY;
            activeMap.Category = entry.Category; activeMap.CategoryDirty = entry.CategoryDirty;
            stampProperties.IsVisible = entry.IsStamp;
            Viewport.History.MarkUnsaved(); UpdateLabels(); UpdateMapTabLabels();
            Status.Text = LocalText.T("Recovered an unsaved copy. Use Save As to choose its destination.");
        });
    }
}
