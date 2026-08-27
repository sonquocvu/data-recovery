using System.Text.Json;
using DataRecoveryStudio.Core;

namespace DataRecoveryStudio.Infrastructure;

public sealed class JsonSettingsStore : ISettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly string _filePath;
    private readonly IStructuredLogger? _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public JsonSettingsStore(string? filePath = null, IStructuredLogger? logger = null)
    {
        _filePath = filePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DataRecoveryStudio",
            "settings.json");
        _logger = logger;
    }

    public async Task<AppSettings> LoadAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!File.Exists(_filePath))
            {
                return AppSettings.Default;
            }

            await using var stream = File.OpenRead(_filePath);
            var settings = await JsonSerializer.DeserializeAsync<AppSettings>(stream, JsonOptions, cancellationToken).ConfigureAwait(false);
            return Normalize(settings);
        }
        catch (JsonException exception)
        {
            await LogLoadFailureAsync(exception, "InvalidJson", cancellationToken).ConfigureAwait(false);
            return AppSettings.Default;
        }
        catch (IOException exception)
        {
            await LogLoadFailureAsync(exception, "ReadFailure", cancellationToken).ConfigureAwait(false);
            return AppSettings.Default;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveAsync(AppSettings settings, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var directory = Path.GetDirectoryName(_filePath) ?? throw new InvalidOperationException("Settings path has no directory.");
            Directory.CreateDirectory(directory);
            var temporaryPath = _filePath + ".tmp";
            await using (var stream = new FileStream(temporaryPath, FileMode.Create, FileAccess.Write, FileShare.None, 4096, true))
            {
                await JsonSerializer.SerializeAsync(stream, Normalize(settings), JsonOptions, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            File.Move(temporaryPath, _filePath, true);
        }
        finally
        {
            _gate.Release();
        }
    }

    private static AppSettings Normalize(AppSettings? settings)
    {
        if (settings is null)
        {
            return AppSettings.Default;
        }

        var language = settings.LanguageCode is "vi-VN" ? "vi-VN" : "en-US";
        return settings with { LanguageCode = language };
    }

    private async Task LogLoadFailureAsync(Exception exception, string reason, CancellationToken cancellationToken)
    {
        if (_logger is null)
        {
            return;
        }

        try
        {
            await _logger.LogAsync(
                "Warning",
                "SettingsLoadFailed",
                new Dictionary<string, object?>
                {
                    ["reason"] = reason,
                    ["exceptionType"] = exception.GetType().FullName,
                    ["message"] = exception.Message,
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception loggingException) when (loggingException is IOException or UnauthorizedAccessException)
        {
            System.Diagnostics.Debug.WriteLine($"Unable to write settings diagnostic: {loggingException.Message}");
        }
    }
}
