using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
namespace CrystalStudio;

public partial class MainWindow
{
    private readonly Button stampProperties = new() { Content = "Stamp Categories\u2026", IsVisible = false, Margin = new Thickness(4) };

    private void InitializeStampEditing(Grid canvas)
    {
        canvas.RowDefinitions.Insert(1, new RowDefinition { Height = GridLength.Auto });
        foreach (Control child in canvas.Children) if (child != mapTabs) Grid.SetRow(child, 2);
        Grid.SetRow(stampProperties, 1);
        stampProperties.HorizontalAlignment = HorizontalAlignment.Left;
        stampProperties.Click += (_, _) => EditStampCategories();
        canvas.Children.Add(stampProperties);
        AssetList.DoubleTapped += (_, e) =>
        {
            if (e.Source is AvaloniaObject source && FindStampItem(source) is AssetChoice { StampPath: { } path }) OpenStampEditor(path);
        };
        categoryTree?.AddHandler(InputElement.DoubleTappedEvent, new EventHandler<TappedEventArgs>((_, e) =>
        {
            if (categoryIndex != null && e.Source is AvaloniaObject source && FindStampItem(source) is StampCategoryNode { Stamp: { } stamp })
                OpenStampEditor(StampCategories.Resolve(categoryIndex.ObjectRoot, stamp.File));
        }));
    }

    private static object? FindStampItem(AvaloniaObject source)
    {
        while (source != null)
        {
            if (source is Button) return null;
            if (source is ListBoxItem or TreeViewItem) return ((Control)source).DataContext;
            source = (source as Visual)?.GetVisualParent();
        }
        return null;
    }

    private void NewStampClick(object sender, RoutedEventArgs e)
    {
        if (busy) return;
        var copied = Viewport.CopiedRegionSize;
        int width = Math.Max(64, (int)copied.Width + 16), height = Math.Max(64, (int)copied.Height + 16);
        if (width > 4096 || height > 4096) { MessageBox.Show(this, "The copied region exceeds the stamp editor limit."); return; }
        AddMapTab(new MapDocument(width, height));
        var map = activeMap!;
        map.IsStamp = true;
        map.Name = $"Untitled stamp {untitledNumber}";
        map.StampOriginX = -(width / 2 & ~1); map.StampOriginY = -(height / 2 & ~1);
        stampProperties.IsVisible = true;
        Viewport.Tool = EditorTool.Select;
        Viewport.Fit(); Viewport.Focus();
        UpdateLabels(); UpdateMapTabLabels();
        Status.Text = "New stamp \u00b7 Paint cells or press Ctrl+V and click to paste copied map data \u00b7 Ctrl+S to save";
    }
    private void OpenStampEditor(string path)
    {
        if (busy) return;
        foreach (TabItem tab in mapTabs.Items)
            if (tab.Tag is OpenMapTab { IsStamp: true } existing && string.Equals(existing.Path, path, StringComparison.OrdinalIgnoreCase)) { mapTabs.SelectedItem = tab; return; }
        try
        {
            var cells = CellCodec.LoadStamp(path);
            int minX = cells.Count == 0 ? 0 : cells.Min(c => c.X), minY = cells.Count == 0 ? 0 : cells.Min(c => c.Y);
            int width = cells.Count == 0 ? 32 : checked(cells.Max(c => c.X) - minX + 17);
            int height = cells.Count == 0 ? 32 : checked(cells.Max(c => c.Y) - minY + 17);
            if (width > 4096 || height > 4096) throw new InvalidDataException("Stamp bounds exceed the editor limit.");
            var document = new MapDocument(width, height);
            foreach (var cell in cells) document.Set(cell.X - minX + 8, cell.Y - minY + 8, cell.Cell.Copy());
            AddMapTab(document, path);
            activeMap!.IsStamp = true;
            activeMap.StampOriginX = minX - 8; activeMap.StampOriginY = minY - 8;
            string? indexPath = StampCategories.FindIndex();
            if (indexPath != null)
            {
                var index = StampCategories.Read(indexPath);
                string? relative = null;
                try { relative = StampCategories.CatalogueFile(index.ObjectRoot, path, stampFolder ?? StampCatalog.FindFolder()); }
                catch (InvalidDataException) { /* Imported stamps may be outside both Objects folders. */ }
                activeMap.Category = index.Objects.FirstOrDefault(s => string.Equals(s.File, relative, StringComparison.OrdinalIgnoreCase));
            }
            stampProperties.IsVisible = true;
            Viewport.Tool = EditorTool.Select;
            Viewport.Fit();
            UpdateMapTabLabels();
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Unable to open stamp"); }
    }

    private void EditStampCategories()
    {
        if (activeMap is not { IsStamp: true } map || busy) return;
        StampCategoryIndex index;
        try { index = StampCategories.Read(StampCategories.FindIndex() ?? throw new FileNotFoundException("The stamp category catalogue was not found.")); }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Unable to read categories"); return; }
        var category = map.Category;
        var known = index.Objects.Concat(category == null ? [] : new[] { category }).ToList();
        static List<string> Options(IEnumerable<string?> values) => values.Where(v => !string.IsNullOrWhiteSpace(v)).Select(v => v!).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(v => v, StringComparer.OrdinalIgnoreCase).ToList();
        var panel = new StackPanel { Margin = new Thickness(16) };
        void Label(string text) => panel.Children.Add(new TextBlock { Text = text, Margin = new Thickness(0, 8, 0, 3) });
        Label("Name");
        var name = new TextBox { Text = category?.Name ?? (map.Path == null ? map.Name : Path.GetFileNameWithoutExtension(map.Path)) }; panel.Children.Add(name);
        ComboBox Single(string label, IEnumerable<string?> options, string? selected, bool optional = true)
        {
            Label(label);
            var values = Options(options);
            if (optional) values.Insert(0, "");
            var input = new ComboBox { ItemsSource = values, MinHeight = 26, SelectedItem = selected ?? "" };
            panel.Children.Add(input); return input;
        }
        StampCategoryPicker Multiple(string label, IEnumerable<string?> options, IEnumerable<string>? selected)
        {
            Label(label);
            var input = new StampCategoryPicker(Options(options), selected ?? []); panel.Children.Add(input); return input;
        }
        var type = Single("Type", known.Select(s => s.Type), category?.Type);
        var subtype = Single("Subtype", known.Select(s => s.Subtype), category?.Subtype);
        var regions = Multiple("Regions", known.SelectMany(s => s.Regions), category?.Regions);
        var groups = Multiple("Region Groups", known.SelectMany(s => s.RegionGroups), category?.RegionGroups);
        var environments = Multiple("Environments", known.SelectMany(s => s.Environments), category?.Environments);
        var tags = Multiple("Tags", known.SelectMany(s => s.Tags), category?.Tags);
        var direction = Single("Direction", known.Select(s => s.Direction), category?.Direction);
        var directionStatus = Single("Direction Status", known.Select(s => s.DirectionStatus).Append("unknown"), category?.DirectionStatus ?? "unknown", false);
        var review = Single("Review Status", known.Select(s => s.ReviewStatus).Append("unreviewed"), category?.ReviewStatus ?? "unreviewed", false);
        var dialog = new EditorWindow { Owner = this, Title = "Stamp Categories", Width = 460, Height = 650, WindowStartupLocation = WindowStartupLocation.CenterOwner, Content = new ScrollViewer { Content = panel } };
        var apply = new Button { Content = "Apply", Margin = new Thickness(0, 16, 0, 0) }; panel.Children.Add(apply);
        apply.Click += (_, _) =>
        {
            static string? Value(ComboBox input) => input.SelectedItem is string { Length: > 0 } text ? text : null;
            if (string.IsNullOrWhiteSpace(name.Text)) { MessageBox.Show(dialog, "Enter a stamp name."); return; }
            map.Category = new(category?.Id ?? "", category?.File ?? "", name.Text.Trim(), Value(type), Value(subtype), regions.SelectedValues, groups.SelectedValues, environments.SelectedValues, tags.SelectedValues, Value(direction), Value(directionStatus) ?? "unknown", null, false, Value(review) ?? "unreviewed");
            map.CategoryDirty = true; UpdateLabels(); UpdateMapTabLabels(); dialog.Close();
        };
        dialog.ShowDialog();
    }

    private bool SaveStampEditor(bool saveAs)
    {
        var map = activeMap!;
        try
        {
            string indexPath = StampCategories.FindIndex() ?? throw new FileNotFoundException("The stamp category catalogue was not found.");
            var index = StampCategories.Read(indexPath);
            bool creating = saveAs || map.Path == null;
            string target;
            if (creating)
            {
                var picker = new SaveFileDialog
                {
                    Filter = "Crystal object (*.X)|*.X",
                    FileName = map.Path == null ? "New stamp.X" : Path.GetFileNameWithoutExtension(map.Path) + " copy.X",
                    InitialDirectory = map.Path == null ? stampFolder ?? StampCatalog.FindFolder() ?? index.ObjectRoot : Path.GetDirectoryName(map.Path),
                    DefaultExt = ".X", AddExtension = true
                };
                if (picker.ShowDialog(this) != true) return false;
                target = picker.FileName;
                if (File.Exists(target)) throw new IOException("A new stamp requires a new object filename.");
            }
            else target = map.Path!;
            string relative = StampCategories.CatalogueFile(index.ObjectRoot, target, stampFolder ?? StampCatalog.FindFolder());
            StampCategories.Resolve(index.ObjectRoot, relative);
            var json = JsonNode.Parse(File.ReadAllText(indexPath))!.AsObject();
            var entries = json["objects"]!.AsArray();
            var existing = entries.FirstOrDefault(n => string.Equals(n?["file"]?.GetValue<string>(), relative, StringComparison.OrdinalIgnoreCase));
            var category = map.Category ?? new CategorizedStamp("", "", Path.GetFileNameWithoutExtension(target), null, null, [], [], [], [], null, "unknown", null, false, "unreviewed");
            category = category with { Id = existing?["id"]?.GetValue<string>() ?? Path.ChangeExtension(relative, null), File = relative, Preview = null, PreviewComplete = false };
            if (existing == null && index.Objects.Any(s => string.Equals(s.Id, category.Id, StringComparison.OrdinalIgnoreCase))) throw new InvalidDataException("The new catalogue ID already exists.");
            var updated = JsonSerializer.SerializeToNode(category, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase })!.AsObject();
            if (existing is JsonObject entry) foreach (var property in updated) entry[property.Key] = property.Value?.DeepClone();
            else entries.Add(updated);
            var cells = CellCodec.CropStamp(Viewport.Document.Cells.Select(pair => new StampCell(pair.Key % Viewport.Document.Width + map.StampOriginX, pair.Key / Viewport.Document.Width + map.StampOriginY, pair.Value)));
            string catalogueTarget = StampCategories.Resolve(index.ObjectRoot, relative);
            bool separateCopy = !string.Equals(Path.GetFullPath(target), catalogueTarget, StringComparison.OrdinalIgnoreCase);
            byte[]? catalogueOriginal = separateCopy && File.Exists(catalogueTarget) ? File.ReadAllBytes(catalogueTarget) : null;
            if (creating && (existing != null || separateCopy && File.Exists(catalogueTarget))) throw new IOException("That object filename already exists in the catalogue. Choose a different name.");
            byte[]? original = File.Exists(target) ? File.ReadAllBytes(target) : null;
            CellCodec.SaveStamp(target, cells);
            try
            {
                if (separateCopy) CellCodec.SaveStamp(catalogueTarget, cells);
                AtomicFile.Write(indexPath, stream => JsonSerializer.Serialize(stream, json, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch
            {
                if (original != null) AtomicFile.Write(target, stream => stream.Write(original)); else File.Delete(target);
                if (separateCopy) { if (catalogueOriginal != null) AtomicFile.Write(catalogueTarget, stream => stream.Write(catalogueOriginal)); else if (File.Exists(catalogueTarget)) File.Delete(catalogueTarget); }
                throw;
            }
            map.Path = target; map.Category = category; map.CategoryDirty = false; Viewport.History.Saved();
            categoryIndex = StampCategories.Read(indexPath);
            selectedStampPath = null; selectedStampPreview = null;
            _ = DiscoverStampsAsync();
            if (CategoryPalette) BuildPage();
            UpdateLabels(); UpdateMapTabLabels(); Status.Text = $"Saved {Path.GetFileName(target)} and catalogue";
            return true;
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Save stamp failed"); return false; }
    }
}
