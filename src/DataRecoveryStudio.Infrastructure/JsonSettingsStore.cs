using System.Text.Json;
using DataRecoveryStudio.Core;

namespace DataRecoveryStudio.Infrastructure;

public sealed class JsonSettingsStore : ISettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly string _filePath;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public JsonSettingsStore(string? filePath = null)
    {
        _filePath = filePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DataRecoveryStudio",
            "settings.json");
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
        catch (JsonException)
        {
            return AppSettings.Default;
        }
        catch (IOException)
        {
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
}
