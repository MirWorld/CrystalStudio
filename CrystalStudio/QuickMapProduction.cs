using Avalonia.LogicalTree;
using System.Globalization;

namespace CrystalStudio;

internal sealed record QuickMapBatchResult(int Seed, Dictionary<string, string> Parameters, MapCandidateMetrics? Metrics,
    bool Accepted, string Details)
{
    internal MapDesignSettings? Design { get; init; }
    internal GenerationFailure? Failure { get; init; }
    public override string ToString() => LocalText.Format("Seed {0} · {1}", Seed, LocalText.T(Accepted ? "Accepted" : "Filtered")) + "\n" + (Metrics?.ToString() ?? Details) +
        (Metrics != null && Details.Length > 0 ? "\n" + Details : "");
}

internal sealed class QuickMapProduction : StackPanel
{
    private readonly EditorWindow window;
    private readonly MapCreationSession session;
    private readonly GeneratorPreviewState state;
    private readonly Button apply;
    private readonly TextBlock feedback = new() { Name = "QuickMapProductionFeedback", TextWrapping = TextWrapping.Wrap };
    private readonly ListBox results = new() { Name = "QuickMapBatchResults", MaxHeight = 220 };
    private readonly List<QuickMapBatchResult> batchResults = [];
    private readonly Button start = new() { Name = "QuickMapBatchStart", Content = LocalText.T("Search seeds") };
    private readonly Button stop = new() { Name = "QuickMapBatchStop", Content = LocalText.T("Stop search"), IsEnabled = false };
    private readonly Button restore = new() { Name = "QuickMapBatchRestore", Content = LocalText.T("Restore selected seed"), IsEnabled = false };
    private readonly MapDesignEditor editor;
    private readonly NumericUpDown attempts = MapDesignEditor.Number("QuickMapBatchCount", 2, 10, 5);
    private readonly NumericUpDown minimumWalkable = MapDesignEditor.Number("QuickMapMinWalkable", 0, 100, 0);
    private readonly NumericUpDown minimumConnected = MapDesignEditor.Number("QuickMapMinConnected", 0, 100, 0);
    private readonly NumericUpDown minimumRooms = MapDesignEditor.Number("QuickMapMinRooms", 0, 1000, 0);
    private CancellationTokenSource? pending;
    private bool closed, confirming;
    internal event Action? CandidatesChanged;

    internal QuickMapProduction(EditorWindow window, MapCreationSession session, GeneratorPreviewState state, Button apply)
    {
        this.window = window; this.session = session; this.state = state; this.apply = apply; Spacing = 6;
        editor = new(session.Design, session.Settings.Width, session.Settings.Height);
        GenerationAlgorithms.SetDesign(window, session.Design);
        Children.Add(new Expander { Header = LocalText.T("Route points, reservations and protection"), Content = editor, HorizontalAlignment = HorizontalAlignment.Stretch });
        string ReservationHint() => GenerationAlgorithms.UsesConstraints(GenerationAlgorithms.Version(window))
            ? "Required reservations guide placement before decoration. Required routes reserve an existing collision-free path; no road is carved automatically. Incompatible native structures still fail explicitly. Preferences only warn; protection regions retain their local-regeneration meaning."
            : "Reservations filter generated layouts; they do not carve roads or place server entities. Required conflicts block creation; preferences only warn. Search may find no matching seed.";
        var reservationHint = MapDesignEditor.Hint(ReservationHint()); Children.Add(reservationHint);
        Children.Add(MapDesignEditor.Fields(("Attempts (2–10)", attempts), ("Min walkable %", minimumWalkable), ("Min reachable %", minimumConnected), ("Min rooms (dungeon)", minimumRooms)));
        Children.Add(MapDesignEditor.Hint("Reachability starts at the first route point, or uses the largest area when no point is defined. Percentages count valid footprint anchors. Measured candidate selection favors quality and different layouts, not arrival order."));
        minimumRooms.IsEnabled = session.Settings.Kind == MapCreationKind.Dungeon;
        var actions = new WrapPanel(); actions.Children.Add(start); actions.Children.Add(stop); actions.Children.Add(restore);
        foreach (Control child in actions.Children) child.Margin = new Thickness(0, 0, 6, 4);
        Children.Add(actions); Children.Add(feedback); Children.Add(results);
        Avalonia.Automation.AutomationProperties.SetLiveSetting(feedback, Avalonia.Automation.AutomationLiveSetting.Polite);
        results.ItemTemplate = new FuncDataTemplate<QuickMapBatchResult>((item, _) => new TextBlock { Text = item?.ToString(), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(4) });
        Children.Add(MapDesignEditor.Hint("Seeds are tested sequentially from the current seed. Only matching results enter the five bounded candidates; all-favorite candidates are never evicted. No map is applied automatically."));
        results.SelectionChanged += (_, _) => restore.IsEnabled = !session.BatchRunning && !confirming && results.SelectedItem is QuickMapBatchResult { Metrics: not null };
        restore.Click += (_, _) =>
        {
            if (!session.BatchRunning && !confirming && results.SelectedItem is QuickMapBatchResult result)
            {
                if (result.Design != null) RestoreDesign(result.Design);
                GeneratorPreferences.Restore(window, result.Parameters);
                feedback.Text = LocalText.Format("Restored seed {0}. Review the rebuilt preview; constraints are checked again before creation.", result.Seed);
            }
        };
        start.Click += async (_, _) => await SearchAsync();
        stop.Click += (_, _) =>
        {
            pending?.Cancel(); if (session.BatchRunning) CancelGenerator();
            stop.IsEnabled = false; feedback.Text = LocalText.T("Stopping… Please wait for cancellation to finish.");
        };
        void SettingsChanged()
        {
            if (!session.BatchRunning && !confirming && batchResults.Count > 0)
                feedback.Text = LocalText.T("Search settings changed. Listed results belong to the previous search; search again to evaluate the new settings.");
        }
        editor.Changed += () =>
        {
            SettingsChanged(); GenerationAlgorithms.SetDesign(window, session.Design);
            if (!session.BatchRunning && !confirming) apply.IsEnabled = editor.InputValid && CurrentPlan(window) != null && state.Phase == GeneratorPreviewPhase.Ready;
            start.IsEnabled = editor.InputValid && !session.BatchRunning && !confirming && CanSearch;
        };
        foreach (var number in new[] { attempts, minimumWalkable, minimumConnected, minimumRooms }) number.ValueChanged += (_, _) => SettingsChanged();
        state.Changed += (_, _) =>
        {
            reservationHint.Text = LocalText.T(ReservationHint());
            start.IsEnabled = editor.InputValid && !session.BatchRunning && !confirming && CanSearch;
            if (session.BatchRunning || confirming || !editor.InputValid) apply.IsEnabled = false;
        };
        window.Closed += (_, _) => { closed = true; pending?.Cancel(); };
        start.IsEnabled = CanSearch;
    }

    internal void RestoreDesign(MapDesignSettings design) => editor.Restore(design);

    private bool CanSearch => state.Phase == GeneratorPreviewPhase.Ready ||
        GenerationAlgorithms.UsesConstraints(GenerationAlgorithms.Version(window)) && state.Phase == GeneratorPreviewPhase.Failed;

    internal static object? CurrentPlan(EditorWindow window) => window switch
    {
        TerrainGeneratorWindow terrain => terrain.Plan, ProvinceGeneratorWindow province => province.Plan,
        TempleGeneratorWindow dungeon => dungeon.Plan, _ => null
    };
    internal static int? PlanSeed(object? plan) => plan switch
    {
        TerrainPlan terrain => terrain.Seed, ProvincePlan province => province.Options.Seed, TemplePlan dungeon => dungeon.Options.Seed, _ => null
    };
    internal static MapDocument Materialize(object plan, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        var map = plan switch
        {
            TerrainPlan terrain => GeneratedMapSnapshot.Terrain(terrain, cancellation),
            ProvincePlan province => MapRecoveryStore.Capture(province.Generated),
            TemplePlan dungeon => MapRecoveryStore.Capture(dungeon.Generated),
            _ => throw new InvalidOperationException("No generated map is available.")
        };
        map.SourceFormat = plan switch { TerrainPlan terrain => terrain.Source.SourceFormat, ProvincePlan province => province.Source.SourceFormat, TemplePlan dungeon => dungeon.Source.SourceFormat, _ => map.SourceFormat };
        return map;
    }
    private RasterImage? Overview => window switch
    {
        TerrainGeneratorWindow terrain => terrain.OverviewPreview,
        ProvinceGeneratorWindow province => province.OverviewPreview,
        TempleGeneratorWindow dungeon => dungeon.OverviewPreview, _ => null
    };

    internal async Task<bool> ConfirmAsync()
    {
        if (session.BatchRunning || confirming || closed || state.Phase != GeneratorPreviewPhase.Ready || CurrentPlan(window) is not { } plan) return false;
        try { editor.EnsureValid(); }
        catch (Exception exception) { feedback.Text = exception.Message; return false; }
        if (session.Design.Empty) return true;
        confirming = true; apply.IsEnabled = false;
        using var job = new CancellationTokenSource(); pending = job;
        var locked = LockInputs(); editor.IsEnabled = false; start.IsEnabled = restore.IsEnabled = false;
        stop.Content = LocalText.T("Stop check"); stop.IsEnabled = true;
        var design = session.Design.Copy(); string identity = Convert.ToBase64String(StudioJson.SerializeToUtf8Bytes(new MapDesignFile(1, session.Settings.Width, session.Settings.Height, design)));
        bool Current() => !closed && !job.IsCancellationRequested && state.Phase == GeneratorPreviewPhase.Ready && ReferenceEquals(CurrentPlan(window), plan) &&
            identity == Convert.ToBase64String(StudioJson.SerializeToUtf8Bytes(new MapDesignFile(1, session.Settings.Width, session.Settings.Height, session.Design)));
        try
        {
            feedback.Text = LocalText.T("Checking layout constraints…");
            var check = await Task.Run(() => MapDesignConstraints.Check(Materialize(plan, job.Token), design, job.Token), job.Token);
            job.Token.ThrowIfCancellationRequested();
            if (!Current()) { if (!closed) feedback.Text = LocalText.T("The preview or constraints changed. Check the current preview again."); return false; }
            feedback.Text = check.Findings.Count == 0 ? LocalText.T("All layout constraints are satisfied.") : string.Join("\n", check.Findings);
            if (!check.Accepted) MessageBox.Show(window, feedback.Text, "Layout constraints", MessageBoxButton.OK, MessageBoxImage.Warning);
            else if (check.Findings.Count > 0 && MessageBox.Show(window, feedback.Text + "\n\n" + LocalText.T("Preferred constraints were not met. Create this map anyway?"), "Layout constraints", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return false;
            return check.Accepted && Current();
        }
        catch (OperationCanceledException) { if (!closed) feedback.Text = LocalText.T("Operation cancelled. No pending changes were applied."); return false; }
        catch (Exception exception) { if (!closed) { feedback.Text = exception.Message; MessageBox.Show(window, exception.Message, "Layout constraints"); } return false; }
        finally
        {
            confirming = false; if (ReferenceEquals(pending, job)) pending = null;
            if (!closed)
            {
                foreach (var item in locked) item.Control.IsEnabled = item.Enabled;
                editor.IsEnabled = true; stop.Content = LocalText.T("Stop search"); stop.IsEnabled = false;
                restore.IsEnabled = results.SelectedItem is QuickMapBatchResult { Metrics: not null };
                apply.IsEnabled = CurrentPlan(window) != null && state.Phase == GeneratorPreviewPhase.Ready; state.Notify(); CandidatesChanged?.Invoke();
            }
        }
    }

    internal async Task SearchAsync()
    {
        if (closed || session.BatchRunning || confirming || !CanSearch) return;
        var seedBox = window.GetLogicalDescendants().OfType<TextBox>().Distinct().Single(control => control.Name == "SeedBox");
        if (!int.TryParse(seedBox.Text, out int firstSeed)) return;
        var locked = LockInputs();
        using var job = new CancellationTokenSource(); pending = job;
        try
        {
            editor.EnsureValid();
            var design = session.Design.Copy(); design.Validate(session.Settings.Width, session.Settings.Height);
            int count = MapDesignEditor.Value(attempts), walkable = MapDesignEditor.Value(minimumWalkable), connected = MapDesignEditor.Value(minimumConnected), rooms = MapDesignEditor.Value(minimumRooms);
            var thresholds = new MapCandidateThresholds(walkable, connected, rooms, session.Settings.Kind == MapCreationKind.Dungeon);
            GenerationFailure? stoppedBy = null;
            session.BatchRunning = true; batchResults.Clear(); results.ItemsSource = null;
            foreach (var item in locked) item.Control.IsEnabled = false;
            editor.IsEnabled = start.IsEnabled = restore.IsEnabled = false; stop.IsEnabled = true;
            attempts.IsEnabled = minimumWalkable.IsEnabled = minimumConnected.IsEnabled = minimumRooms.IsEnabled = false;
            if (window.GetLogicalDescendants().OfType<Button>().FirstOrDefault(button => button.Name == "QuickMapBack") is { } back) back.IsEnabled = false;
            CandidatesChanged?.Invoke();
            var detail = window.GetLogicalDescendants().OfType<CheckBox>().Distinct().Single(control => control.Name == "DetailToggle"); detail.IsChecked = false;
            for (int index = 0; index < count; index++)
            {
                job.Token.ThrowIfCancellationRequested();
                int seed = unchecked(firstSeed + index);
                feedback.Text = LocalText.Format("Searching {0}/{1} · Seed {2}", index + 1, count, seed);
                try
                {
                    await WaitForSeedAsync(seedBox, seed, job.Token);
                    var plan = CurrentPlan(window) ?? throw new InvalidOperationException("Preview has no plan.");
                    var parameters = GeneratorPreferences.Capture(window);
                    var measured = await Task.Run(() =>
                    {
                        var map = Materialize(plan, job.Token);
                        return (Metrics: MapRouteAnalysis.Measure(map, plan is TemplePlan dungeon ? dungeon.RoomCount : null, job.Token, design), Check: MapDesignConstraints.Check(map, design, job.Token));
                    }, job.Token);
                    job.Token.ThrowIfCancellationRequested();
                    if (!ReferenceEquals(CurrentPlan(window), plan) || state.Phase != GeneratorPreviewPhase.Ready)
                        throw new InvalidOperationException(LocalText.T("The preview or constraints changed. Check the current preview again."));
                    var metrics = measured.Metrics with { PreferredConflicts = measured.Check.Findings.Count(finding => !finding.Required) };
                    var metricFailures = thresholds.Failures(metrics);
                    bool accepted = measured.Check.Accepted && metricFailures.Count == 0;
                    string details = string.Join("\n", measured.Check.Findings.Select(finding => finding.ToString()).Concat(metricFailures));
                    if (accepted)
                    {
                        var retained = session.Candidates.RememberMeasured(session.Settings, parameters, metrics, Overview, design);
                        if (retained.Candidate != null) retained.Candidate.DisplayValues = GeneratorPreferences.Describe(window, retained.Candidate.Parameters);
                        details += (details.Length == 0 ? "" : "\n") + retained.Explanation;
                    }
                    batchResults.Add(new(seed, parameters, metrics, accepted, details)
                        { Design = design.Copy(), Failure = accepted ? null : new(GenerationFailureKind.SeedRejected, "Candidate filtering", details, measured.Check.Findings.ToArray()) });
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception exception)
                {
                    var failure = GenerationDiagnostics.Describe(exception, "Seed search");
                    batchResults.Add(new(seed, GeneratorPreferences.Capture(window), null, false, failure.ToString()) { Failure = failure, Design = design.Copy() });
                    if (failure.Kind is GenerationFailureKind.InputConflict or GenerationFailureKind.MissingAssets or GenerationFailureKind.Unexpected) stoppedBy = failure;
                }
                results.ItemsSource = batchResults.ToArray(); CandidatesChanged?.Invoke();
                if (stoppedBy != null) break;
            }
            feedback.Text = LocalText.Format("Search complete: {0}/{1} matched. Select a result to restore; nothing was applied.", batchResults.Count(result => result.Accepted), batchResults.Count);
            if (stoppedBy != null) feedback.Text += "\n" + stoppedBy;
            else if (batchResults.All(result => !result.Accepted)) feedback.Text += "\n" + new GenerationFailure(GenerationFailureKind.SearchExhausted, "Seed search", LocalText.T("No matching seed was found within the requested attempt count."), []);
        }
        catch (OperationCanceledException) { if (!closed) feedback.Text = LocalText.T("Search stopped. Completed results are retained; no map was applied."); }
        catch (Exception exception) { if (!closed) feedback.Text = GenerationDiagnostics.Describe(exception, "Seed search").ToString(); }
        finally
        {
            session.BatchRunning = false; if (ReferenceEquals(pending, job)) pending = null;
            if (!closed)
            {
                foreach (var item in locked) item.Control.IsEnabled = item.Enabled;
                editor.IsEnabled = attempts.IsEnabled = minimumWalkable.IsEnabled = minimumConnected.IsEnabled = true;
                minimumRooms.IsEnabled = session.Settings.Kind == MapCreationKind.Dungeon; stop.IsEnabled = false;
                if (window.GetLogicalDescendants().OfType<Button>().FirstOrDefault(button => button.Name == "QuickMapBack") is { } back) back.IsEnabled = true;
                apply.IsEnabled = CurrentPlan(window) != null && state.Phase == GeneratorPreviewPhase.Ready;
                restore.IsEnabled = results.SelectedItem is QuickMapBatchResult { Metrics: not null };
                state.Notify(); CandidatesChanged?.Invoke();
            }
        }
    }

    private (Control Control, bool Enabled)[] LockInputs()
    {
        var controls = window.GetLogicalDescendants().OfType<Control>().Distinct().Where(control =>
            !control.GetLogicalAncestors().Contains(this) && control is not Button { IsCancel: true } &&
            (control is ComboBox or TextBox or Slider or CheckBox or Button or NumericUpDown || control.Name is "GeneratorWorkflow" or "QuickMapCandidatesPanel"))
            .Select(control => (Control: control, Enabled: control.IsEnabled)).ToArray();
        foreach (var item in controls) item.Control.IsEnabled = false;
        return controls;
    }

    private async Task WaitForSeedAsync(TextBox seedBox, int seed, CancellationToken cancellation)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Changed(object? sender, EventArgs args)
        {
            if (!int.TryParse(seedBox.Text, out int current) || current != seed) return;
            if (state.Phase == GeneratorPreviewPhase.Ready && PlanSeed(CurrentPlan(window)) == seed) completion.TrySetResult();
            else if (state.Phase == GeneratorPreviewPhase.Failed)
            {
                var failure = state.FailureDetails ?? new GenerationFailure(GenerationFailureKind.SeedRejected, "Preview generation", state.Failure ?? LocalText.T("Preview unavailable"), []);
                completion.TrySetException(GenerationDiagnostics.Reject(failure.Kind, failure.Stage, failure.Message, failure.Findings));
            }
            else if (state.Phase == GeneratorPreviewPhase.Cancelled) completion.TrySetCanceled();
        }
        state.Changed += Changed;
        using var registration = cancellation.Register(() => completion.TrySetCanceled(cancellation));
        try
        {
            seedBox.Text = seed.ToString(CultureInfo.InvariantCulture); Changed(null, EventArgs.Empty);
            await completion.Task;
        }
        finally { state.Changed -= Changed; }
    }
    private void CancelGenerator()
    {
        switch (window)
        {
            case TerrainGeneratorWindow terrain: terrain.CancelPreview(); break;
            case ProvinceGeneratorWindow province: province.CancelPreview(); break;
            case TempleGeneratorWindow dungeon: dungeon.CancelPreview(); break;
        }
    }
}
