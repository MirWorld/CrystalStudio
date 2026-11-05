using System.IO;
using System.Text.Json.Nodes;

namespace CrystalStudio;

public enum AiRole { Vision, Image, Chat }

public sealed record AiEndpointSettings(string Provider = "openai", string BaseUrl = "https://api.openai.com/v1",
    string ApiKey = "", string Model = "gpt-4o", string Path = "", string Auth = "bearer",
    string ExtraBody = "", string ResponsePath = "", int TimeoutSeconds = 180, string SizeFormat = "x")
{
    public void Validate(AiRole role)
    {
        if (!AiProviders.All.Any(p => p.Id == Provider && (role == AiRole.Image || !p.ImageOnly)))
            throw new InvalidDataException(LocalText.T("This provider does not support the selected AI role."));
        if (!Uri.TryCreate(BaseUrl, UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http") || !string.IsNullOrEmpty(uri.UserInfo))
            throw new InvalidDataException(LocalText.T("Enter a valid HTTP or HTTPS AI service address."));
        if (Provider != "sdwebui" && Provider != "custom" && string.IsNullOrWhiteSpace(Model))
            throw new InvalidDataException(LocalText.T("Enter an AI model name."));
        if (TimeoutSeconds < 1 || TimeoutSeconds > 1800)
            throw new InvalidDataException(LocalText.T("AI timeout must be between 1 and 1800 seconds."));
        if (Auth is not ("bearer" or "query" or "x-api-key" or "raw" or "none"))
            throw new InvalidDataException(LocalText.T("Unknown AI authentication method."));
        if (!string.IsNullOrWhiteSpace(Path) && (Path.StartsWith("//", StringComparison.Ordinal) || Uri.TryCreate(Path, UriKind.Absolute, out _)))
            throw new InvalidDataException(LocalText.T("AI request path must be relative to the service address."));
        if (SizeFormat is not ("x" or "star" or "pixel")) throw new InvalidDataException("Unknown image size format.");
        if (!string.IsNullOrWhiteSpace(ExtraBody))
        {
            try { if (JsonNode.Parse(ExtraBody) is not JsonObject) throw new InvalidDataException(LocalText.T("AI extra body must be a JSON object.")); }
            catch (System.Text.Json.JsonException) { throw new InvalidDataException(LocalText.T("AI extra body must be a JSON object.")); }
        }
    }
}

public sealed record AiSettings(AiEndpointSettings? Vision = null, AiEndpointSettings? Image = null, AiEndpointSettings? Chat = null)
{
    public AiEndpointSettings For(AiRole role) => (role switch { AiRole.Vision => Vision, AiRole.Image => Image, _ => Chat }) ?? AiProviders.Create("openai", role);
}

public sealed record AiProvider(string Id, string Label, string BaseUrl, string ImageModel, string VisionModel, string ChatModel,
    string Auth = "bearer", bool ImageOnly = false);

public static class AiProviders
{
    public static IReadOnlyList<AiProvider> All { get; } = new AiProvider[]
    {
        new("openai", "OpenAI / Compatible", "https://api.openai.com/v1", "gpt-image-1", "gpt-4o", "gpt-4o"),
        new("gemini", "Google Gemini / Nano Banana", "https://generativelanguage.googleapis.com/v1beta", "gemini-2.5-flash-image", "gemini-2.5-flash", "gemini-2.5-flash", "query"),
        new("ark", "Volcengine Ark / Seedream", "https://ark.cn-beijing.volces.com/api/v3", "doubao-seedream-4-0-250828", "doubao-seed-1-6-vision-250815", "doubao-seed-1-6-250615"),
        new("dashscope", "Alibaba DashScope / Wanx", "https://dashscope.aliyuncs.com/api/v1", "wanx2.1-t2i-turbo", "qwen-vl-max", "qwen-plus"),
        new("zhipu", "Zhipu GLM / CogView", "https://open.bigmodel.cn/api/paas/v4", "cogview-4", "glm-4v-plus", "glm-4-plus"),
        new("siliconflow", "SiliconFlow", "https://api.siliconflow.cn/v1", "Kwai-Kolors/Kolors", "Qwen/Qwen2.5-VL-72B-Instruct", "Qwen/Qwen2.5-72B-Instruct"),
        new("stability", "Stability AI", "https://api.stability.ai", "core", "", "", ImageOnly: true),
        new("sdwebui", "Local SD WebUI / Forge", "http://127.0.0.1:7860", "", "", "", "none", true),
        new("custom", "Custom request template", "", "", "", "")
    };
    public static AiEndpointSettings Create(string id, AiRole role)
    {
        var provider = All.Single(p => p.Id == id);
        return new(id, provider.BaseUrl, Model: role switch { AiRole.Image => provider.ImageModel, AiRole.Vision => provider.VisionModel, _ => provider.ChatModel }, Auth: provider.Auth);
    }
}
