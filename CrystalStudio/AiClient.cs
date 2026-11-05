using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CrystalStudio;

// Each call carries a complete endpoint. Credentials never live on shared default headers.
public sealed class AiClient(HttpClient? http = null)
{
    private static readonly HttpClient shared = new() { Timeout = Timeout.InfiniteTimeSpan };
    private readonly HttpClient http = http ?? shared;

    public Task<string> TextAsync(AiEndpointSettings cfg, string system, string prompt, IReadOnlyList<string> images, CancellationToken token) =>
        RunAsync(cfg, images.Count > 0 ? AiRole.Vision : AiRole.Chat, token, async stop =>
        {
            string path;
            JsonObject body;
            if (cfg.Provider == "gemini")
            {
                path = "/models/" + Uri.EscapeDataString(cfg.Model) + ":generateContent";
                var parts = new JsonArray(new JsonObject { ["text"] = prompt });
                foreach (var image in images)
                {
                    var (mime, data) = DataImage(image);
                    parts.Add(new JsonObject { ["inline_data"] = new JsonObject { ["mime_type"] = mime, ["data"] = data } });
                }
                body = new() { ["systemInstruction"] = new JsonObject { ["parts"] = new JsonArray(new JsonObject { ["text"] = system }) },
                    ["contents"] = new JsonArray(new JsonObject { ["role"] = "user", ["parts"] = parts }),
                    ["generationConfig"] = new JsonObject { ["temperature"] = 0.2 } };
            }
            else if (cfg.Provider == "dashscope")
            {
                bool vision = images.Count > 0;
                path = vision ? "/services/aigc/multimodal-generation/generation" : "/services/aigc/text-generation/generation";
                var content = new JsonArray(new JsonObject { ["text"] = prompt });
                foreach (var image in images) content.Add(new JsonObject { ["image"] = image });
                body = new() { ["model"] = cfg.Model,
                    ["input"] = new JsonObject { ["messages"] = new JsonArray(
                        new JsonObject { ["role"] = "system", ["content"] = vision ? new JsonArray(new JsonObject { ["text"] = system }) : JsonValue.Create(system) },
                        new JsonObject { ["role"] = "user", ["content"] = vision ? content : JsonValue.Create(prompt) }) },
                    ["parameters"] = new JsonObject { ["result_format"] = "message", ["temperature"] = 0.2 } };
            }
            else
            {
                path = "/chat/completions";
                var content = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = prompt });
                foreach (var image in images) content.Add(new JsonObject { ["type"] = "image_url", ["image_url"] = new JsonObject { ["url"] = image } });
                body = new() { ["model"] = cfg.Model, ["temperature"] = 0.2,
                    ["messages"] = new JsonArray(new JsonObject { ["role"] = "system", ["content"] = system },
                        new JsonObject { ["role"] = "user", ["content"] = images.Count > 0 ? content : JsonValue.Create(prompt) }) };
            }
            var vars = Variables(cfg, prompt, system, images, 1024, 1024);
            body = Extra(cfg, body, vars);
            var json = await JsonRequest(cfg, HttpMethod.Post, ChoosePath(cfg, path, vars), body, stop);
            if (!string.IsNullOrWhiteSpace(cfg.ResponsePath))
                return StringAt(json, cfg.ResponsePath) ?? throw new InvalidDataException(LocalText.T("AI response path did not contain text."));
            string result = Text(json?["choices"]?[0]?["message"]?["content"])
                + Text(json?["candidates"]?[0]?["content"]?["parts"])
                + Text(json?["output"] is JsonObject output ? output["choices"]?[0]?["message"]?["content"] ?? output["text"] : null)
                + Text(json?["output_text"]);
            if (result.Length == 0 && json?["output"] is JsonArray items)
                result = string.Concat(items.Select(item => Text(item?["content"])));
            return string.IsNullOrWhiteSpace(result) ? throw new InvalidDataException(LocalText.T("AI returned no text. Check the model and response path.")) : result;
        });

    public Task<byte[]> GenerateImageAsync(AiEndpointSettings cfg, string prompt, int width, int height, CancellationToken token) =>
        RunAsync(cfg, AiRole.Image, token, async stop =>
        {
            if (width < 64 || height < 64 || width > 4096 || height > 4096) throw new ArgumentOutOfRangeException(nameof(width));
            var vars = Variables(cfg, prompt, "", [], width, height);
            string aspect = Aspect(width, height);
            if (cfg.Provider == "stability")
            {
                using var form = new MultipartFormDataContent();
                form.Add(new StringContent(prompt), "prompt"); form.Add(new StringContent("png"), "output_format");
                form.Add(new StringContent(aspect), "aspect_ratio");
                using var request = Request(cfg, HttpMethod.Post, ChoosePath(cfg, "/v2beta/stable-image/generate/" + (string.IsNullOrWhiteSpace(cfg.Model) ? "core" : cfg.Model), vars));
                request.Headers.TryAddWithoutValidation("Accept", "image/*"); request.Content = form;
                using var response = await Send(request, cfg, stop);
                return await response.Content.ReadAsByteArrayAsync(stop);
            }
            string path = "/images/generations";
            JsonObject body = new() { ["model"] = cfg.Model, ["prompt"] = prompt, ["n"] = 1, ["size"] = vars["size"]!.DeepClone() };
            if (cfg.Provider == "gemini")
            {
                bool imagen = cfg.Model.Contains("imagen", StringComparison.OrdinalIgnoreCase);
                path = "/models/" + Uri.EscapeDataString(cfg.Model) + (imagen ? ":predict" : ":generateContent");
                body = imagen ? new() { ["instances"] = new JsonArray(new JsonObject { ["prompt"] = prompt }),
                        ["parameters"] = new JsonObject { ["sampleCount"] = 1, ["aspectRatio"] = aspect } }
                    : new() { ["contents"] = new JsonArray(new JsonObject { ["role"] = "user", ["parts"] = new JsonArray(new JsonObject { ["text"] = prompt }) }),
                        ["generationConfig"] = new JsonObject { ["responseModalities"] = new JsonArray("TEXT", "IMAGE"), ["imageConfig"] = new JsonObject { ["aspectRatio"] = aspect } } };
            }
            else if (cfg.Provider == "sdwebui")
            {
                path = "/sdapi/v1/txt2img";
                body = new() { ["prompt"] = prompt, ["width"] = width, ["height"] = height, ["steps"] = 28, ["cfg_scale"] = 7, ["sampler_name"] = "DPM++ 2M Karras" };
            }
            else if (cfg.Provider == "dashscope")
            {
                path = "/services/aigc/text2image/image-synthesis";
                body = new() { ["model"] = cfg.Model, ["input"] = new JsonObject { ["prompt"] = prompt },
                    ["parameters"] = new JsonObject { ["size"] = $"{width}*{height}", ["n"] = 1 } };
            }
            var json = await JsonRequest(cfg, HttpMethod.Post, ChoosePath(cfg, path, vars), Extra(cfg, body, vars), stop, cfg.Provider == "dashscope");
            if (cfg.Provider == "dashscope")
            {
                string id = StringAt(json, "output.task_id") ?? StringAt(json, "task_id")
                    ?? throw new InvalidDataException(LocalText.T("DashScope did not return a task ID."));
                while (true)
                {
                    await Task.Delay(2500, stop);
                    json = await JsonRequest(cfg, HttpMethod.Get, "/tasks/" + Uri.EscapeDataString(id), null, stop);
                    string? state = StringAt(json, "output.task_status") ?? StringAt(json, "task_status");
                    if (state == "SUCCEEDED") break;
                    if (state is "FAILED" or "CANCELED" or "UNKNOWN") throw new InvalidDataException(LocalText.T("DashScope image task failed or was cancelled."));
                }
            }
            string? image = !string.IsNullOrWhiteSpace(cfg.ResponsePath) ? StringAt(json, cfg.ResponsePath) : FindImage(json);
            if (string.IsNullOrWhiteSpace(image)) throw new InvalidDataException(LocalText.T("AI returned no image. Check the image model and response path."));
            if (Uri.TryCreate(image, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
            {
                // Image download hosts must never receive the API endpoint's credentials.
                using var download = new HttpRequestMessage(HttpMethod.Get, uri);
                using var response = await Send(download, cfg, stop);
                return await response.Content.ReadAsByteArrayAsync(stop);
            }
            string b64 = image.StartsWith("data:", StringComparison.OrdinalIgnoreCase) ? DataImage(image).Data : image;
            try { return Convert.FromBase64String(b64); }
            catch (FormatException) { throw new InvalidDataException(LocalText.T("AI returned invalid image data.")); }
        });

    private static async Task<T> RunAsync<T>(AiEndpointSettings cfg, AiRole role, CancellationToken token, Func<CancellationToken, Task<T>> action)
    {
        cfg.Validate(role);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(cfg.TimeoutSeconds));
        try { return await action(timeout.Token); }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { throw new InvalidDataException(LocalText.T("AI request timed out. Check the service or increase the timeout.")); }
        catch (HttpRequestException) { throw new InvalidDataException(LocalText.T("Could not connect to the AI service. Check its address and network connection.")); }
        catch (JsonException) { throw new InvalidDataException(LocalText.T("AI returned invalid JSON.")); }
    }

    private async Task<JsonNode?> JsonRequest(AiEndpointSettings cfg, HttpMethod method, string path, JsonObject? body, CancellationToken token, bool dashAsync = false)
    {
        using var request = Request(cfg, method, path);
        if (body != null) request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        if (dashAsync) request.Headers.TryAddWithoutValidation("X-DashScope-Async", "enable");
        using var response = await Send(request, cfg, token);
        return JsonNode.Parse(await response.Content.ReadAsStringAsync(token));
    }

    private async Task<HttpResponseMessage> Send(HttpRequestMessage request, AiEndpointSettings cfg, CancellationToken token)
    {
        var response = await http.SendAsync(request, token);
        if (response.IsSuccessStatusCode) return response;
        using (response)
        {
            string detail = await response.Content.ReadAsStringAsync(token);
            if (cfg.ApiKey.Length > 0) detail = detail.Replace(cfg.ApiKey, "***", StringComparison.Ordinal).Replace(Uri.EscapeDataString(cfg.ApiKey), "***", StringComparison.Ordinal);
            detail = new string(detail.Where(c => !char.IsControl(c)).Take(240).ToArray());
            throw new InvalidDataException(LocalText.Format("AI service returned HTTP {0}: {1}", (int)response.StatusCode, detail));
        }
    }

    private static HttpRequestMessage Request(AiEndpointSettings cfg, HttpMethod method, string path)
    {
        string url = cfg.BaseUrl.TrimEnd('/') + "/" + path.TrimStart('/');
        if (cfg.Auth == "query" && cfg.ApiKey.Length > 0) url += (url.Contains('?') ? "&" : "?") + "key=" + Uri.EscapeDataString(cfg.ApiKey);
        var request = new HttpRequestMessage(method, url);
        if (cfg.ApiKey.Length > 0)
        {
            if (cfg.Auth == "bearer") request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + cfg.ApiKey);
            else if (cfg.Auth == "raw") request.Headers.TryAddWithoutValidation("Authorization", cfg.ApiKey);
            else if (cfg.Auth == "x-api-key") request.Headers.TryAddWithoutValidation("x-api-key", cfg.ApiKey);
        }
        return request;
    }

    private static (string Mime, string Data) DataImage(string value)
    {
        int comma = value.IndexOf(',');
        if (!value.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase) || comma < 0 || !value[..comma].EndsWith(";base64", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException(LocalText.T("Expected a base64 image data URL."));
        return (value[5..value.IndexOf(';')], value[(comma + 1)..]);
    }

    private static JsonObject Variables(AiEndpointSettings cfg, string prompt, string system, IReadOnlyList<string> images, int width, int height) => new()
    {
        ["model"] = cfg.Model, ["prompt"] = prompt, ["system"] = system, ["image"] = images.FirstOrDefault() ?? "",
        ["images"] = new JsonArray(images.Select(i => (JsonNode?)JsonValue.Create(i)).ToArray()), ["width"] = width, ["height"] = height, ["n"] = 1,
        ["size"] = cfg.SizeFormat switch { "star" => JsonValue.Create($"{width}*{height}"), "pixel" => JsonValue.Create(width * height), _ => JsonValue.Create($"{width}x{height}") }
    };

    private static JsonObject Extra(AiEndpointSettings cfg, JsonObject body, JsonObject vars)
    {
        if (string.IsNullOrWhiteSpace(cfg.ExtraBody)) return body;
        var extra = Fill(JsonNode.Parse(cfg.ExtraBody), vars) as JsonObject ?? throw new InvalidDataException(LocalText.T("AI extra body must be a JSON object."));
        if (cfg.Provider == "custom") return extra;
        foreach (var pair in extra) body[pair.Key] = pair.Value?.DeepClone();
        return body;
    }

    private static JsonNode? Fill(JsonNode? node, JsonObject vars)
    {
        if (node is JsonObject obj) return new JsonObject(obj.Select(p => new KeyValuePair<string, JsonNode?>(p.Key, Fill(p.Value, vars))));
        if (node is JsonArray array) return new JsonArray(array.Select(n => Fill(n, vars)).ToArray());
        if (node is JsonValue value && value.TryGetValue<string>(out var text))
        {
            foreach (var pair in vars) if (text == "{{" + pair.Key + "}}") return pair.Value?.DeepClone();
            foreach (var pair in vars) text = text.Replace("{{" + pair.Key + "}}", pair.Value is JsonValue v && v.TryGetValue<string>(out var s) ? s : pair.Value?.ToJsonString() ?? "", StringComparison.Ordinal);
            return JsonValue.Create(text);
        }
        return node?.DeepClone();
    }

    private static string ChoosePath(AiEndpointSettings cfg, string fallback, JsonObject vars)
    {
        string path = string.IsNullOrWhiteSpace(cfg.Path) ? fallback : cfg.Path.Trim();
        foreach (var pair in vars) if (pair.Value is JsonValue value && value.TryGetValue<string>(out var text)) path = path.Replace("{{" + pair.Key + "}}", Uri.EscapeDataString(text), StringComparison.Ordinal);
        return path;
    }

    private static string Text(JsonNode? node) => node switch
    {
        JsonValue value when value.TryGetValue<string>(out var text) => text,
        JsonArray array => string.Concat(array.Select(p => p is JsonObject obj ? Text(obj["text"]) : Text(p))),
        _ => ""
    };

    private static string? StringAt(JsonNode? node, string path)
    {
        foreach (string key in path.Replace("[", ".").Replace("]", "").Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            if (node is JsonObject obj) node = obj[key];
            else if (node is JsonArray array && int.TryParse(key, out int index) && index >= 0 && index < array.Count) node = array[index];
            else return null;
        }
        return node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
    }

    private static string? FindImage(JsonNode? node, int depth = 0)
    {
        if (depth > 12) return null;
        if (node is JsonArray array)
        {
            foreach (var item in array) if (FindImage(item, depth + 1) is {} image) return image;
        }
        if (node is not JsonObject obj) return null;
        foreach (string key in new[] { "b64_json", "base64", "image_base64", "bytesBase64Encoded", "url", "image_url", "imageUrl" })
            if (obj[key] is JsonValue value && value.TryGetValue<string>(out var text) && text.Length > 0) return text;
        foreach (string key in new[] { "inlineData", "inline_data" })
            if (obj[key] is JsonObject inline && inline["data"] is JsonValue data && data.TryGetValue<string>(out var b64)) return b64;
        if (obj["images"] is JsonArray images && images.FirstOrDefault() is JsonValue first && first.TryGetValue<string>(out var sd)) return sd;
        foreach (var pair in obj) if (FindImage(pair.Value, depth + 1) is {} image) return image;
        return null;
    }

    private static string Aspect(int width, int height)
    {
        (int W, int H)[] options = [(1, 1), (4, 3), (3, 4), (3, 2), (2, 3), (16, 9), (9, 16), (21, 9), (9, 21)];
        var best = options.MinBy(p => Math.Abs((double)p.W / p.H - (double)width / height));
        return best.W.ToString(CultureInfo.InvariantCulture) + ":" + best.H.ToString(CultureInfo.InvariantCulture);
    }
}
