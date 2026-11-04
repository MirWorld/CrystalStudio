namespace CrystalStudio;

internal static class OverviewBatch
{
    internal const int RowsPerBatch = 32;
    internal sealed record Result(SceneGroup Scene, int Sprites, double Depth, Atlas Texture,
        Dictionary<(int Layer, int Band), Part> Parts)
    {
        internal Dictionary<(Sprite Sprite, Rect Bounds), EffectPatch> Effects { get; init; } = [];
        internal bool HasBlends { get; } = Parts.Values.Any(p => p.HasBlends);
    }
    internal sealed record EffectPatch(Color Background, (RasterImage Image, Rect Bounds)[] Inputs, RasterImage Bitmap);
    internal sealed record Part(SceneGroup Model, Atlas Texture, Placement[] Placements, int Sprites)
    {
        internal bool HasBlends { get; } = Placements.Any(p => p.Blend);
    }
    internal readonly record struct Placement(int X, int Y, int Library, int Image, int Layer, double Order, bool Blend = false, int BlendId = -1)
    {
        internal bool Anchored => Layer is 1 or 2;
        internal bool Collision => Layer == 3;
        internal (int Library, int Image) TextureKey => Blend ? (-2 - Layer, BlendId) : (Library, Image);
    }


    internal static async Task<Result> BuildAsync(KeyValuePair<int, Cell>[] snapshot, int width, int height, int layers, int level,
        AssetStore? assets, CancellationToken token, Result? previous = null, int[]? bands = null, Color background = default, bool recomposite = false)
    {
        token.ThrowIfCancellationRequested();
        Array.Sort(snapshot, (a, b) => a.Key.CompareTo(b.Key));
        var placements = new List<Placement>();
        double layerDepth = (double)width * height + 1;
        for (int layer = 0; layer < 4; layer++)
        {
            if ((layers & (layer == 3 ? 40 : 1 << layer)) == 0) continue;
            foreach (var pair in snapshot)
            {
                token.ThrowIfCancellationRequested();
                int x = pair.Key % width, y = pair.Key / width; var cell = pair.Value;
                switch (layer)
                {
                    case 0:
                        if ((x & 1) == 0 && (y & 1) == 0 && (cell.BackImage & 0x1FFFFFFF) != 0)
                            placements.Add(new(x, y, cell.BackLibrary, (cell.BackImage & 0x1FFFFFFF) - 1, layer, pair.Key + .5));
                        break;
                    case 1:
                        if (cell.MiddleImage > 0) placements.Add(new(x, y, cell.MiddleLibrary, cell.MiddleImage - 1, layer, layerDepth + pair.Key + .5, MapSpriteDrawing.MiddleBlend(cell.MiddleFrames), MapSpriteDrawing.MiddleBlend(cell.MiddleFrames) ? pair.Key : -1));
                        break;
                    case 2:
                        for (int i = 0; i < cell.ForegroundImages.Count; i++)
                        {
                            var front = cell.ForegroundImages[i];
                            if ((front.Image & 0x7FFF) > 0) placements.Add(new(x, y, front.Library, MapPreview.FrontImage(cell, front, (layers & 16) != 0),
                                layer, layerDepth * layer + pair.Key + (i + 1.0) / (cell.ForegroundImages.Count + 1), MapSpriteDrawing.FrontBlend(front.Frames), MapSpriteDrawing.FrontBlend(front.Frames) ? unchecked(pair.Key * 256 + i) : -1));
                        }
                        break;
                    case 3:
                        int collisionMask = ((layers & 8) != 0 ? 0x20000000 : 0) | ((layers & 32) != 0 ? 0x40000000 : 0);
                        int type = CollisionMarkers.Type(cell.BackImage & collisionMask);
                        if (type != 0) placements.Add(new(x, y, -1, type, layer, layerDepth * layer + pair.Key + .5));
                        break;
                }
            }
        }
        if (previous != null && recomposite)
        {
            var edited = bands!.ToHashSet();
            foreach (var part in previous.Parts)
                if (!edited.Contains(part.Key.Band)) placements.AddRange(part.Value.Placements);
        }
        var groups = placements.GroupBy(p => (p.Layer, Band: p.Y / RowsPerBatch)).ToDictionary(g => g.Key, g => g.ToArray());
        Atlas texture;
        var effects = new Dictionary<(Sprite Sprite, Rect Bounds), EffectPatch>();
        Dictionary<(int Layer, int Band), Part> parts;
        if (previous == null)
        {
            texture = await PrepareAtlas(placements, assets, level, token, background, effects: effects);
            parts = groups.ToDictionary(g => g.Key, g => BuildPart(g.Value, texture, token));
        }
        else
        {
            texture = recomposite ? await PrepareAtlas(placements, assets, level, token, background, previous.Effects, effects, previous.Texture) : previous.Texture;
            parts = new(previous.Parts);
            var affected = recomposite ? bands!.Concat(placements.Where(p => p.Blend).Select(p => p.Y / RowsPerBatch))
                .Concat(previous.Parts.Where(p => p.Value.HasBlends).Select(p => p.Key.Band)).Distinct() : bands!;
            foreach (int band in affected) for (int layer = 0; layer < 4; layer++)
            {
                token.ThrowIfCancellationRequested(); var key = (layer, band);
                if (!groups.TryGetValue(key, out var items)) { parts.Remove(key); continue; }
                parts.TryGetValue(key, out var old);
                if (old != null && Unchanged(old, items, texture)) continue;
                // Existing sprite coordinates and materials are reusable. A new sprite
                // needs only a small atlas for this batch, never a repack of the map.
                var atlas = items.Any(p => p.Blend) ? texture : old?.Texture ?? texture;
                if (!items.All(p => atlas.Sprites.ContainsKey(p.TextureKey)))
                    atlas = items.All(p => texture.Sprites.ContainsKey(p.TextureKey)) ? texture :
                        await PrepareAtlas(items, assets, level, token, background);
                parts[key] = BuildPart(items, atlas, token);
            }
        }
        var scene = new SceneGroup();
        foreach (var pair in parts.OrderBy(p => p.Key.Layer).ThenBy(p => p.Key.Band)) scene.Children.Add(pair.Value.Model);
        
        return new(scene, parts.Values.Sum(p => p.Sprites), layerDepth * 4, texture, parts) { Effects = effects };
    }

    private static bool Unchanged(Part old, Placement[] items, Atlas texture)
    {
        if (old.Placements.Length != items.Length) return false;
        for (int i = 0; i < items.Length; i++)
        {
            var before = old.Placements[i]; var after = items[i];
            if (before != after) return false;
            if (after.Blend && (!old.Texture.Sprites.TryGetValue(before.TextureKey, out var original) ||
                !texture.Sprites.TryGetValue(after.TextureKey, out var updated) || !ReferenceEquals(original.Bitmap, updated.Bitmap))) return false;
        }
        return true;
    }

    private static async Task<Atlas> PrepareAtlas(IEnumerable<Placement> placements, AssetStore? assets, int level, CancellationToken token, Color background,
        Dictionary<(Sprite Sprite, Rect Bounds), EffectPatch>? previousEffects = null,
        Dictionary<(Sprite Sprite, Rect Bounds), EffectPatch>? effects = null, Atlas? previousAtlas = null)
    {
        var sprites = assets == null ? new Dictionary<(int Library, int Image), Sprite>() :
            await assets.PrepareOverviewAsync(placements.Where(p => !p.Collision).Select(p => (p.Library, p.Image)), level, token);
        foreach (int type in placements.Where(p => p.Collision).Select(p => p.Image).Distinct())
        {
            var image = CollisionMarkers.Texture(type);
            sprites[(-1, type)] = new(image, 0, 0, 48, 32);
        }
        token.ThrowIfCancellationRequested();
        if (placements.Any(p => p.Blend))
        {
            // Only sprites near an effect can contribute to its background. Building
            // Sprite drawings for the entire map here makes a tiny edit scale with every
            // ground tile, even when the map contains only a handful of lights.
            const int bucketWidth = 16 * 48, bucketHeight = 16 * 32;
            var effectBuckets = new HashSet<(int X, int Y)>();
            foreach (var effect in placements.Where(p => p.Blend))
            {
                token.ThrowIfCancellationRequested();
                if (!sprites.TryGetValue((effect.Library, effect.Image), out var sprite)) continue;
                var bounds = MapSpriteDrawing.Bounds(sprite, effect.X, effect.Y, effect.Library, effect.Image,
                    effect.Anchored, effect.Layer == 2, true);
                for (int y = (int)Math.Floor(bounds.Top / bucketHeight); y <= (int)Math.Floor(bounds.Bottom / bucketHeight); y++)
                    for (int x = (int)Math.Floor(bounds.Left / bucketWidth); x <= (int)Math.Floor(bounds.Right / bucketWidth); x++)
                        effectBuckets.Add((x, y));
            }
            bool NearEffect(Rect bounds)
            {
                for (int y = (int)Math.Floor(bounds.Top / bucketHeight); y <= (int)Math.Floor(bounds.Bottom / bucketHeight); y++)
                    for (int x = (int)Math.Floor(bounds.Left / bucketWidth); x <= (int)Math.Floor(bounds.Right / bucketWidth); x++)
                        if (effectBuckets.Contains((x, y))) return true;
                return false;
            }
            var earlier = new List<SceneImage>();
            long cachedBytes = 0;
            foreach (var p in placements.OrderBy(p => p.Order))
            {
                token.ThrowIfCancellationRequested();
                if (!sprites.TryGetValue((p.Library, p.Image), out var sprite)) continue;
                var bounds = MapSpriteDrawing.Bounds(sprite, p.X, p.Y, p.Library, p.Image, p.Anchored, p.Layer == 2, p.Blend);
                if (!p.Blend && !NearEffect(bounds)) continue;
                if (p.Blend)
                {
                    var inputs = earlier.Where(d => d.Rect.Intersects(bounds)).Select(d => (d.ImageSource, d.Rect)).ToArray();
                    var key = (sprite, bounds);
                    RasterImage patch;
                    if (previousEffects != null && previousEffects.TryGetValue(key, out var previous) &&
                        previous.Background == background && inputs.AsSpan().SequenceEqual(previous.Inputs)) patch = previous.Bitmap;
                    else
                    {
                        var behind = new SceneGroup();
                        behind.Children.Add(new SceneRectangle(new ImmutableSolidColorBrush(background), null, bounds));
                        foreach (var input in inputs) behind.Children.Add(new SceneImage(input.ImageSource, input.Rect));
                        patch = MapSpriteDrawing.Additive(sprite, bounds, behind);
                    }
                    // The current immutable result owns this cache; old row meshes do not
                    // retain background dependency lists from earlier edits.
                    cachedBytes += (long)inputs.Length * 40 + (long)patch.PixelWidth * patch.PixelHeight * 4;
                    if (effects != null && cachedBytes <= 32L * 1024 * 1024) effects[key] = new(background, inputs, patch);
                    sprites[p.TextureKey] = sprite with { Bitmap = patch };
                    earlier.Add(new SceneImage(patch, bounds));
                }
                else earlier.Add(new SceneImage(sprite.Bitmap, bounds));
            }
        }
        if (previousAtlas != null && sprites.All(p => previousAtlas.Sprites.TryGetValue(p.Key, out var old) && old == p.Value))
            return previousAtlas;
        return PackAtlas(sprites, token);
    }

    private static Part BuildPart(Placement[] placements, Atlas atlas, CancellationToken token)
    {
        var scene = new SceneGroup { RetainRaster = true }; int count = 0;
        var quads = new List<(Rect Source, Rect Destination)>();
        foreach (var placement in placements)
        {
            token.ThrowIfCancellationRequested();
            if (!atlas.Sprites.TryGetValue(placement.TextureKey, out var sprite)) continue;
            var bounds = MapSpriteDrawing.Bounds(sprite, placement.X, placement.Y, placement.Library, placement.Image,
                placement.Anchored, placement.Layer == 2, placement.Blend);
            quads.Add((atlas.Rectangles[placement.TextureKey], bounds)); count++;
        }
        if (quads.Count > 0) scene.Children.Add(new SceneAtlasBatch(atlas.Bitmap, quads.ToArray()));
        return new(scene, atlas, placements, count);
    }

    internal sealed record Atlas(RasterImage Bitmap, Dictionary<(int Library, int Image), Rect> Rectangles,
        Dictionary<(int Library, int Image), Sprite> Sprites);
    private static Atlas PackAtlas(Dictionary<(int Library, int Image), Sprite> sprites, CancellationToken token)
    {
        var ordered = sprites.OrderByDescending(pair => pair.Value.Bitmap.PixelHeight).ToArray();
        int size = 32;
        var rectangles = new Dictionary<(int Library, int Image), Rect>(); int usedHeight;
        while (true)
        {
            rectangles.Clear(); int x = 0, y = 0, row = 0; bool fits = true;
            foreach (var pair in ordered)
            {
                int w = pair.Value.Bitmap.PixelWidth, h = pair.Value.Bitmap.PixelHeight;
                if (w + 2 > size) { fits = false; break; }
                if (x + w + 2 > size) { x = 0; y += row; row = 0; }
                if (y + h + 2 > size) { fits = false; break; }
                rectangles[pair.Key] = new(x + 1, y + 1, w, h); x += w + 2; row = Math.Max(row, h + 2);
            }
            usedHeight = Math.Max(1, y + row);
            if (fits) break;
            if (size >= 4096)
            {
                bool changed = false;
                foreach (var pair in sprites.ToArray())
                    if (pair.Value.Bitmap.PixelWidth > 1 || pair.Value.Bitmap.PixelHeight > 1)
                    { sprites[pair.Key] = ShrinkTexture(pair.Value, token); changed = true; }
                if (!changed) throw new InvalidOperationException("Too many distinct sprites for one overview atlas.");
                ordered = sprites.OrderByDescending(pair => pair.Value.Bitmap.PixelHeight).ToArray();
                size = 32; continue;
            }
            size *= 2;
        }
        int height = 1; while (height < usedHeight) height *= 2;
        byte[] pixels = new byte[size * height * 4];
        foreach (var pair in ordered)
        {
            token.ThrowIfCancellationRequested(); var rect = rectangles[pair.Key];
            var source = pair.Value.Bitmap.Format == RasterFormats.Bgra32 ? pair.Value.Bitmap :
                new ConvertedRasterImage(pair.Value.Bitmap, RasterFormats.Bgra32, null, 0);
            int x = (int)rect.X, y = (int)rect.Y, w = (int)rect.Width, h = (int)rect.Height;
            source.CopyPixels(new PixelRect(0, 0, w, h), pixels, size * 4, (y * size + x) * 4);
            for (int row = y; row < y + h; row++)
            {
                pixels.AsSpan((row * size + x) * 4, 4).CopyTo(pixels.AsSpan((row * size + x - 1) * 4, 4));
                pixels.AsSpan((row * size + x + w - 1) * 4, 4).CopyTo(pixels.AsSpan((row * size + x + w) * 4, 4));
            }
            pixels.AsSpan((y * size + x - 1) * 4, (w + 2) * 4).CopyTo(pixels.AsSpan(((y - 1) * size + x - 1) * 4, (w + 2) * 4));
            pixels.AsSpan(((y + h - 1) * size + x - 1) * 4, (w + 2) * 4).CopyTo(pixels.AsSpan(((y + h) * size + x - 1) * 4, (w + 2) * 4));
        }
        var bitmap = RasterImage.Create(size, height, 96, 96, RasterFormats.Bgra32, null, pixels, size * 4);
        
        return new(bitmap, rectangles, sprites);
    }

    private static Sprite ShrinkTexture(Sprite sprite, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        int w = sprite.Bitmap.PixelWidth, h = sprite.Bitmap.PixelHeight;
        var source = sprite.Bitmap.Format == RasterFormats.Bgra32 ? sprite.Bitmap : new ConvertedRasterImage(sprite.Bitmap, RasterFormats.Bgra32, null, 0);
        byte[] input = new byte[w * h * 4]; source.CopyPixels(input, w * 4, 0);
        int width = (w + 1) / 2, height = (h + 1) / 2;
        byte[] pixels = OverviewPixels.Reduce(input, w, h, width, height, token);
        var bitmap = RasterImage.Create(width, height, 96, 96, RasterFormats.Bgra32, null, pixels, width * 4);
         return new(bitmap, sprite.X, sprite.Y, sprite.Width, sprite.Height);
    }
}
