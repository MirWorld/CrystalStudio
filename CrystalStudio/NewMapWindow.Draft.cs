using Avalonia.LogicalTree;

namespace CrystalStudio;

internal sealed partial class NewMapWindow
{
    internal void OfferDraft(Func<bool> restore, MapCreationSession session)
    {
        var panel = this.GetLogicalDescendants().OfType<ScrollViewer>().Select(view => view.Content).OfType<StackPanel>().First();
        var button = new Button { Name = "QuickMapResumeDraft", Content = LocalText.T("Resume saved creation task"), Margin = new Thickness(0, 6) };
        panel.Children.Insert(2, button);
        button.Click += (_, _) =>
        {
            try
            {
                if (!restore()) return;
                applyingSelection = true;
                try
                {
                    PresetBox.SelectedIndex = -1; TemplateBox.SelectedIndex = -1; KindBox.SelectedItem = session.Settings.Kind;
                    SetDimensions(session.Settings.Width, session.Settings.Height); NameBox.Text = session.Settings.Name;
                    FolderBox.Text = session.Settings.Directory ?? ""; SaveToggle.IsChecked = false; PresetNameBox.Text = session.PresetName;
                    SelectedParameters = new(session.Parameters); draftParameters[currentKind] = new(session.Parameters);
                }
                finally { applyingSelection = false; }
                templateHint.Text = LocalText.T("Creation task restored. Review settings; the result will be a new unsaved map."); Validate();
            }
            catch (Exception exception) { feedback.Text = exception.Message; }
        };
    }
}
