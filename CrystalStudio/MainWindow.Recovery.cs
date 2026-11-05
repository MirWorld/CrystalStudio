using System.IO;

namespace CrystalStudio;

public partial class MainWindow
{
    private MapRecoveryStore? recovery;
    private readonly DispatcherTimer recoveryTimer = new() { Interval = TimeSpan.FromMinutes(1) };
    private readonly Dictionary<Guid, long> recoveredRevisions = [];
    private bool recoveryWriting;

    private void InitializeRecovery()
    {
        try { recovery = new MapRecoveryStore(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { StudioLog.Error("Recovery.Initialize", ex); Status.Text = LocalText.T("Automatic recovery is unavailable."); }
        recoveryTimer.Tick += async (_, _) => await SaveRecoveryAsync();
        Loaded += (_, _) => recoveryTimer.Start();
        Closed += (_, _) =>
        {
            recoveryTimer.Stop();
            try { recovery?.Dispose(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { StudioLog.Error("Recovery.Close", ex); }
        };
    }

    private MapRecoveryData CaptureRecovery(OpenMapTab map)
    {
        var document = ReferenceEquals(map, activeMap) ? Viewport.Document : map.State.Document;
        var format = map.Path == null && !map.Recovered && !map.IsStamp ? settings.AssetFormat : document.SourceFormat;
        return new(map.RecoveryId, MapDisplayName(map), map.Path, DateTime.UtcNow, document.Width, document.Height,
            format, document.Cells.Select(pair => new StampCell(pair.Key % document.Width, pair.Key / document.Width, pair.Value.Copy())).ToList(),
            document.PlacedStamps.ToList(), map.IsStamp, map.StampOriginX, map.StampOriginY, map.Category, map.CategoryDirty);
    }

    internal async Task SaveRecoveryAsync()
    {
        if (busy || closed || recoveryWriting || recovery == null) return;
        recoveryWriting = true;
        try
        {
            foreach (var map in mapTabs.Items.Cast<TabItem>().Select(t => (OpenMapTab)t.Tag!).ToArray())
            {
                if (!mapTabs.Items.Cast<TabItem>().Any(tab => ReferenceEquals(tab.Tag, map))) continue;
                var history = ReferenceEquals(map, activeMap) ? Viewport.History : map.State.History;
                if (!history.Dirty && !map.CategoryDirty) { RemoveRecovery(map); continue; }
                var document = ReferenceEquals(map, activeMap) ? Viewport.Document : map.State.Document;
                long revision = document.PickupRevision;
                if (!map.CategoryDirty && recoveredRevisions.TryGetValue(map.RecoveryId, out var saved) && saved == revision) continue;
                var snapshot = CaptureRecovery(map);
                int version = recovery.Version(map.RecoveryId);
                await Task.Run(() => recovery.Write(snapshot, version));
                if (closed) return;
                recoveredRevisions[map.RecoveryId] = revision;
            }
        }
        catch (Exception ex)
        { StudioLog.Error("Recovery.Save", ex); Status.Text = LocalText.T("Could not write the automatic recovery snapshot."); }
        finally { recoveryWriting = false; }
    }

    private void RemoveRecovery(OpenMapTab map)
    {
        recoveredRevisions.Remove(map.RecoveryId);
        try { recovery?.Remove(map.RecoveryId); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { StudioLog.Error("Recovery.Remove", ex); }
    }

    private async void RecoverMapsClick(object sender, RoutedEventArgs e) => await OfferRecoveryAsync(showEmpty: true);

    private async Task OfferRecoveryAsync(bool showEmpty = false)
    {
        if (busy || closed || recovery == null) return;
        await Run(async () =>
        {
            var entries = await Task.Run(recovery.Available);
            if (closed) return;
            if (entries.Count == 0)
            {
                if (showEmpty) MessageBox.Show(this, LocalText.T("No recovery snapshots are available."));
                return;
            }
            var dialog = new EditorWindow { Title = LocalText.T("Recover Maps"), Width = 720, Height = 380, Owner = this, WindowStartupLocation = WindowStartupLocation.CenterOwner };
            var panel = new DockPanel { Margin = new(18) };
            var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Margin = new(0, 12, 0, 0) };
            DockPanel.SetDock(actions, Dock.Bottom); panel.Children.Add(actions);
            var list = new ListBox { ItemsSource = entries, SelectedIndex = 0 };
            panel.Children.Add(list); dialog.Content = panel;
            var open = new Button { Content = LocalText.T("Open Recovered Copy") };
            var dismiss = new Button { Content = LocalText.T("Delete Snapshot") };
            var close = new Button { Content = LocalText.T("Close"), IsCancel = true };
            actions.Children.Add(open); actions.Children.Add(dismiss); actions.Children.Add(close);
            open.Click += async (_, _) =>
            {
                if (list.SelectedItem is not MapRecoveryEntry entry) return;
                open.IsEnabled = dismiss.IsEnabled = false;
                try
                {
                    var document = await Task.Run(entry.Data.Restore);
                    if (closed || !dialog.IsVisible) return;
                    AddMapTab(document);
                    var map = activeMap!;
                    map.Recovered = true;
                    map.Name = LocalText.Format("Recovered — {0}", entry.Data.Name);
                    map.IsStamp = entry.Data.IsStamp; map.StampOriginX = entry.Data.OriginX; map.StampOriginY = entry.Data.OriginY;
                    map.Category = entry.Data.Category; map.CategoryDirty = entry.Data.CategoryDirty;
                    // A recovered copy must require saving, even if no further edits are made.
                    Viewport.History.MarkRecovered();
                    stampProperties.IsVisible = map.IsStamp;
                    var snapshot = CaptureRecovery(map);
                    int version = recovery.Version(map.RecoveryId);
                    await Task.Run(() => recovery.Write(snapshot, version));
                    MapRecoveryStore.Dismiss(entry);
                    entries.Remove(entry); list.ItemsSource = entries.ToArray();
                    UpdateLabels(); UpdateMapTabLabels();
                    if (entries.Count == 0) dialog.DialogResult = true;
                }
                catch (Exception ex) { StudioLog.Error("Recovery.Restore", ex); MessageBox.Show(dialog, ex.Message); }
                finally { open.IsEnabled = dismiss.IsEnabled = true; }
            };
            dismiss.Click += (_, _) =>
            {
                if (list.SelectedItem is not MapRecoveryEntry entry) return;
                if (MessageBox.Show(dialog, LocalText.T("Delete this recovery snapshot?"), LocalText.T("Recover Maps"), MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
                try { MapRecoveryStore.Dismiss(entry); entries.Remove(entry); list.ItemsSource = entries.ToArray(); }
                catch (Exception ex) { MessageBox.Show(dialog, ex.Message); }
            };
            dialog.ShowDialog();
        });
    }
}
