using Avalonia.LogicalTree;

namespace CrystalStudio;

internal sealed class GeneratorWorkflow
{
    private readonly StackPanel panel = new() { Name = "GeneratorWorkflow", Spacing = 6, Margin = new Thickness(0, 8) };
    private readonly Expander diagnostics;
    private readonly GeneratorPreviewState state;
    private readonly TextBlock impactText = new() { Name = "GeneratorImpactText", TextWrapping = TextWrapping.Wrap };
    private readonly Image impactImage = new() { Name = "GeneratorImpactMap", Height = 110, Stretch = Stretch.Uniform };
    private readonly Expander impact;
    private RasterImage? impactBitmap;
    private GenerationImpact? displayedImpact;
    private bool quickCreate;

    internal GeneratorWorkflow(EditorWindow window, GeneratorPreviewState state, Action cancel, Action retry, Func<bool> canCancel)
    {
        this.state = state;
        var settings = window.GetLogicalDescendants().OfType<ScrollViewer>().Select(view => view.Content).OfType<StackPanel>().First(panel => panel.Children.OfType<ComboBox>().Any());
        var status = new TextBlock { Name = "GeneratorStage", TextWrapping = TextWrapping.Wrap };
        Avalonia.Automation.AutomationProperties.SetLiveSetting(status, Avalonia.Automation.AutomationLiveSetting.Polite);
        var progress = new ProgressBar { Name = "GeneratorProgress", IsIndeterminate = true, Height = 4 };
        var stop = new Button { Name = "GeneratorStop", Content = LocalText.T("Stop generation") };
        var recheck = new Button { Name = "GeneratorRecheck", Content = LocalText.T("Recheck and regenerate") };
        var actions = new WrapPanel(); actions.Children.Add(stop); actions.Children.Add(recheck);
        stop.Margin = new Thickness(0, 0, 6, 0); stop.Click += (_, _) => cancel(); recheck.Click += (_, _) => retry();
        diagnostics = new Expander { Name = "GeneratorDiagnostics", Header = LocalText.T("Artwork details and recovery"), Content = new ResourceDiagnosticsView(state, "Generator"), HorizontalAlignment = HorizontalAlignment.Stretch };
        var impactContent = new StackPanel { Spacing = 6 }; impactContent.Children.Add(impactText); impactContent.Children.Add(impactImage);
        impactContent.Children.Add(new TextBlock { Text = LocalText.T("Orange marks affected preview bins, not individual pixels or stamp metadata. This is read-only; applying still uses the original rules and one undo step."), TextWrapping = TextWrapping.Wrap, FontSize = 11 });
        impact = new Expander { Name = "GeneratorImpact", Header = LocalText.T("Application impact"), Content = impactContent, HorizontalAlignment = HorizontalAlignment.Stretch };
        panel.Children.Add(status); panel.Children.Add(progress); panel.Children.Add(actions); panel.Children.Add(impact); panel.Children.Add(diagnostics);
        settings.Children.Insert(0, panel);
        void Refresh()
        {
            bool busy = state.Phase is GeneratorPreviewPhase.Checking or GeneratorPreviewPhase.Generating or GeneratorPreviewPhase.Cancelling;
            status.Text = state.Phase switch
            {
                GeneratorPreviewPhase.Waiting => LocalText.T("Parameters changed. Waiting to rebuild; the old preview cannot be applied."),
                GeneratorPreviewPhase.Cancelling => LocalText.T("Stopping… Please wait for cancellation to finish."),
                GeneratorPreviewPhase.Cancelled => LocalText.T("Generation cancelled. The map was not changed."),
                GeneratorPreviewPhase.Ready => LocalText.T("Preview ready"),
                GeneratorPreviewPhase.Failed => LocalText.T("Preview failed. Review artwork details or change the style."),
                _ => LocalText.T(state.Stage)
            };
            progress.IsVisible = busy; stop.IsEnabled = state.Phase != GeneratorPreviewPhase.Cancelling && canCancel();
            stop.IsVisible = busy || state.Phase == GeneratorPreviewPhase.Waiting;
            status.IsVisible = !quickCreate;
            recheck.IsEnabled = !busy;
            impact.IsEnabled = state.Phase == GeneratorPreviewPhase.Ready && state.Impact != null;
            if (!ReferenceEquals(displayedImpact, state.Impact))
            {
                displayedImpact = state.Impact; impactImage.Source = null; impactBitmap?.Dispose(); impactBitmap = null;
                if (displayedImpact != null) impactImage.Source = impactBitmap = displayedImpact.CreateMap();
            }
            impactText.Text = (quickCreate ? LocalText.T("Creates a new map. The original map is not modified.") : LocalText.T("Applies to the current map using one undo step."))
                + "\n" + (state.Impact?.Description ?? LocalText.T("Generate a preview before applying."));
            impactImage.Opacity = state.Phase == GeneratorPreviewPhase.Ready ? 1 : .4;
        }
        state.Changed += (_, _) => Refresh();
        window.Closed += (_, _) => { impactImage.Source = null; impactBitmap?.Dispose(); };
        Refresh();
    }

    internal void UseQuickCreate()
    {
        quickCreate = true; diagnostics.IsVisible = false; state.Notify();
    }
}
