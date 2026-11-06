namespace CrystalStudio;

internal sealed class ModuleConnectionsWindow : EditorWindow
{
    internal ModuleConnectionEditor Editor { get; }
    internal ListBox ConnectionsList => Editor.ConnectionsList;
    internal TextBox IdBox => Editor.IdBox;
    internal TextBox KindBox => Editor.KindBox;
    internal TextBox XBox => Editor.XBox;
    internal TextBox YBox => Editor.YBox;
    internal TextBox WidthBox => Editor.WidthBox;
    internal ComboBox EdgeBox => Editor.EdgeBox;
    internal TextBlock Feedback => Editor.Feedback;
    internal ModuleConnections? Result { get; private set; }
    internal ModuleConnectionsWindow(IReadOnlyList<StampCell> source, ModuleConnections? saved, AssetStore? assets = null)
    {
        Title = LocalText.T("Module connections"); Width = 1240; Height = 800; MinWidth = 1000; MinHeight = 640;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Editor = new(source, saved, assets);
        var root = new Grid { RowDefinitions = new RowDefinitions("*,Auto"), Margin = new Thickness(12) };
        root.Children.Add(Editor);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Margin = new Thickness(0, 12, 0, 0) };
        var apply = new Button { Content = LocalText.T("Apply"), IsDefault = true };
        apply.Click += (_, _) => { if (TryApply()) DialogResult = true; };
        actions.Children.Add(apply); actions.Children.Add(new Button { Content = LocalText.T("Cancel"), IsCancel = true });
        actions.Children.Add(new TextBlock { Text = LocalText.T("Apply connections, then save the object to use it in map generation."), VerticalAlignment = VerticalAlignment.Center });
        Grid.SetRow(actions, 1); root.Children.Add(actions); Content = root;
        Editor.Changed += () => apply.IsEnabled = Editor.State.Problem == null && !Editor.State.Adding &&
            Editor.State.Check(Editor.State.PreviewConnections) == null;
        Closed += (_, _) => Editor.Dispose();
    }
    internal bool StoreDraft() => Editor.Commit();
    internal bool TryApply() { Result = Editor.Accept(); return Result != null; }
}
