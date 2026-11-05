namespace CrystalStudio;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        int exitCode = 1;
        try
        {
            StudioLog.Initialize();
            exitCode = BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        catch (Exception ex) { StudioLog.Fatal("Program.Main", ex); }
        finally { StudioLog.Shutdown(exitCode); }
        return exitCode;
    }

    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
        .UsePlatformDetect().WithInterFont()
        .AfterPlatformServicesSetup(_ => StudioLog.InstallDispatcherHandler());
}
