namespace CrystalStudio;

internal sealed class QuickMapDraftPanel : StackPanel
{
    internal QuickMapDraftPanel(EditorWindow window, MapCreationSession session, GeneratorPreviewState state)
    {
        if (session.DraftStore == null || session.RecipeFactory == null) return;
        Spacing = 5;
        var status = new TextBlock { Name = "QuickMapDraftStatus", TextWrapping = TextWrapping.Wrap };
        var save = new Button { Name = "QuickMapSaveDraft", Content = LocalText.T("Save creation task draft") };
        Children.Add(MapDesignEditor.Hint("Task drafts retain parameters, markers, favorites and comparison choices. They are separate from map files; resuming always starts unsaved. Decoration variants are rebuilt from their native recipe, never trusted as an old check result."));
        Children.Add(save); Children.Add(status);
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(750) };
        bool closing = false;
        void Save()
        {
            timer.Stop(); if (!closing && (session.BatchRunning || session.VariationBusy)) { timer.Start(); return; }
            try
            {
                if (!session.BatchRunning) session.RememberParameters(GeneratorPreferences.Capture(window));
                session.DraftStore.Save(QuickMapDraftStore.Capture(session, session.RecipeFactory()));
                session.DraftSaved = true;
                status.Text = LocalText.Format("Creation task draft saved at {0}. No map file was overwritten.", DateTime.Now.ToString("HH:mm:ss"));
            }
            catch (Exception exception) { status.Text = LocalText.T("Draft could not be saved; the previous draft was kept.") + "\n" + exception.Message; StudioLog.Error("QuickMap.Draft", exception); }
        }
        void Schedule() { if (closing) return; timer.Stop(); timer.Start(); }
        timer.Tick += (_, _) => Save(); save.Click += (_, _) => Save(); session.DraftChanged += Schedule; session.OutputChanged += Schedule;
        state.Completed += (_, _) => Schedule();
        window.Closing += (_, _) => { closing = true; Save(); };
        window.Closed += (_, _) => { timer.Stop(); session.DraftChanged -= Schedule; session.OutputChanged -= Schedule; };
    }
}
