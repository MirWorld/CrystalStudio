namespace CrystalStudio;

public partial class MainWindow
{
    private readonly List<CheckBox> layerLockButtons = [];
    private bool syncingLayerLocks;

    private void InitializeLayerLocks()
    {
        string[] names = ["Ground", "Middle", "Objects", "Low Collisions", "High Collisions"];
        foreach (var (row, index) in LayerList.Items.Cast<ListBoxItem>().Select((row, index) => (row, index)))
        {
            var grid = (Grid)row.Content!;
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var button = new CheckBox { Name = "LayerLock" + index, Content = new TextBlock
                { Text = "\uE72E", FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 11 },
                MinWidth = 0, MinHeight = 0, Padding = new(0), Margin = new(4, 0, 2, 0), VerticalAlignment = VerticalAlignment.Center };
            Avalonia.Automation.AutomationProperties.SetName(button, LocalText.Format("Lock {0}", LocalText.T(names[index])));
            ToolTip.SetTip(button, LocalText.Format("Lock {0}", LocalText.T(names[index])));
            Grid.SetColumn(button, 2); grid.Children.Add(button); layerLockButtons.Add(button);
            var layer = (MapLayer)index;
            button.IsCheckedChanged += (_, _) =>
            {
                if (!syncingLayerLocks) Viewport.SetLayerLocked(layer, button.IsChecked == true);
            };
        }
    }
    private void SyncLayerLocks()
    {
        syncingLayerLocks = true;
        try { for (int i = 0; i < layerLockButtons.Count; i++) layerLockButtons[i].IsChecked = Viewport.IsLayerLocked((MapLayer)i); }
        finally { syncingLayerLocks = false; }
    }
}
