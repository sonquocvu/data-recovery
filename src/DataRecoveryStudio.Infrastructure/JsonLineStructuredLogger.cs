using System.Text.Json;
using DataRecoveryStudio.Core;

namespace DataRecoveryStudio.Infrastructure;

public sealed class JsonLineStructuredLogger : IStructuredLogger
{
    private readonly string _logPath;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public JsonLineStructuredLogger(string? logPath = null)
    {
        _logPath = logPath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DataRecoveryStudio",
            "logs",
            $"application-{DateTime.UtcNow:yyyyMMdd}.jsonl");
    }

    public string LogPath => _logPath;

    public async ValueTask LogAsync(
        string level,
        string eventName,
        IReadOnlyDictionary<string, object?> properties,
        CancellationToken cancellationToken = default)
    {
        var entry = new
        {
            timestamp = DateTimeOffset.UtcNow,
            level,
            eventName,
            properties,
        };
        var line = JsonSerializer.Serialize(entry) + Environment.NewLine;

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var directory = Path.GetDirectoryName(_logPath) ?? throw new InvalidOperationException("Log path has no directory.");
            Directory.CreateDirectory(directory);
            await File.AppendAllTextAsync(_logPath, line, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }
}
