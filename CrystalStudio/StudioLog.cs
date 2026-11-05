using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using Avalonia.Logging;

namespace CrystalStudio;

/// <summary>Process diagnostics that remain usable before the UI starts and during fatal exception handling.</summary>
internal static class StudioLog
{
    private static FileLog? file;
    private static int handlersInstalled;
    private static int dispatcherHandlerInstalled;
    private static int fatalObserved;
    internal static string? FilePath => file?.FilePath;

    internal static void Initialize()
    {
        if (file != null) return;
        string profile = "<unavailable>";
        Exception? initializationError = null;
        try { profile = EditorPaths.ProfileDirectory; file = new FileLog(Path.Combine(profile, "Logs")); }
        catch (Exception ex)
        {
            initializationError = ex;
            try { file = new FileLog(Path.Combine(Path.GetTempPath(), "CrystalStudio", "Logs")); }
            catch (Exception) { /* Diagnostics must not prevent startup when both locations are unavailable. */ }
        }
        Info("Process", $"Starting CrystalStudio {typeof(Program).Assembly.GetName().Version}; " +
            $"Executable={Environment.ProcessPath}; BaseDirectory={AppContext.BaseDirectory}; " +
            $"Runtime={RuntimeInformation.FrameworkDescription}; OS={RuntimeInformation.OSDescription}; " +
            $"Architecture={RuntimeInformation.ProcessArchitecture}; DynamicCode={RuntimeFeature.IsDynamicCodeSupported}; " +
            $"Profile={profile}; Log={FilePath}");
        if (initializationError != null) Warning("Logging.Initialize", "Using the temporary log directory", initializationError);
        if (Interlocked.Exchange(ref handlersInstalled, 1) != 0) return;
        Logger.Sink = new AvaloniaSink();
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Fatal("AppDomain.UnhandledException", e.ExceptionObject as Exception,
                $"Terminating={e.IsTerminating}; {e.ExceptionObject}");
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Error("TaskScheduler.UnobservedTaskException", e.Exception);
            e.SetObserved();
        };
        AppDomain.CurrentDomain.ProcessExit += (_, _) => Shutdown(Environment.ExitCode);
    }

    internal static void InstallDispatcherHandler()
    {
        // Accessing UIThread before platform setup creates a NullDispatcherImpl that cannot run the desktop loop.
        if (Interlocked.Exchange(ref dispatcherHandlerInstalled, 1) == 0)
            Dispatcher.UIThread.UnhandledException += (_, e) => Fatal("Dispatcher.UnhandledException", e.Exception);
    }

    internal static void Info(string area, string message) => file?.Write(LogEventLevel.Information, area, message);
    internal static void Warning(string area, string message, Exception? error = null) => file?.Write(LogEventLevel.Warning, area, message, error);
    internal static void Error(string area, Exception error, string message = "Operation failed") => file?.Write(LogEventLevel.Error, area, message, error);
    internal static void Fatal(string area, Exception? error, string message = "Unhandled exception")
    {
        Interlocked.Exchange(ref fatalObserved, 1);
        file?.Write(LogEventLevel.Fatal, area, message, error);
    }
    internal static void Shutdown(int exitCode)
    {
        file?.Write(LogEventLevel.Information, "Process", $"Session ended; ExitCode={exitCode}; FatalException={Volatile.Read(ref fatalObserved) != 0}");
        file?.Dispose();
    }

    internal sealed class AvaloniaSink : ILogSink
    {
        public bool IsEnabled(LogEventLevel level, string area) => level >= LogEventLevel.Warning;
        public void Log(LogEventLevel level, string area, object? source, string messageTemplate) =>
            Log(level, area, source, messageTemplate, []);
        public void Log(LogEventLevel level, string area, object? source, string messageTemplate, params object?[] propertyValues)
        {
            if (!IsEnabled(level, area)) return;
            var message = new StringBuilder(messageTemplate);
            foreach (var value in propertyValues) message.Append(" | ").Append(value);
            file?.Write(level, "Avalonia." + area, message.ToString());
        }
    }
}

/// <summary>Bounded UTF-8 files with immediate writes, independent sessions and no logging exceptions escaping to callers.</summary>
internal sealed class FileLog : IDisposable
{
    private readonly object gate = new();
    private readonly string directory;
    private readonly string session = $"CrystalStudio-{DateTime.Now:yyyyMMdd-HHmmss-fff}-{Environment.ProcessId}-{Guid.NewGuid():N}";
    private readonly long maxFileBytes;
    private readonly int retainedFiles;
    private StreamWriter? writer;
    private int part;
    private bool disposed;
    internal string? FilePath { get; private set; }

    internal FileLog(string directory, long maxFileBytes = 8 * 1024 * 1024, int retainedFiles = 20)
    {
        this.directory = Path.GetFullPath(directory);
        this.maxFileBytes = Math.Max(256, maxFileBytes);
        this.retainedFiles = Math.Max(2, retainedFiles);
        Directory.CreateDirectory(this.directory);
        OpenFile();
    }

    private void OpenFile()
    {
        FilePath = Path.Combine(directory, $"{session}-{part++:D3}.log");
        writer = new StreamWriter(new FileStream(FilePath, FileMode.CreateNew, FileAccess.Write, FileShare.Read), new UTF8Encoding(false)) { AutoFlush = true };
        // Only owned log files are eligible; an active process keeps its file protected from deletion.
        try
        {
            foreach (var old in new DirectoryInfo(directory).EnumerateFiles("CrystalStudio-*.log")
                .OrderByDescending(f => f.LastWriteTimeUtc).ThenByDescending(f => f.Name, StringComparer.Ordinal).Skip(retainedFiles))
            {
                try { old.Delete(); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    internal void Write(LogEventLevel level, string area, string message, Exception? error = null)
    {
        lock (gate)
        {
            if (disposed) return;
            try
            {
                if (writer == null || writer.BaseStream.Position >= maxFileBytes)
                {
                    writer?.Dispose(); writer = null;
                    OpenFile();
                }
                writer.WriteLine($"{DateTimeOffset.Now.ToString("O", CultureInfo.InvariantCulture)} [{level}] [P{Environment.ProcessId}:T{Environment.CurrentManagedThreadId}] [{area}] {message}");
                if (error != null) writer.WriteLine(error.ToString());
                if (level == LogEventLevel.Fatal) ((FileStream)writer.BaseStream).Flush(true);
            }
            catch (Exception ex)
            {
                // Avoid recursively logging failures in the log destination itself.
                Debug.WriteLine($"CrystalStudio logging failed: {ex.Message}");
            }
        }
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            try { writer?.Dispose(); }
            catch (Exception) { }
            writer = null;
        }
    }
}
