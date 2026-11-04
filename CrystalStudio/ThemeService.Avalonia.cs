using System.Text.Json;
using Avalonia.Platform;

namespace CrystalStudio;

public static class ThemeService
{
    private static bool initialized;
    public static bool ForceDark { get; private set; }
    public static event Action? Changed;
    private static readonly string preferences = Path.Combine(EditorPaths.ProfileDirectory, "appearance.json");
    private sealed record Preference(bool ForceDark);
    public static Brush Brush(string key) => Application.Current?.TryFindResource(key, out var value) == true && value is Brush brush ? brush : Brushes.Gray;
    public static void Initialize()
    {
        if (initialized) return;
        initialized = true;
        try { if (File.Exists(preferences)) ForceDark = JsonSerializer.Deserialize<Preference>(File.ReadAllText(preferences))?.ForceDark ?? false; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
        if (Application.Current?.PlatformSettings is { } platform) platform.ColorValuesChanged += (_, _) => ApplyPlatform();
        ApplyPlatform();
    }
    public static void SetDarkOverride(bool enabled)
    {
        ForceDark = enabled; ApplyPlatform();
        try { AtomicFile.WriteJson(preferences, new Preference(enabled)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { MessageBox.Show("The colour preference could not be saved: " + ex.Message, "Appearance"); }
    }
    private static void ApplyPlatform()
    {
        var values = Application.Current?.PlatformSettings?.GetColorValues();
        ApplyPalette(ForceDark || values?.ThemeVariant == PlatformThemeVariant.Dark, values?.AccentColor1 ?? Color.FromRgb(132, 152, 255));
    }
    private static Color Blend(Color a, Color b, double amount) => Color.FromRgb((byte)(a.R * (1 - amount) + b.R * amount), (byte)(a.G * (1 - amount) + b.G * amount), (byte)(a.B * (1 - amount) + b.B * amount));
    public static void ApplyPalette(bool dark, Color accent, bool highContrast = false)
    {
        var app = Application.Current!; var resources = app.Resources;
        app.RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light;
        foreach (var key in resources.Keys.OfType<string>().Where(k => k.StartsWith("Theme")).ToArray())
        {
            if (!Color.TryParse("#" + key[5..], out var original)) continue;
            double luminance = (original.R * 0.2126 + original.G * 0.7152 + original.B * 0.0722) / 255;
            bool blue = original.B - original.R > 35 && original.B - original.G > 12;
            Color color = original;
            if (highContrast) color = luminance < 0.4 ? Colors.Black : Colors.White;
            else if (blue) color = luminance < 0.4 ? Blend(dark ? Color.FromRgb(25, 29, 35) : Colors.White, accent, dark ? 0.28 : 0.15) : Blend(accent, dark ? Colors.White : Colors.Black, dark ? 0.3 : 0.15);
            else if (!dark) color = luminance < 0.4 ? Blend(Colors.White, Color.FromRgb(163, 173, 185), Math.Clamp(luminance * 1.4 - 0.07, 0, 0.65)) : Color.FromRgb(67, 77, 90);
            resources[key] = new ImmutableSolidColorBrush(Color.FromArgb(original.A, color.R, color.G, color.B));
        }
        resources["Panel"] = Brush("Theme1C2028"); resources["Muted"] = Brush("Theme929CAB"); resources["Accent"] = new ImmutableSolidColorBrush(accent); resources["Line"] = Brush("Theme303640");
        resources["Theme6378DF"] = new ImmutableSolidColorBrush(accent);
        resources["OnAccent"] = new ImmutableSolidColorBrush((accent.R * 0.2126 + accent.G * 0.7152 + accent.B * 0.0722) > 145 ? Colors.Black : Colors.White);
        resources["CanvasBackground"] = highContrast ? Brushes.Black : Brush("Theme14171D");
        resources["CanvasPaper"] = new ImmutableSolidColorBrush(dark ? Color.FromRgb(31, 37, 43) : Color.FromRgb(239, 242, 245));
        resources["CanvasGrid"] = new ImmutableSolidColorBrush(dark ? Color.FromArgb(38, 160, 177, 199) : Color.FromArgb(60, 73, 89, 108));
        Changed?.Invoke();
    }
}
