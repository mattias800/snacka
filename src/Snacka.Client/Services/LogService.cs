using System.Text;

namespace Snacka.Client.Services;

/// <summary>
/// Service for capturing application logs to a file for bug reporting.
/// </summary>
public interface ILogService
{
    /// <summary>
    /// Gets the path to the log file.
    /// </summary>
    string LogFilePath { get; }

    /// <summary>
    /// Gets the directory containing log files.
    /// </summary>
    string LogDirectory { get; }

    /// <summary>
    /// Reads and returns the current log content.
    /// </summary>
    string GetLogs();

    /// <summary>
    /// Clears the log file.
    /// </summary>
    void ClearLogs();
}

/// <summary>
/// A TextWriter that writes to both the original console and a log file.
/// </summary>
public class LoggingTextWriter : TextWriter
{
    private readonly TextWriter _originalWriter;
    private readonly StreamWriter _fileWriter;
    private readonly object _lock = new();

    public LoggingTextWriter(TextWriter originalWriter, string logFilePath)
    {
        _originalWriter = originalWriter;

        // Open file with shared read access so logs can be read while app is running
        var fileStream = new FileStream(logFilePath, FileMode.Append, FileAccess.Write, FileShare.Read);
        _fileWriter = new StreamWriter(fileStream) { AutoFlush = true };
    }

    public override Encoding Encoding => _originalWriter.Encoding;

    public override void Write(char value)
    {
        lock (_lock)
        {
            _originalWriter.Write(value);
            _fileWriter.Write(value);
        }
    }

    public override void Write(string? value)
    {
        lock (_lock)
        {
            _originalWriter.Write(value);
            _fileWriter.Write(value);
        }
    }

    public override void WriteLine(string? value)
    {
        var timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
        var line = $"[{timestamp}] {value}";

        lock (_lock)
        {
            _originalWriter.WriteLine(value); // Original console without timestamp
            _fileWriter.WriteLine(line);      // File with timestamp
        }
    }

    public override void WriteLine()
    {
        lock (_lock)
        {
            _originalWriter.WriteLine();
            _fileWriter.WriteLine();
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _fileWriter.Dispose();
        }
        base.Dispose(disposing);
    }
}

public class LogService : ILogService
{
    private static LogService? _instance;
    private readonly string _logDirectory;
    private readonly string _logFilePath;

    public static LogService Instance => _instance ?? throw new InvalidOperationException("LogService not initialized");

    public string LogFilePath => _logFilePath;
    public string LogDirectory => _logDirectory;

    private LogService(string? profileName)
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        _logDirectory = Path.Combine(appData, "Snacka");

        if (!string.IsNullOrEmpty(profileName))
        {
            _logDirectory = Path.Combine(_logDirectory, $"profile-{profileName}");
        }

        _logDirectory = Path.Combine(_logDirectory, "logs");
        Directory.CreateDirectory(_logDirectory);

        // Use date-based log file, rotate daily
        var dateStr = DateTime.Now.ToString("yyyy-MM-dd");
        _logFilePath = Path.Combine(_logDirectory, $"snacka-{dateStr}.log");

        // Clean up old log files (keep last 7 days)
        CleanupOldLogs(7);
    }

    /// <summary>
    /// Initialize the log service and redirect console output.
    /// Call this early in Program.Main().
    /// </summary>
    public static void Initialize(string? profileName = null)
    {
        _instance = new LogService(profileName);

        // Redirect Console.Out and Console.Error to our logging writer
        var loggingWriter = new LoggingTextWriter(Console.Out, _instance._logFilePath);
        Console.SetOut(loggingWriter);
        Console.SetError(loggingWriter);

        // Write startup marker
        Console.WriteLine("=== Snacka Client Started ===");
        Console.WriteLine($"Log file: {_instance._logFilePath}");
    }

    public string GetLogs()
    {
        try
        {
            // Read with shared access since we're writing to it
            using var fileStream = new FileStream(_logFilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(fileStream);
            return reader.ReadToEnd();
        }
        catch (FileNotFoundException)
        {
            return "No logs available.";
        }
        catch (Exception ex)
        {
            return $"Error reading logs: {ex.Message}";
        }
    }

    public void ClearLogs()
    {
        try
        {
            // We can't truncate the file while it's open, so just write a marker
            Console.WriteLine("=== Logs cleared by user ===");
        }
        catch
        {
            // Ignore errors
        }
    }

    private void CleanupOldLogs(int keepDays)
    {
        try
        {
            var cutoffDate = DateTime.Now.AddDays(-keepDays);
            var logFiles = Directory.GetFiles(_logDirectory, "snacka-*.log");

            foreach (var file in logFiles)
            {
                var fileInfo = new FileInfo(file);
                if (fileInfo.LastWriteTime < cutoffDate)
                {
                    try
                    {
                        File.Delete(file);
                    }
                    catch
                    {
                        // Ignore deletion errors
                    }
                }
            }
        }
        catch
        {
            // Ignore cleanup errors
        }
    }
}
