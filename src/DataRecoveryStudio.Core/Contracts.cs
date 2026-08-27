namespace DataRecoveryStudio.Core;

public interface IDeviceDiscoveryService
{
    Task<IReadOnlyList<StorageDevice>> GetDevicesAsync(CancellationToken cancellationToken);
}

public interface IScanService
{
    Task<ScanSession> ScanAsync(
        StorageDevice source,
        ScanModeKind mode,
        IProgress<ScanProgress> progress,
        CancellationToken cancellationToken);
}

public interface IRecoveryCatalogService
{
    Task<IReadOnlyList<RecoverableFile>> GetResultsAsync(
        ScanSession session,
        CancellationToken cancellationToken);
}

public interface ISettingsStore
{
    Task<AppSettings> LoadAsync(CancellationToken cancellationToken);

    Task SaveAsync(AppSettings settings, CancellationToken cancellationToken);
}

public interface ILocalizationService
{
    event EventHandler? LanguageChanged;

    string LanguageCode { get; }

    string this[string key] { get; }

    void SetLanguage(string languageCode);
}

public interface IStructuredLogger
{
    ValueTask LogAsync(
        string level,
        string eventName,
        IReadOnlyDictionary<string, object?> properties,
        CancellationToken cancellationToken = default);
}
