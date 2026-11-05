using Avalonia.Logging;

namespace CrystalStudio.Tests;

public sealed class StudioLogTests
{
    [Fact]
    public void ConcurrentWritesPersistCompleteMessagesAndExceptionStacksBeforeClose()
    {
        string directory=Path.Combine(Path.GetTempPath(),"CrystalStudio-logs-"+Guid.NewGuid().ToString("N"));
        try
        {
            using var log=new FileLog(directory);
            Exception exception;
            try { ThrowPreviewError(); throw new Exception("unreachable"); }
            catch(InvalidOperationException ex){exception=ex;}
            Parallel.For(0,300,i=>log.Write(LogEventLevel.Information,"并发测试",$"record-{i:D4}-end"));
            log.Write(LogEventLevel.Error,"Extraction.Preview","中文物件预览错误",exception);
            var lines=Read(log.FilePath!).Split('\n',StringSplitOptions.RemoveEmptyEntries).Select(line=>line.TrimEnd('\r')).ToArray();
            Assert.Equal(300,lines.Count(line=>line.Contains("record-",StringComparison.Ordinal)));
            foreach(int i in Enumerable.Range(0,300))Assert.Single(lines,line=>line.EndsWith($"record-{i:D4}-end",StringComparison.Ordinal));
            string content=Read(log.FilePath!);
            Assert.Contains("[Error]",content);Assert.Contains("中文物件预览错误",content);
            Assert.Contains(nameof(ThrowPreviewError),content);Assert.Contains("preview failure",content);
            Assert.Contains("inner evidence",content);
        }
        finally{if(Directory.Exists(directory))Directory.Delete(directory,true);}
    }

    [Fact]
    public void RotationLimitsOwnedLogsAndPreservesOtherFilesAndActiveSessions()
    {
        string directory=Path.Combine(Path.GetTempPath(),"CrystalStudio-log-rotation-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string other=Path.Combine(directory,"user-notes.txt");File.WriteAllText(other,"retain this");
        try
        {
            using var active=new FileLog(directory,1024,4);
            active.Write(LogEventLevel.Information,"Test","active session");
            string first=active.FilePath!;
            using var second=new FileLog(directory,1024,4);
            for(int i=0;i<30;i++)second.Write(LogEventLevel.Warning,"Rotation",i+new string('x',600));
            Assert.NotEqual(first,second.FilePath);
            Assert.True(File.Exists(first));Assert.Equal("retain this",File.ReadAllText(other));
            Assert.InRange(Directory.GetFiles(directory,"CrystalStudio-*.log").Length,2,5);
            Assert.Contains("29xxx",Read(second.FilePath!));
            active.Write(LogEventLevel.Error,"Test","active session remains writable");
            Assert.Contains("remains writable",Read(first));
        }
        finally{Directory.Delete(directory,true);}
    }

    private static string Read(string path)
    {
        using var reader=new StreamReader(new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite));
        return reader.ReadToEnd();
    }
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static void ThrowPreviewError()=>throw new InvalidOperationException("preview failure",new IOException("inner evidence"));
}
