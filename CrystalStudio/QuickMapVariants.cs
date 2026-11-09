using Avalonia.LogicalTree;

namespace CrystalStudio;

internal sealed class QuickMapVariants : StackPanel
{
    private readonly EditorWindow window;
    private readonly MapCreationSession session;
    private readonly GeneratorPreviewState state;
    private readonly Button apply;
    private readonly Image image;
    private readonly AssetStore assets;
    private readonly Button freeze = new() { Name = "QuickMapLockStructure", Content = LocalText.T("Lock layout and collision") };
    private readonly Button generate = new() { Name = "QuickMapVaryDecoration", Content = LocalText.T("Generate decoration variant"), IsEnabled = false };
    private readonly Button stop = new() { Name = "QuickMapStopVariation", Content = LocalText.T("Stop operation"), IsEnabled = false };
    private readonly NumericUpDown seed = MapDesignEditor.Number("QuickMapDecorationSeed", int.MinValue, int.MaxValue, 1);
    private readonly CheckBox before = new() { Name = "QuickMapVariantBefore", Content = LocalText.T("Compare: show locked baseline"), IsEnabled = false };
    private readonly TextBlock feedback = new() { Name = "QuickMapVariationFeedback", TextWrapping = TextWrapping.Wrap };
    private (Control Control, bool Enabled)[] locked = [];
    private CancellationTokenSource? pending;
    private RasterImage? baselineImage, variantImage;
    private IImage? previousImage;
    private bool closed;
    private bool restoringRecipe;
    private readonly StackPanel scope = new() { Spacing = 4 };
    internal bool Busy => pending != null;

    internal QuickMapVariants(EditorWindow window, MapCreationSession session, GeneratorPreviewState state, Button apply, Image image, AssetStore assets)
    {
        this.window = window; this.session = session; this.state = state; this.apply = apply; this.image = image; this.assets = assets; Spacing = 6;
        Children.Add(MapDesignEditor.Hint("Lock an approved layout, then rearrange only complete non-blocking native decoration stamps already present in it. Ground, collision, structural objects and design reservations stay fixed. Missing safe placements remain unchanged."));
        Children.Add(freeze); Children.Add(MapDesignEditor.Fields(("Decoration seed", seed)));
        Children.Add(MapDesignEditor.Hint("Choose which native object types may move. Nothing is selected automatically; non-blocking does not by itself mean an object is decorative."));
        Children.Add(new ScrollViewer { Content = scope, MaxHeight = 140 });
        var actions = new WrapPanel(); actions.Children.Add(generate); actions.Children.Add(stop); Children.Add(actions); Children.Add(before); Children.Add(feedback);
        freeze.Click += async (_, _) =>
        {
            if (session.StructureLocked)
            {
                if (session.DecorationVariant != null && MessageBox.Show(window, "Unlocking discards the decoration variant and returns to the original generator preview. Continue?", "Layout lock", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
                Unlock();
            }
            else await LockAsync();
        };
        var discardRequested = new Button { Name = "QuickMapDiscardRequestedVariant", Content = LocalText.T("Use base layout instead of the requested decoration variant"), IsVisible = session.RequestedDecorationSeed != null || session.RequestedLock };
        discardRequested.Click += (_, _) =>
        {
            if (Busy) return;
            session.RequestedDecorationSeed = null; session.RequestedLock = false; discardRequested.IsVisible = false;
            if (session.StructureLocked) Unlock(); else UpdateBusy();
        };
        Children.Add(discardRequested);
        generate.Click += async (_, _) => await GenerateAsync(); stop.Click += (_, _) => { pending?.Cancel(); stop.IsEnabled = false; };
        before.IsCheckedChanged += (_, _) => ShowImage();
        seed.ValueChanged += (_, _) => { if (!Busy) InvalidateVariant(); };
        state.Changed += (_, _) =>
        {
            freeze.IsEnabled = !Busy && !session.BatchRunning && state.Phase == GeneratorPreviewPhase.Ready;
            if (session.StructureLocked && !Busy && state.Phase != GeneratorPreviewPhase.Ready)
            { Unlock(); feedback.Text = LocalText.T("The generator preview changed. The layout lock and decoration variant were invalidated; review the new preview before locking again."); }
        };
        window.Closed += (_, _) => { closed = true; pending?.Cancel(); image.Source = null; baselineImage?.Dispose(); variantImage?.Dispose(); };
        state.Completed += async (_, _) =>
        {
            if (session.RequestedDecorationSeed == null && !session.RequestedLock || Busy || restoringRecipe || session.BatchRunning || window is TerrainGeneratorWindow { PreferencesRestored: false }) return;
            int? requested = session.RequestedDecorationSeed;
            restoringRecipe = true;
            try
            {
                if (await LockAsync())
                {
                    session.RequestedLock = false;
                    if (requested is { } value)
                    {
                        seed.Value = value; await GenerateAsync();
                        if (session.DecorationVariant?.Seed == requested) { session.RequestedDecorationSeed = null; discardRequested.IsVisible = false; }
                    }
                }
            }
            finally { restoringRecipe = false; if (!closed) UpdateBusy(); }
        };
    }

    private void LockInputs()
    {
        if (locked.Length > 0) return;
        locked = window.GetLogicalDescendants().OfType<Control>().Where(control => !control.GetLogicalAncestors().Contains(this) &&
            (control is ComboBox or TextBox or Slider or CheckBox or NumericUpDown || control.Name is "GeneratorWorkflow" or "QuickMapCandidatesPanel" or "QuickMapBack" or
                "QuickMapDesignUndo" or "QuickMapDesignRedo" or "QuickMapOverview" or "QuickMapInspectCentre" or "RandomizeButton" ||
                control is Button { Parent: DockPanel seedPanel } && seedPanel.Children.OfType<TextBox>().Any(box => box.Name == "SeedBox")))
            .Select(control => (Control: control, Enabled: control.IsEnabled)).ToArray();
        foreach (var item in locked) item.Control.IsEnabled = false;
    }

    internal async Task<bool> LockAsync()
    {
        if (Busy || session.BatchRunning || state.Phase != GeneratorPreviewPhase.Ready || QuickMapProduction.CurrentPlan(window) is not { } plan) return false;
        using var job = new CancellationTokenSource(); pending = job; session.VariationBusy = true; previousImage = image.Source; LockInputs(); UpdateBusy();
        try
        {
            var design = session.Design.Copy();
            var snapshot = await Task.Run(() => QuickMapProduction.Materialize(plan, job.Token), job.Token);
            var check = await Task.Run(() => MapDesignConstraints.Check(snapshot, design, job.Token), job.Token);
            if (!check.Accepted) throw new InvalidOperationException(LocalText.T("Fix required layout issues before locking this map."));
            var rendered = await RenderAsync(snapshot, job.Token);
            if (closed || job.IsCancellationRequested || !ReferenceEquals(plan, QuickMapProduction.CurrentPlan(window)) || !design.SameAs(session.Design)) { rendered.Dispose(); return false; }
            baselineImage?.Dispose(); baselineImage = rendered;
            scope.Children.Clear();
            var names = snapshot.PlacedStamps.Where(stamp => stamp.Wall == null && stamp.GeneratedBanks == null && stamp.Changes.Count > 0 &&
                stamp.Changes.All(delta => MapDecorationVariation.SameStructure(delta.Before, delta.After) && (delta.After.BackImage & 0x60000000) == 0))
                .GroupBy(stamp => stamp.Name, StringComparer.Ordinal).OrderBy(group => group.Key).ToArray();
            if (names.Length > 1000) throw new InvalidOperationException(LocalText.T("Too many native object types for a bounded decoration variation. Use a smaller layout."));
            if (session.RequestedDecorationSeed != null && session.DecorationNames is { } expected && expected.Any(name => !names.Any(group => group.Key == name)))
                throw new InvalidOperationException(LocalText.T("A requested native decoration type is missing. The variant was not restored; review the layout or explicitly use the base layout."));
            var selectedNames = session.DecorationNames?.ToHashSet(StringComparer.Ordinal) ?? [];
            foreach (var group in names)
            {
                var choice = new CheckBox { Content = LocalText.Format("Allow moving {0} ({1})", group.Key, group.Count()), Tag = group.Key, IsChecked = selectedNames.Contains(group.Key) };
                choice.IsCheckedChanged += (_, _) => { session.DecorationNames = scope.Children.OfType<CheckBox>().Where(item => item.IsChecked == true).Select(item => (string)item.Tag!).ToList(); InvalidateVariant(); session.NotifyDraft(); };
                scope.Children.Add(choice);
            }
            session.DecorationNames = selectedNames.Where(name => names.Any(group => group.Key == name)).ToList();
            session.LockedBaseline = snapshot;
            feedback.Text = LocalText.T("Layout locked. Only safe native decoration may move; markers and structural parameters are frozen. Unlock explicitly to change them.");
            session.NotifyOutput(); ShowImage(); return true;
        }
        catch (OperationCanceledException) { feedback.Text = LocalText.T("Operation cancelled. No pending changes were applied."); return false; }
        catch (Exception exception) { feedback.Text = exception.Message; return false; }
        finally { pending = null; session.VariationBusy = false; if (!closed) { if (!session.StructureLocked) RestoreInputs(); UpdateBusy(); } }
    }

    internal async Task GenerateAsync()
    {
        if (Busy || session.LockedBaseline is not { } baseline) return;
        int value;
        try { value = MapDesignEditor.Value(seed); }
        catch (InvalidOperationException exception) { feedback.Text = exception.Message; return; }
        using var job = new CancellationTokenSource(); pending = job; session.VariationBusy = true; UpdateBusy();
        try
        {
            var design = session.Design.Copy();
            var allowed = session.DecorationNames?.ToHashSet(StringComparer.Ordinal) ?? [];
            var result = await Task.Run(() => MapDecorationVariation.Create(baseline, design, value, job.Token, allowed), job.Token);
            var rendered = await RenderAsync(result.Generated, job.Token);
            if (closed || job.IsCancellationRequested || !ReferenceEquals(baseline, session.LockedBaseline) || !design.SameAs(session.Design)) { rendered.Dispose(); return; }
            image.Source = null; variantImage?.Dispose(); variantImage = rendered; session.DecorationVariant = result;
            before.IsChecked = false; ShowImage(); session.NotifyOutput();
            feedback.Text = LocalText.Format("Moved {0} of {1} eligible native decorations; {2} changed cells. Ground and collision fingerprint verified. Zero changes means no safe alternative was found.", result.Moved, result.Movable, result.Changed.Count);
        }
        catch (OperationCanceledException) { feedback.Text = LocalText.T("Operation cancelled. No pending changes were applied."); }
        catch (Exception exception) { feedback.Text = exception.Message; }
        finally { pending = null; session.VariationBusy = false; if (!closed) UpdateBusy(); }
    }

    private async Task<RasterImage> RenderAsync(MapDocument map, CancellationToken cancellation)
    {
        var rendered = await Task.Run(() => StampPreview.Create(map.Cells.Select(pair => new StampCell(pair.Key % map.Width, pair.Key / map.Width, pair.Value)).ToArray(),
            assets, 1600, 1000, cancellation, new Rect(0, 0, map.Width * 48, map.Height * 32), waitForAssets: true) ?? throw new InvalidOperationException(LocalText.T("Preview unavailable")), cancellation);
        if (cancellation.IsCancellationRequested) { rendered.Dispose(); cancellation.ThrowIfCancellationRequested(); }
        return rendered;
    }
    private void ShowImage()
    {
        if (!session.StructureLocked) return;
        image.Source = before.IsChecked == true || variantImage == null ? baselineImage : variantImage;
        if (window.GetLogicalDescendants().OfType<TextBlock>().FirstOrDefault(control => control.Name == "PreviewMapInfo") is { } info)
            info.Text = LocalText.T(before.IsChecked == true && session.DecorationVariant != null ? "Showing the baseline for comparison. Create Map still uses the generated decoration variant." : "Creates a new map with the locked layout and the displayed decoration. The original map is not modified.");
    }
    private void InvalidateVariant()
    {
        if (session.DecorationVariant == null) return;
        session.DecorationVariant = null; image.Source = null; variantImage?.Dispose(); variantImage = null; before.IsChecked = false; ShowImage();
        feedback.Text = LocalText.T("Decoration settings changed. The previous variant was discarded; generate again. The locked baseline is unchanged.");
        session.NotifyOutput(); UpdateBusy();
    }
    private void RestoreInputs() { foreach (var item in locked) item.Control.IsEnabled = item.Enabled; locked = []; }
    private void UpdateBusy()
    {
        state.Notify(); freeze.IsEnabled = !Busy && !session.BatchRunning && state.Phase == GeneratorPreviewPhase.Ready;
        freeze.Content = LocalText.T(session.StructureLocked ? "Unlock layout" : "Lock layout and collision");
        generate.IsEnabled = !Busy && session.StructureLocked; seed.IsEnabled = !Busy;
        scope.IsEnabled = !Busy;
        before.IsEnabled = !Busy && session.DecorationVariant != null; stop.IsEnabled = Busy;
        apply.IsEnabled = !Busy && !session.RequestedLock && session.RequestedDecorationSeed == null && state.Phase == GeneratorPreviewPhase.Ready && QuickMapProduction.CurrentPlan(window) != null;
    }
    private void Unlock()
    {
        session.LockedBaseline = null; session.DecorationVariant = null; session.RequestedDecorationSeed = null; session.RequestedLock = false; before.IsChecked = false;
        image.Source = previousImage; baselineImage?.Dispose(); baselineImage = null; variantImage?.Dispose(); variantImage = null;
        if (window.GetLogicalDescendants().OfType<TextBlock>().FirstOrDefault(control => control.Name == "PreviewMapInfo") is { } info) info.Text = LocalText.T("Creates a new map. The original map is not modified.");
        RestoreInputs(); session.NotifyOutput(); UpdateBusy();
    }
}
