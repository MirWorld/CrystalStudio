using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CrystalStudio;

internal enum AiObjectCompleteness { Complete, Incomplete, Uncertain }
internal sealed record AiObjectRecognition(string Name, string Category, string Reason, AiObjectCompleteness Completeness);

internal static class ExtractionAi
{
    internal const string PromptVersion = "object-completeness-v1";
    private const string SystemPrompt = """
        你是传奇类等距/俯视 2D 游戏地图素材审核专家。任务是判断候选能否作为独立完整物件使用，然后矫正名称和类别。
        第一张图只包含本次提取的候选像素；第二张图（如有）是地图上下文，仅辅助判断归属和缺失部分。
        只审核第一张图的实际内容。不能把第二张图中母建筑或邻近物体的像素算入候选，也不能想象补齐缺失内容。
        当前名称、类别、模板匹配仅供参考，可能把建筑切片误标为建筑或屋顶，不是完整性证据。必须先判断完整性，再命名分类。

        completeness 必须为以下三者之一：
        - incomplete：明确只是较大物体的局部，缺少独立整体轮廓或必要结构。例如屋顶的一条竖向瓦片、屋面窄条、檐角、半片屋顶、建筑侧边残片；即使能认出传统建筑或瓦屋顶，也必须判为 incomplete，不能因成功命名而保留。
        - complete：可独立使用的完整物件，轮廓及结构在该游戏视角下自洽。例如完整房屋、帐篷、蒙古包、瞭望塔、树、石、柱、旗杆或路灯。正常透视、背面不可见和遮挡不等于残缺；完整屋顶轮廓也不能仅因看不到背面或墙体就判残缺。
        - uncertain：没有足够可见证据确认完整或残缺，例如像素无法辨清、效果/地形无法确定独立边界；保留给人工审核，禁止为了分类而猜测。
        细长、尺寸小、只有一张素材、透明留白或在画布边缘本身都不是剔除依据。完整的柱子、旗杆、树干，以及结构完整且能独立复用的墙段、栅栏段或桥段不能仅因细长或可拼接而判残缺。
        注意区分“完整模块”和“被截断的条状切片”：前者有合理的端点、结构和用途，后者仅是某母物体表面的一条局部纹理或缺少主体的边缘。
        reason 简述可见的完整性证据；判 incomplete 时指出缺少的主体或截断结构，判 uncertain 时说明不确定原因。不要把推测写成事实。

        仅返回严格 JSON，不要代码围栏或额外文字：
        {"name":"简短具体的物体或残片名称","category":"允许的类别原文","completeness":"complete 或 incomplete 或 uncertain","reason":"简短的完整性及识别依据"}
        category 必须从用户提供的类别中选择并保留原文；无法分类时使用 Unclassified。所有字段均为必填字符串。
        """;

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
        if (name.Length is < 1 or > 120 || name.Any(char.IsControl) || allowed == null || reason.Length is < 1 or > 2000)
            throw new InvalidDataException(LocalText.T("AI returned an invalid object name or category. Please retry."));
        var completeness = Read("completeness").ToLowerInvariant() switch
        {
            "complete" => AiObjectCompleteness.Complete,
            "incomplete" => AiObjectCompleteness.Incomplete,
            "uncertain" => AiObjectCompleteness.Uncertain,
            _ => throw new InvalidDataException(LocalText.T("AI did not return a valid completeness decision. Please retry."))
        };
        return new(name, allowed, reason, completeness);
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
        string system = SystemPrompt + (LocalText.Language == "zh-CN" ? "\nname 和 reason 使用简体中文。" : "\nWrite name and reason in English.");
        var allowed = new JsonArray(categories.Select(c => (JsonNode?)JsonValue.Create(c)).ToArray()).ToJsonString();
        var reference = new JsonObject { ["name"] = candidate.Name, ["category"] = candidate.Category }.ToJsonString();
        string user = $"允许的类别：{allowed}\n旧识别信息（不作为完整性证据）：{reference}\n请先审核第一张图的物件完整性，再返回名称、类别、completeness 和可见依据。";
        return Parse(await client.TextAsync(endpoint, system, user, images, token), categories);
    }
}
