using System.Collections;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;

namespace CrystalStudio.Tests;

public sealed class AssetSelectionTests
{
    [AvaloniaFact]
    public void SelectingSpriteFromAnotherToolPreservesListAndSelection()
    {
        var window = new MainWindow(new AssetHistoryStore(Path.Combine(TestApplication.Root, "selection-assets.json")),
            new RecentMapStore(Path.Combine(TestApplication.Root, "selection-maps.json")));
        try
        {
            var viewport = window.FindControl<MapViewport>("Viewport")!;
            var list = window.FindControl<ListBox>("AssetList")!;
            var choices = (IList)list.ItemsSource!;
            choices.Clear();
            for (int i = 0; i < 3; i++) choices.Add(new AssetChoice { Index = i, Label = i.ToString() });
            var original = choices.Cast<object>().ToArray();

            foreach (int index in new[] { 2, 1, 0, 2 })
            {
                viewport.Tool = EditorTool.Select;
                list.SelectedIndex = index;
                Assert.Equal(EditorTool.Pencil, viewport.Tool);
                Assert.Equal(index, viewport.SpriteIndex);
                Assert.Equal(original, choices.Cast<object>().ToArray());
                Assert.Same(original[index], list.SelectedItem);
                Assert.False(window.FindControl<Slider>("BrushSize")!.IsEnabled);
            }
        }
        finally { window.Close(); }
    }
}
