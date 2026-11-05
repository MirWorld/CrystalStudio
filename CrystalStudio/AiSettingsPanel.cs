using System.IO;

namespace CrystalStudio;

internal sealed class AiSettingsPanel : StackPanel
{
    private readonly Dictionary<AiRole, EndpointPanel> editors = [];
    internal AiSettingsPanel(AiSettings settings)
    {
        Spacing = 8;
        Children.Add(new TextBlock { Text = LocalText.T("Configure vision, image generation and chat independently. Object recognition uses Vision AI."), TextWrapping = TextWrapping.Wrap });
        var tabs = new TabControl { Name = "AiRoleTabs" };
        var items = new List<TabItem>();
        foreach (var role in Enum.GetValues<AiRole>())
        {
            var editor = new EndpointPanel(role, settings.For(role)); editors.Add(role, editor);
            items.Add(new TabItem { Header = LocalText.T(role switch { AiRole.Vision => "Vision AI", AiRole.Image => "Image AI", _ => "Chat AI" }),
                Content = editor, Name = "Ai" + role + "Tab" });
        }
        tabs.ItemsSource = items; tabs.SelectedIndex = 0; Children.Add(tabs);
    }
    internal AiSettings Read()
    {
        var result = new AiSettings(editors[AiRole.Vision].Read(), editors[AiRole.Image].Read(), editors[AiRole.Chat].Read());
        foreach (var role in Enum.GetValues<AiRole>()) result.For(role).Validate(role);
        return result;
    }
    internal void CancelTests() { foreach (var editor in editors.Values) editor.CancelTest(); }

    private sealed class EndpointPanel : StackPanel
    {
        private readonly AiRole role;
        private readonly ComboBox provider = new(), auth = new(), size = new();
        private readonly TextBox url = new(), key = new() { PasswordChar = '●' }, model = new(), path = new(), extra = new(), response = new(), timeout = new();
        private readonly TextBlock result = new() { TextWrapping = TextWrapping.Wrap };
        private readonly Button test = new(), cancel = new();
        private readonly Image image = new() { MaxHeight = 180, Stretch = Stretch.Uniform };
        private readonly AiProvider[] providers;
        private readonly Dictionary<string, AiEndpointSettings> drafts = [];
        private CancellationTokenSource? testStop;
        private string currentProvider;
        private RasterImage? testImage;
        internal EndpointPanel(AiRole role, AiEndpointSettings settings)
        {
            this.role = role; Spacing = 6; Margin = new(0, 8);
            providers = AiProviders.All.Where(p => role == AiRole.Image || !p.ImageOnly).ToArray();
            currentProvider = providers.Any(p => p.Id == settings.Provider) ? settings.Provider : "openai";
            Name = "Ai" + role + "Settings";
            provider.Name = "Ai" + role + "Provider"; url.Name = "Ai" + role + "BaseUrl";
            key.Name = "Ai" + role + "Key"; model.Name = "Ai" + role + "Model";
            provider.ItemsSource = providers.Select(p => LocalText.T(p.Label)).ToArray();
            provider.SelectedIndex = Array.FindIndex(providers, p => p.Id == currentProvider);
            Add("Provider", provider); Add("Service base URL", url); Add("API key", key); Add("Model", model);
            var advanced = new StackPanel { Spacing = 6 };
            auth.ItemsSource = new[] { "bearer", "query", "x-api-key", "raw", "none" };
            size.ItemsSource = new[] { "x", "star", "pixel" };
            void Advanced(string label, Control control) { advanced.Children.Add(new TextBlock { Text = LocalText.T(label) }); advanced.Children.Add(control); }
            path.Watermark = LocalText.T("Leave empty to use the provider default path");
            extra.AcceptsReturn = true; extra.MinHeight = 80; extra.MaxHeight = 160;
            extra.Watermark = "{\"model\":\"{{model}}\",\"prompt\":\"{{prompt}}\"}";
            Advanced("Request path", path); Advanced("Authentication", auth); Advanced("Timeout (seconds)", timeout);
            Advanced("Extra JSON / custom request template", extra); Advanced("Response path (optional)", response);
            if (role == AiRole.Image) Advanced("Image size format (x / star / pixel)", size);
            advanced.Children.Add(new TextBlock { Text = LocalText.T("Templates: {{model}}, {{prompt}}, {{system}}, {{image}}, {{images}}, {{width}}, {{height}}, {{size}}, {{n}}. Custom providers replace the request body; other providers merge extra JSON."), TextWrapping = TextWrapping.Wrap, FontSize = 11 });
            Children.Add(new Expander { Header = LocalText.T("Advanced AI settings"), Content = advanced, HorizontalAlignment = HorizontalAlignment.Stretch });
            var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            test.Content = LocalText.T(role == AiRole.Image ? "Test image generation (creates an image)" : "Test AI request");
            test.Name = "Ai" + role + "Test"; cancel.Content = LocalText.T("Cancel"); cancel.IsEnabled = false;
            actions.Children.Add(test); actions.Children.Add(cancel); Children.Add(actions); Children.Add(result); Children.Add(image);
            Fill(settings with { Provider = currentProvider });
            provider.SelectionChanged += (_, _) =>
            {
                if (provider.SelectedIndex < 0) return;
                drafts[currentProvider] = Read(); currentProvider = providers[provider.SelectedIndex].Id;
                Fill(drafts.GetValueOrDefault(currentProvider) ?? AiProviders.Create(currentProvider, role));
                result.Text = "";
            };
            test.Click += async (_, _) => await TestAsync(); cancel.Click += (_, _) => CancelTest();
            DetachedFromVisualTree += (_, _) => { CancelTest(); image.Source = null; testImage?.Dispose(); testImage = null; };
        }
        private void Add(string label, Control control) { Children.Add(new TextBlock { Text = LocalText.T(label) }); Children.Add(control); }
        private void Fill(AiEndpointSettings cfg)
        {
            url.Text = cfg.BaseUrl; key.Text = cfg.ApiKey; model.Text = cfg.Model; path.Text = cfg.Path; extra.Text = cfg.ExtraBody;
            response.Text = cfg.ResponsePath; timeout.Text = cfg.TimeoutSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
            auth.SelectedItem = cfg.Auth; size.SelectedItem = cfg.SizeFormat;
        }
        internal AiEndpointSettings Read() => new(currentProvider, url.Text?.Trim() ?? "", key.Text?.Trim() ?? "", model.Text?.Trim() ?? "",
            path.Text?.Trim() ?? "", auth.SelectedItem as string ?? "bearer", extra.Text?.Trim() ?? "", response.Text?.Trim() ?? "",
            int.TryParse(timeout.Text, out int seconds) ? seconds : 0, size.SelectedItem as string ?? "x");
        internal void CancelTest() => testStop?.Cancel();
        private async Task TestAsync()
        {
            if (testStop != null) return;
            using var stop = new CancellationTokenSource(); testStop = stop;
            var cfg = Read();
            provider.IsEnabled = test.IsEnabled = false; cancel.IsEnabled = true; result.Text = LocalText.T("Testing AI request…");
            try
            {
                var client = new AiClient();
                if (role == AiRole.Image)
                {
                    var bytes = await client.GenerateImageAsync(cfg, "A single green tree, 2D game asset, plain background", 1024, 1024, stop.Token);
                    using var stream = new MemoryStream(bytes); var bitmap = RasterImage.Load(stream);
                    image.Source = null; testImage?.Dispose(); testImage = bitmap; image.Source = bitmap;
                    result.Text = LocalText.T("AI image generated successfully.");
                }
                else
                {
                    string[] images = role == AiRole.Vision ? ["data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+jRZkAAAAASUVORK5CYII="] : [];
                    string text = await client.TextAsync(cfg, "Reply briefly.", role == AiRole.Vision ? "Describe this image in one short sentence." : "Reply with OK.", images, stop.Token);
                    result.Text = LocalText.Format("AI request succeeded: {0}", text.Length > 300 ? text[..300] : text);
                }
            }
            catch (OperationCanceledException) { result.Text = LocalText.T("AI request cancelled."); }
            catch (Exception ex) { result.Text = LocalText.T(ex.Message); }
            finally { testStop = null; provider.IsEnabled = test.IsEnabled = true; cancel.IsEnabled = false; }
        }
    }
}
