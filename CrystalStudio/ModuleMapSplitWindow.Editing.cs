using System.IO;

namespace CrystalStudio;

internal sealed partial class ModuleMapSplitWindow
{
    private sealed record Snapshot(ModuleSplitResult Result, HashSet<string> Selected, ModuleArea[] Protection, ModuleArea? Active);
    private readonly Stack<Snapshot> undo = new(), redo = new();
    private readonly List<ModuleArea> protection = [];
    private IReadOnlyList<ModuleArea> objectProtection = [];
    private bool dragging, applyingSnapshot;
    private (int X, int Y)? protectionStart;
    private ModuleArea[]? replaced;
    private ModuleArea? mergeFirst;
    private ModuleSplitResult? previousResult;
    internal ComboBox ModeBox { get; } = new() { ItemsSource = new[] { LocalText.T("Fixed grid"), LocalText.T("Along corridors") }, SelectedIndex = 0 };
    internal ComboBox ToolBox { get; } = new() { ItemsSource = new[] { LocalText.T("Select block"), LocalText.T("Vertical cut"), LocalText.T("Horizontal cut"), LocalText.T("Move cut"), LocalText.T("Merge blocks"), LocalText.T("Protect region") }, SelectedIndex = 0, MinWidth = 130 };
    internal Button UndoButton { get; } = new() { Content = LocalText.T("Undo") };
    internal Button RedoButton { get; } = new() { Content = LocalText.T("Redo") };
    internal Button LocateButton { get; } = new() { Content = LocalText.T("Locate problem") };
    internal ComboBox RegionsBox { get; } = new() { MinWidth = 160, MaxWidth = 260 };
    private readonly TextBlock instruction = new() { TextWrapping = TextWrapping.Wrap };

    private void InitializeEditing(Panel toolbar)
    {
        objectProtection = ModulePartitions.ObjectProtection(source);
        settings.Children.Insert(1, new TextBlock { Text = LocalText.T("Split method") });
        settings.Children.Insert(2, ModeBox);
        ModeBox.SelectionChanged += (_, _) => InvalidateSplit();
        toolbar.Children.Add(ToolBox); toolbar.Children.Add(UndoButton); toolbar.Children.Add(RedoButton); toolbar.Children.Add(LocateButton);
        var terrain = new CheckBox { Content = LocalText.T("Passable regions"), IsChecked = true, Margin = new Thickness(6) };
        terrain.IsCheckedChanged += (_, _) => { Preview.ShowTerrain = terrain.IsChecked == true; Preview.Refresh(); }; toolbar.Children.Add(terrain);
        Preview.ShowTerrain = true;
        var grid = new CheckBox { Content = LocalText.T("Grid"), IsChecked = false, Margin = new Thickness(6) };
        grid.IsCheckedChanged += (_, _) => { Preview.Viewport.GridVisible = grid.IsChecked == true; Preview.Refresh(); }; toolbar.Children.Add(grid);
        Preview.Viewport.GridVisible = false;
        RegionsBox.SelectionChanged += (_, _) =>
        {
            if (RegionsBox.SelectedItem is ModuleTerrainRegion region) { Preview.Frame(region.Area); Preview.ShowTerrain = true; terrain.IsChecked = true; }
        };
        toolbar.Children.Add(RegionsBox);
        var clear = new Button { Content = LocalText.T("Clear protection") };
        clear.Click += (_, _) => { if (Result == null || pending != null || protection.Count == 0) return; Remember(); protection.Clear(); Refresh(); }; toolbar.Children.Add(clear);
        settings.Children.Add(instruction);
        ToolBox.SelectionChanged += (_, _) => { CancelDraft(); Preview.FocusCanvas(); };
        UndoButton.Click += (_, _) => Restore(undo, redo);
        RedoButton.Click += (_, _) => Restore(redo, undo);
        LocateButton.Click += (_, _) =>
        {
            if (PartsList.SelectedItem is ModuleSplitChoice c)
                Preview.Locate(c.Part.Problem?.X ?? c.Part.Area.X, c.Part.Problem?.Y ?? c.Part.Area.Y);
        };
        Preview.Pressed += BeginEdit;
        Preview.Moved += MoveEdit;
        Preview.Released += async (x, y) => await EndEditAsync(x, y);
        Preview.CaptureCancelled += () => { if (dragging) CancelDraft(); };
        Preview.Shortcut += (key, modifiers) =>
            HandleShortcut(key, modifiers);
        AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Handled || e.Source is Visual visual && (visual is TextBox || visual.GetVisualAncestors().OfType<TextBox>().Any())) return;
            if (e.Key == Key.Escape || e.KeyModifiers.HasFlag(KeyModifiers.Control) && e.Key is Key.Z or Key.Y)
            { HandleShortcut(e.Key, e.KeyModifiers); e.Handled = true; }
        });
    }

    private void HandleShortcut(Key key, KeyModifiers modifiers)
    {
        if (key == Key.Escape) { pending?.Cancel(); CancelDraft(); ToolBox.SelectedIndex = 0; }
        if (key == Key.Z && modifiers.HasFlag(KeyModifiers.Control)) Restore(modifiers.HasFlag(KeyModifiers.Shift) ? redo : undo, modifiers.HasFlag(KeyModifiers.Shift) ? undo : redo);
        if (key == Key.Y && modifiers.HasFlag(KeyModifiers.Control)) Restore(redo, undo);
    }

    private void InvalidateSplit()
    {
        if (applyingSnapshot) return;
        revision++; pending?.Cancel(); previousResult = Result ?? previousResult; Result = null;
        undo.Clear(); redo.Clear(); CancelDraft(); Refresh();
    }
    private Snapshot Current() => new(Result!, Choices.Where(c => c.Selected).Select(c => ModulePartitions.Key(c.Part.Area)).ToHashSet(),
        protection.ToArray(), (PartsList.SelectedItem as ModuleSplitChoice)?.Part.Area);
    private void Remember()
    {
        undo.Push(Current());
        // Bound retained artwork as well as operation count. Always retain the most
        // recent operation, including for a map larger than the history budget.
        long cells = 0; var keep = new List<Snapshot>();
        foreach (var snapshot in undo)
        {
            long size = snapshot.Result.Parts.Sum(p => (long)(p.Draft?.Cells.Count ?? 0));
            if (keep.Count > 0 && (keep.Count == 16 || cells + size > 2_000_000)) break;
            keep.Add(snapshot); cells += size;
        }
        undo.Clear(); foreach (var snapshot in keep.AsEnumerable().Reverse()) undo.Push(snapshot); redo.Clear();
    }
    private void Restore(Stack<Snapshot> from, Stack<Snapshot> to)
    {
        if (Result == null || pending != null || from.Count == 0 || Saved != null) return;
        CancelDraft(); to.Push(Current()); var snapshot = from.Pop(); protection.Clear(); protection.AddRange(snapshot.Protection);
        Present(snapshot.Result, snapshot.Selected, snapshot.Active); Refresh();
    }
    private void Present(ModuleSplitResult result, HashSet<string>? selected = null, ModuleArea? active = null)
    {
        applyingSnapshot = true;
        try
        {
            Result = result; previousResult = result;
            foreach (var old in Choices) old.Dispose();
            Choices.Clear();
            foreach (var part in result.Parts)
            {
                var choice = new ModuleSplitChoice(part, splitAssets);
                if (selected != null) choice.Selected = selected.Contains(ModulePartitions.Key(part.Area));
                choice.Changed += Refresh; Choices.Add(choice);
            }
            PartsList.ItemsSource = Choices.ToArray();
            PartsList.SelectedItem = Choices.FirstOrDefault(c => c.Part.Area == active) ?? Choices.FirstOrDefault(c => c.Part.Valid) ?? Choices.FirstOrDefault();
        }
        finally { applyingSnapshot = false; }
        Refresh();
    }
    private void CancelDraft()
    {
        dragging = false; protectionStart = null; replaced = null; mergeFirst = null;
        Preview.CutPreview = null; Preview.CutOpenings = []; Preview.ProtectionDraft = null; errorMessage = null; Refresh();
    }
    private void RefreshEditing()
    {
        bool available = Result != null && pending == null && Saved == null;
        UndoButton.IsEnabled = available && undo.Count > 0; RedoButton.IsEnabled = available && redo.Count > 0;
        ToolBox.IsEnabled = available; LocateButton.IsEnabled = PartsList.SelectedItem is ModuleSplitChoice;
        Preview.ProtectedRegions = objectProtection.Concat(protection).ToArray();
        instruction.Text = ToolBox.SelectedIndex switch
        {
            1 or 2 => LocalText.T("Click or drag inside a block to place a cut. Orange is a draft; red explains a rejected cut. Esc cancels."),
            3 => LocalText.T("Drag a complete shared edge between two blocks. T-junction edges must be merged first."),
            4 => LocalText.T("Click two adjacent blocks to merge their rectangular union."),
            5 => LocalText.T("Drag a protected rectangle. Automatic and manual cuts will avoid it. Esc cancels."),
            _ => LocalText.T("Click a block to inspect it; checkboxes choose what to save. Green is the main passage, orange marks other regions; blank cells may be passable.")
        };
        if (Result == null && Choices.Count > 0) instruction.Text = LocalText.T("Preview is out of date. Re-run the split before editing or saving.");
        if (Result != null && Preview.ProtectedRegions.Any(region => Result.Parts.Count(p => ModulePartitions.Intersects(p.Area, region)) > 1))
        {
            instruction.Text = LocalText.T("Existing cuts cross a protected region. Merge the affected blocks or preview the split again.");
            SaveButton.IsEnabled = GenerateButton.IsEnabled = false;
        }
        if (Preview.CutPreview != null || Preview.ProtectionDraft != null || mergeFirst != null)
            SaveButton.IsEnabled = GenerateButton.IsEnabled = false;
    }
    private ModuleSplitChoice? Hit(int x, int y) => Choices.FirstOrDefault(c => ModulePartitions.Contains(c.Part.Area, x, y));
    private void BeginEdit(int x, int y)
    {
        if (Result == null || pending != null || Saved != null) return;
        if (ToolBox.SelectedIndex == 0) { PartsList.SelectedItem = Hit(x, y); return; }
        if (ToolBox.SelectedIndex == 5)
        { dragging = true; protectionStart = (Math.Clamp(x, 0, source.Width - 1), Math.Clamp(y, 0, source.Height - 1)); MoveEdit(x, y); return; }
        if (ToolBox.SelectedIndex == 4)
        {
            var part = Hit(x, y); if (part == null) return;
            PartsList.SelectedItem = part;
            if (mergeFirst == null) { mergeFirst = part.Part.Area; Refresh(); return; }
            dragging = true; replaced = [mergeFirst.Value, part.Part.Area]; return;
        }
        if (ToolBox.SelectedIndex == 3)
        {
            // Hit testing uses screen distance so handles stay usable at every zoom.
            var pointer = Preview.Screen(Preview.PointerCellPosition.X, Preview.PointerCellPosition.Y);
            foreach (var a in Choices) foreach (var b in Choices)
            {
                if (a == b) continue;
                bool vertical = a.Part.Area.X + a.Part.Area.Width == b.Part.Area.X && a.Part.Area.Y == b.Part.Area.Y && a.Part.Area.Height == b.Part.Area.Height;
                bool horizontal = a.Part.Area.Y + a.Part.Area.Height == b.Part.Area.Y && a.Part.Area.X == b.Part.Area.X && a.Part.Area.Width == b.Part.Area.Width;
                if (!vertical && !horizontal) continue;
                int position = vertical ? b.Part.Area.X : b.Part.Area.Y;
                var line = Preview.Screen(vertical ? position : x, vertical ? y : position);
                if ((vertical ? Math.Abs(line.X - pointer.X) : Math.Abs(line.Y - pointer.Y)) > 8) continue;
                var union = ModulePartitions.Merge(a.Part.Area, b.Part.Area);
                if (!ModulePartitions.Contains(union, x, y)) continue;
                dragging = true; replaced = [a.Part.Area, b.Part.Area]; Preview.CutPreview = new(union, vertical, position); Refresh(); return;
            }
            errorMessage = LocalText.T("Drag a complete shared edge between two blocks. T-junction edges must be merged first."); Refresh(); return;
        }
        var item = Hit(x, y); if (item == null) return;
        PartsList.SelectedItem = item; Preview.CutPreview = null; replaced = [item.Part.Area]; dragging = true; MoveEdit(x, y);
    }
    private void MoveEdit(int x, int y)
    {
        if (Result == null || pending != null) return;
        if (protectionStart is { } start)
        {
            x = Math.Clamp(x, 0, source.Width - 1); y = Math.Clamp(y, 0, source.Height - 1);
            Preview.ProtectionDraft = new(Math.Min(x, start.X), Math.Min(y, start.Y), Math.Abs(x - start.X) + 1, Math.Abs(y - start.Y) + 1); Refresh(); return;
        }
        if (ToolBox.SelectedIndex is not (1 or 2 or 3)) return;
        var area = dragging ? Preview.CutPreview?.Area ?? replaced?.FirstOrDefault() : Hit(x, y)?.Part.Area;
        if (area == null || ToolBox.SelectedIndex == 3 && !dragging) return;
        bool vertical = ToolBox.SelectedIndex == 3 ? Preview.CutPreview!.Vertical : ToolBox.SelectedIndex == 1;
        int position = ((vertical ? x : y) + 1) & ~1;
        bool invalid = false; errorMessage = null;
        Preview.CutOpenings = [];
        try
        {
            var cut = ModulePartitions.Cut(area.Value, vertical, position, Preview.ProtectedRegions);
            Preview.CutOpenings = ModuleMapSplit.SharedOpenings(source, cut.A, cut.B, vertical).SelectMany(pair => new[] { pair.A, pair.B }).ToArray();
        }
        catch (InvalidDataException e) { invalid = true; errorMessage = e.Message; }
        Preview.CutPreview = new(area.Value, vertical, position, invalid); Refresh();
    }
    private async Task EndEditAsync(int x, int y)
    {
        if (!dragging || Result == null || pending != null) return;
        if (protectionStart != null)
        {
            MoveEdit(x, y); Remember(); if (Preview.ProtectionDraft is { } region) protection.Add(region);
            CancelDraft(); return;
        }
        if (ToolBox.SelectedIndex is 1 or 2 or 3) MoveEdit(x, y);
        dragging = false;
        if (replaced == null) return;
        ModuleArea[] replacements;
        try
        {
            if (ToolBox.SelectedIndex == 4) replacements = [ModulePartitions.Merge(replaced[0], replaced[1])];
            else
            {
                var draft = Preview.CutPreview!;
                var cut = ModulePartitions.Cut(draft.Area, draft.Vertical, draft.Position, Preview.ProtectedRegions);
                replacements = [cut.A, cut.B];
            }
        }
        catch (InvalidDataException error) { Reject(error.Message); return; }
        var baseline = Current(); var affected = replaced;
        var areas = Result.Parts.Select(p => p.Area).Except(affected).Concat(replacements).OrderBy(a => a.Y).ThenBy(a => a.X).ToArray();
        using var job = new CancellationTokenSource(); pending = job; Refresh();
        try
        {
            var result = await Task.Run(() => ModuleMapSplit.Build(source, areas, job.Token), job.Token);
            if (closed || job.IsCancellationRequested) return;
            var invalid = result.Parts.FirstOrDefault(p => replacements.Contains(p.Area) && !p.Valid);
            if (invalid != null) { Reject(invalid.Problem!.Message); return; }
            // A new boundary can change a neighbour's ports too; never silently break a previously usable block.
            var broken = result.Parts.FirstOrDefault(p => !p.Valid && baseline.Result.Parts.Any(old => old.Area == p.Area && old.Valid));
            if (broken != null) { Reject(broken.Problem!.Message); return; }
            Remember(); var selection = baseline.Selected;
            if (affected.Any(a => selection.Contains(ModulePartitions.Key(a)))) foreach (var area in replacements) selection.Add(ModulePartitions.Key(area));
            CancelDraft(); Present(result, selection, replacements[0]);
        }
        catch (OperationCanceledException) { CancelDraft(); }
        catch (Exception error) when (error is InvalidDataException or InvalidOperationException or OverflowException)
        { Reject(error.Message); }
        finally { pending = null; if (!closed) Refresh(); }
    }
    private void Reject(string message)
    {
        if (Preview.CutPreview is { } cut) Preview.CutPreview = cut with { Invalid = true };
        errorMessage = message; Refresh();
    }
}
