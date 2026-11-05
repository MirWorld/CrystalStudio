namespace CrystalStudio;

public partial class MainWindow
{
    private ContextMenu CreateMapTabMenu(TabItem tab)
    {
        var menu = new ContextMenu();
        foreach (string action in new[] { "Close All", "Close Tabs to Right", "Close Tabs to Left", "Close Other Tabs" })
        {
            var item = new MenuItem { Header = LocalText.T(action) };
            item.Click += (_, _) => CloseMapTabs(tab, action);
            menu.Items.Add(item);
        }
        menu.Opening += (_, _) =>
        {
            int index = mapTabs.Items.IndexOf(tab);
            var items = menu.Items.Cast<MenuItem>().ToArray();
            items[0].IsEnabled = !busy;
            items[1].IsEnabled = !busy && index < mapTabs.Items.Count - 1;
            items[2].IsEnabled = !busy && index > 0;
            items[3].IsEnabled = !busy && mapTabs.Items.Count > 1;
        };
        return menu;
    }

    private void CloseMapTabs(TabItem anchor, string action)
    {
        if (busy) return;
        var tabs = mapTabs.Items.Cast<TabItem>().ToArray();
        int index = Array.IndexOf(tabs, anchor);
        if (index < 0) return;
        var selected = mapTabs.SelectedItem;
        var targets = action switch
        {
            "Close Tabs to Right" => tabs.Skip(index + 1),
            "Close Tabs to Left" => tabs.Take(index),
            "Close Other Tabs" => tabs.Where(tab => tab != anchor),
            _ => tabs.AsEnumerable()
        };
        foreach (var tab in targets)
            if (!CloseMapTab(tab)) return;
        if (selected != null && mapTabs.Items.Contains(selected)) mapTabs.SelectedItem = selected;
        else if (mapTabs.Items.Contains(anchor)) mapTabs.SelectedItem = anchor;
    }

    private TabItem? draggedMapTab;
    private Point mapTabDragStart;
    private bool mapTabDragging;

    private void InitializeMapTabDragging()
    {
        mapTabs.AddHandler(PointerPressedEvent, (_, e) =>
        {
            if (busy || !e.GetCurrentPoint(mapTabs).Properties.IsLeftButtonPressed || e.Source is not Visual source) return;
            var ancestors = source.GetVisualAncestors().Prepend(source).ToArray();
            if (ancestors.OfType<Button>().Any()) return;
            draggedMapTab = ancestors.OfType<TabItem>().FirstOrDefault(tab => mapTabs.Items.Contains(tab));
            mapTabDragStart = e.GetPosition(mapTabs);
            mapTabDragging = false;
        }, RoutingStrategies.Tunnel);
        mapTabs.PointerMoved += (_, e) =>
        {
            if (busy || draggedMapTab == null || !e.GetCurrentPoint(mapTabs).Properties.IsLeftButtonPressed) return;
            var point = e.GetPosition(mapTabs);
            if (!mapTabDragging && Math.Abs(point.X - mapTabDragStart.X) < 6) return;
            mapTabDragging = true;
            e.Pointer.Capture(mapTabs);
            var others = mapTabs.Items.Cast<TabItem>().Where(tab => tab != draggedMapTab).ToArray();
            int index = others.Count(tab => tab.TranslatePoint(new Point(tab.Bounds.Width / 2, 0), mapTabs) is Point centre && point.X > centre.X);
            MoveMapTab(draggedMapTab, index);
            e.Handled = true;
        };
        mapTabs.AddHandler(PointerReleasedEvent, (_, e) =>
        {
            bool wasDragging = mapTabDragging;
            draggedMapTab = null; mapTabDragging = false;
            if (wasDragging) { e.Pointer.Capture(null); e.Handled = true; }
        }, RoutingStrategies.Tunnel);
        mapTabs.PointerCaptureLost += (_, e) =>
        {
            if (e.Source != mapTabs) return;
            draggedMapTab = null; mapTabDragging = false;
        };
    }

    private void MoveMapTab(TabItem tab, int index)
    {
        int oldIndex = mapTabs.Items.IndexOf(tab);
        if (oldIndex < 0 || oldIndex == index) return;
        var selected = mapTabs.SelectedItem;
        switchingMap = true;
        try
        {
            mapTabs.Items.RemoveAt(oldIndex);
            mapTabs.Items.Insert(index, tab);
            mapTabs.SelectedItem = selected;
        }
        finally { switchingMap = false; }
    }
}
