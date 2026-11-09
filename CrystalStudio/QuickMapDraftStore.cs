using System.IO;

namespace CrystalStudio;

internal sealed record QuickMapDraftImage(int Width, int Height, RasterFormat Format, byte[] Pixels)
{
    internal static QuickMapDraftImage? Capture(RasterImage? image)
    {
        if (image == null) return null;
        var pixels = new byte[checked(image.PixelWidth * image.PixelHeight * 4)]; image.CopyPixels(pixels, image.PixelWidth * 4, 0);
        return new(image.PixelWidth, image.PixelHeight, image.Format, pixels);
    }
    internal void Validate()
    {
        if (Width is < 1 or > 640 || Height is < 1 or > 400 || !Enum.IsDefined(Format) || Pixels == null || Pixels.Length != checked(Width * Height * 4))
            throw new InvalidDataException("Invalid draft preview dimensions.");
    }
    internal RasterImage Restore() { Validate(); return RasterImage.Create(Width, Height, 96, 96, Format, null, Pixels, Width * 4); }
}

internal sealed record QuickMapDraftCandidate(MapGenerationSettings Generation, Dictionary<string, string> Parameters, MapDesignSettings Design,
    bool Favorite, QuickMapDraftImage? Thumbnail, QuickMapDraftImage? Comparison, MapCandidateMetrics? Metrics);
internal sealed record QuickMapDraft(int Version, DateTime SavedUtc, MapCreationSettings Settings, MapCreationRecipe Recipe, string PresetName,
    List<QuickMapDraftCandidate> Candidates, int? Selected, int? CompareA, int? CompareB)
{
    public bool LayoutLocked { get; init; }
}

internal sealed class QuickMapDraftStore(string filePath)
{
    internal string FilePath { get; } = Path.GetFullPath(filePath);
    internal bool Exists => File.Exists(FilePath);
    internal const int MaximumBytes = 16 * 1024 * 1024;

    internal static QuickMapDraft Capture(MapCreationSession session, MapCreationRecipe recipe)
    {
        return new(1, DateTime.UtcNow, session.Settings, recipe with { Parameters = new(session.Parameters), Design = session.Design.Copy(),
            DecorationSeed = session.DecorationVariant?.Seed ?? session.RequestedDecorationSeed, DecorationNames = session.DecorationNames == null ? null : [..session.DecorationNames] }, session.PresetName,
            session.Candidates.Items.Select(candidate => new QuickMapDraftCandidate(candidate.Generation, new(candidate.Parameters), candidate.CopyDesign() ?? new(), candidate.IsFavorite,
                QuickMapDraftImage.Capture(candidate.Thumbnail), QuickMapDraftImage.Capture(candidate.Comparison), candidate.Metrics)).ToList(),
            ValidIndex(session.SelectedCandidateIndex), ValidIndex(session.CompareAIndex), ValidIndex(session.CompareBIndex)) { LayoutLocked = session.StructureLocked || session.RequestedLock };
        int? ValidIndex(int? index) => index.HasValue && index >= 0 && index < session.Candidates.Items.Count ? index : null;
    }

    internal void Save(QuickMapDraft draft)
    {
        Validate(draft); var bytes = StudioJson.SerializeToUtf8Bytes(draft);
        if (bytes.Length > MaximumBytes) throw new InvalidDataException(LocalText.T("The creation draft exceeds the safe size limit. The previous draft was kept."));
        AtomicFile.Write(FilePath, stream => stream.Write(bytes));
    }
    internal QuickMapDraft Load()
    {
        if (new FileInfo(FilePath).Length > MaximumBytes) throw new InvalidDataException("Creation draft is too large.");
        var draft = StudioJson.Deserialize<QuickMapDraft>(File.ReadAllText(FilePath)) ?? throw new InvalidDataException("Empty creation draft.");
        Validate(draft); return draft;
    }
    internal void Clear() { if (Exists) File.Delete(FilePath); }

    internal static void Validate(QuickMapDraft draft)
    {
        if (draft.Version != 1 || draft.Settings == null || draft.Recipe == null || draft.PresetName == null || draft.PresetName.Length > 80 || draft.Candidates == null || draft.Candidates.Count > QuickMapCandidates.Capacity)
            throw new InvalidDataException("Invalid creation draft.");
        MapRecipeRestore.Validate(draft.Recipe); MapCreationPaths.ValidateName(draft.Settings.Name);
        if (draft.Settings.Kind != draft.Recipe.Kind || draft.Settings.Width != draft.Recipe.Width || draft.Settings.Height != draft.Recipe.Height)
            throw new InvalidDataException("Draft settings and recipe do not match.");
        foreach (int? index in new[] { draft.Selected, draft.CompareA, draft.CompareB })
            if (index.HasValue && (index < 0 || index >= draft.Candidates.Count)) throw new InvalidDataException("Invalid draft candidate selection.");
        foreach (var candidate in draft.Candidates)
        {
            if (candidate == null || candidate.Generation == null || candidate.Design == null || candidate.Parameters == null) throw new InvalidDataException("Invalid draft candidate.");
            MapRecipeRestore.Validate(draft.Recipe with { Kind = candidate.Generation.Kind, Width = candidate.Generation.Width, Height = candidate.Generation.Height,
                Parameters = candidate.Parameters, Design = candidate.Design, DecorationSeed = null });
            candidate.Thumbnail?.Validate(); candidate.Comparison?.Validate();
        }
    }

    internal static void Restore(QuickMapDraft draft, MapCreationSession session, bool artworkMatches)
    {
        Validate(draft);
        var candidates = new List<QuickMapCandidate>();
        try
        {
            foreach (var saved in draft.Candidates)
            {
                var settings = new MapCreationSettings(saved.Generation.Kind, saved.Generation.Width, saved.Generation.Height);
                var candidate = new QuickMapCandidate(settings, saved.Parameters, artworkMatches ? saved.Thumbnail?.Restore() : null, saved.Design) { IsFavorite = saved.Favorite };
                candidates.Add(candidate);
                candidate.Comparison = artworkMatches ? saved.Comparison?.Restore() : null;
                candidate.RestoreDesignContext(saved.Design);
            }
        }
        catch { foreach (var candidate in candidates) { candidate.Thumbnail?.Dispose(); candidate.Comparison?.Dispose(); } throw; }
        session.Update(draft.Settings with { SaveImmediately = false }, draft.Recipe.Parameters, draft.PresetName);
        session.Design = draft.Recipe.Design?.Copy() ?? new(); session.Candidates.Replace(candidates);
        session.SelectedCandidateIndex = draft.Selected; session.CompareAIndex = draft.CompareA; session.CompareBIndex = draft.CompareB;
        session.RequestedDecorationSeed = artworkMatches ? draft.Recipe.DecorationSeed : null;
        session.DecorationNames = artworkMatches ? draft.Recipe.DecorationNames?.ToList() : null;
        session.RequestedLock = artworkMatches && draft.LayoutLocked;
        session.ExpectedRecipeParameters = new Dictionary<string, string>(draft.Recipe.Parameters);
        session.DraftSaved = true;
    }
}
