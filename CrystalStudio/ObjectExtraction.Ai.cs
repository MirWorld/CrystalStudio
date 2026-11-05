using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CrystalStudio;

internal sealed record AiObjectRecognition(string Name, string Category, string Reason);

internal static class ExtractionAi
{
    internal static AiObjectRecognition Parse(string text, IReadOnlyList<string> categories)
    {
        // A model may wrap its JSON in a code fence or a short explanation.
        int start = text.IndexOf('{'), end = text.LastIndexOf('}');
        if (start < 0 || end < start) throw new InvalidDataException(LocalText.T("AI did not return valid object recognition JSON."));
        JsonObject json;
        try { json = JsonNode.Parse(text[start..(end + 1)]) as JsonObject ?? throw new JsonException(); }
        catch (JsonException) { throw new InvalidDataException(LocalText.T("AI did not return valid object recognition JSON.")); }
        string Read(string key) => json[key] is JsonValue value && value.TryGetValue<string>(out var result) ? result.Trim() : "";
        string name = Read("name"), category = Read("category"), reason = Read("reason");
        string? allowed = categories.FirstOrDefault(c => string.Equals(c, category, StringComparison.OrdinalIgnoreCase));
        if (name.Length is < 1 or > 120 || name.Any(char.IsControl) || allowed == null || reason.Length > 2000)
            throw new InvalidDataException(LocalText.T("AI returned an invalid object name or category. Please retry."));
        return new(name, allowed, reason);
    }

    internal static async Task<AiObjectRecognition> RecognizeAsync(AiClient client, AiEndpointSettings endpoint,
        ExtractionCandidate candidate, AssetStore assets, IReadOnlyList<string> categories, CancellationToken token)
    {
        var images = await Task.Run(() =>
        {
            string Render(IReadOnlyList<StampCell> cells, Rect? area = null)
            {
                using var bitmap = StampPreview.Create(cells, assets, 768, 640, token, area, true, true)
                    ?? throw new InvalidDataException(LocalText.T("Preview artwork did not load."));
                using var stream = new MemoryStream(); bitmap.Save(stream);
                return "data:image/png;base64," + Convert.ToBase64String(stream.ToArray());
            }
            var result = new List<string> { Render(candidate.Cells) };
            if (candidate.Context.Count > 0) result.Add(Render(candidate.Context, candidate.ContextArea));
            return result;
        }, token);
        string system = "你是传奇类 2D 游戏地图素材识别专家。第一张图是待识别的独立物体，第二张图（如有）仅为周围地图环境。" +
            "只识别第一张图的物体，不把周围物体当成目标。返回严格 JSON：{\"name\":\"简短具体的物体名称\",\"category\":\"允许的类别原文\",\"reason\":\"简短识别依据\"}。" +
            "category 必须从用户提供的类别中选择，保留类别原文；无法识别时使用 Unclassified。不要推测不存在的细节。" +
            (LocalText.Language == "zh-CN" ? "name 和 reason 使用简体中文。" : "Write name and reason in English.");
        var allowed = new JsonArray(categories.Select(c => (JsonNode?)JsonValue.Create(c)).ToArray()).ToJsonString();
        string user = $"允许的类别：{allowed}\n当前名称：{candidate.Name}\n当前类别：{candidate.Category}\n请根据图片矫正名称与类别。";
        return Parse(await client.TextAsync(endpoint, system, user, images, token), categories);
    }
}
