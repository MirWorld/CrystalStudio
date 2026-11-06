using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace CrystalStudio.Tests;

public sealed class EditingToolEntryTests
{
    [AvaloniaFact]
    public void EyedropperSamplesWholeGroundTileAndMiddleWithoutPainting()
    {
        var document = new MapDocument(8, 8);
        document.Set(0, 0, new Cell { BackLibrary = 7, BackImage = 6 });
        document.Set(1, 1, new Cell { MiddleLibrary = 8, MiddleImage = 10 });
        var viewport = new MapViewport { Tool = EditorTool.Eyedropper, Layer = MapLayer.Ground };
        viewport.SetDocument(document);
        var picked = new List<(int Library, int Image, MapLayer Layer)>();
        viewport.AssetPicked += (library, image, layer) => picked.Add((library, image, layer));
        var window = new EditorWindow { Width = 640, Height = 480, Content = viewport }; window.Show();
        try
        {
            window.MouseDown(new Point(132, 118), MouseButton.Left);
            window.MouseMove(new Point(180, 150)); window.MouseUp(new Point(180, 150), MouseButton.Left);
            Assert.Equal((7, 5, MapLayer.Ground), Assert.Single(picked));
            viewport.Layer = MapLayer.Middle;
            window.MouseDown(new Point(132, 118), MouseButton.Left); window.MouseUp(new Point(132, 118), MouseButton.Left);
            Assert.Equal((8, 9, MapLayer.Middle), picked[1]);
            Assert.Equal(2, document.Cells.Count());
            Assert.False(viewport.History.Dirty); Assert.False(viewport.History.CanUndo);
        }
        finally { viewport.StopBackgroundWork(); window.Close(); }
    }

    [AvaloniaFact]
    public void HandPansWithLeftDragEvenWhenLayersAreLockedAndDoesNotPaint()
    {
        var viewport = new MapViewport { Tool = EditorTool.Pan, SpriteIndex = 5 };
        viewport.SetDocument(new(16, 16)); viewport.SetLayerLocked(MapLayer.Ground, true);
        var window = new EditorWindow { Width = 640, Height = 480, Content = viewport }; window.Show();
        try
        {
            var before = viewport.VisibleWorldBounds;
            window.MouseDown(new Point(200, 180), MouseButton.Left); window.MouseMove(new Point(260, 220));
            window.MouseUp(new Point(260, 220), MouseButton.Left);
            Assert.Equal(before.X - 60, viewport.VisibleWorldBounds.X);
            Assert.Equal(before.Y - 40, viewport.VisibleWorldBounds.Y);
            Assert.Empty(viewport.Document.Cells); Assert.False(viewport.History.Dirty);
            viewport.Tool = EditorTool.Pencil; viewport.SetLayerLocked(MapLayer.Ground, false);
            window.MouseDown(new Point(200, 180), MouseButton.Left); window.MouseUp(new Point(200, 180), MouseButton.Left);
            Assert.NotEmpty(viewport.Document.Cells); Assert.True(viewport.History.Dirty);
        }
        finally { viewport.StopBackgroundWork(); window.Close(); }
    }

    [AvaloniaFact]
    public void PasteTakesPriorityOverHandAndEyedropperLeftClicks()
    {
        var viewport = new MapViewport { Layer = MapLayer.Middle };
        var window = new EditorWindow { Width = 640, Height = 480, Content = viewport }; window.Show();
        try
        {
            foreach (var tool in new[] { EditorTool.Pan, EditorTool.Eyedropper })
            {
                var document = new MapDocument(8, 8); document.Set(0, 0, new Cell { MiddleImage = 3 });
                viewport.RestoreEditorState(new(document, new(), 1, new Point(60, 70), new Rect(0, 0, 1, 1)));
                viewport.CopySelection(); viewport.Tool = tool; viewport.BeginPaste();
                window.MouseDown(new Point(180, 182), MouseButton.Left); window.MouseUp(new Point(180, 182), MouseButton.Left);
                Assert.False(viewport.PastingRegion); Assert.Equal(3, document.Get(2, 2).MiddleImage);
                viewport.Undo(); Assert.Equal(0, document.Get(2, 2).MiddleImage);
            }
        }
        finally { viewport.StopBackgroundWork(); window.Close(); }
    }

    [AvaloniaFact]
    public void EditMenuUsesExistingClipboardAndToolbarToolsRespectLayerSupport()
    {
        var window = new MainWindow(); window.Show();
        try
        {
            var viewport = window.FindControl<MapViewport>("Viewport")!;
            var copy = window.FindControl<MenuItem>("CopyRegionMenuButton")!;
            var cut = window.FindControl<MenuItem>("CutRegionMenuButton")!;
            var paste = window.FindControl<MenuItem>("PasteRegionMenuButton")!;
            var picker = window.FindControl<RadioButton>("EyedropperToolButton")!;
            var hand = window.FindControl<RadioButton>("PanToolButton")!;
            Assert.False(copy.IsEnabled); Assert.False(cut.IsEnabled); Assert.False(paste.IsEnabled);
            var document = new MapDocument(8, 8); document.Set(2, 2, new Cell { MiddleImage = 3 });
            viewport.RestoreEditorState(new(document, new(), 1, default, new Rect(2, 2, 2, 2)));
            Assert.True(copy.IsEnabled); Assert.True(cut.IsEnabled);
            copy.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Assert.True(paste.IsEnabled); Assert.False(viewport.History.Dirty);
            cut.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Assert.Equal(0, document.Get(2, 2).MiddleImage); Assert.True(viewport.History.Dirty);
            viewport.Undo(); Assert.Equal(3, document.Get(2, 2).MiddleImage);
            paste.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent)); Assert.True(viewport.PastingRegion);
            viewport.CancelPaste();
            viewport.SetLayerLocked(MapLayer.Objects, true); Assert.False(paste.IsEnabled);
            viewport.SetLayerLocked(MapLayer.Objects, false);
            picker.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Assert.Equal(EditorTool.Eyedropper, viewport.Tool);
            window.FindControl<ListBox>("LayerList")!.SelectedIndex = (int)MapLayer.Objects;
            Assert.False(picker.IsEnabled); Assert.NotEqual(EditorTool.Eyedropper, viewport.Tool);
            hand.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Assert.Equal(EditorTool.Pan, viewport.Tool);
            viewport.History.Saved();
        }
        finally { window.Close(); }
    }
}
