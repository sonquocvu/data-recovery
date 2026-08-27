using DataRecoveryStudio.Core;

namespace DataRecoveryStudio.Application;

public sealed record DestinationOption(
    PhysicalDeviceId DeviceId,
    string DisplayName,
    string Path,
    long AvailableBytes)
{
    public string Available => ByteFormatter.Format(AvailableBytes);
}

public sealed class DestinationViewModel : ObservableObject
{
    private readonly PhysicalDeviceId _sourceDeviceId;
    private readonly long _requiredBytes;
    private DestinationOption? _selectedDestination;
    private DestinationValidationResult _validation;

    public DestinationViewModel(
        PhysicalDeviceId sourceDeviceId,
        string sourceDisplayName,
        string sourceLocation,
        long requiredBytes,
        IReadOnlyList<DestinationOption> destinations,
        ILocalizationService? localization = null)
    {
        _sourceDeviceId = sourceDeviceId;
        _requiredBytes = requiredBytes;
        Destinations = destinations;
        SourceDisplayName = sourceDisplayName;
        SourceLocation = sourceLocation;
        Localization = localization is null ? null : new LocalizedText(localization);
        _validation = RecoveryDestinationPolicy.Validate(sourceDeviceId, null, requiredBytes, 0);
    }

    public IReadOnlyList<DestinationOption> Destinations { get; }
    public PhysicalDeviceId SourceDeviceId => _sourceDeviceId;
    public string SourceDisplayName { get; }
    public string SourceLocation { get; }
    public LocalizedText? Localization { get; }
    public string RequiredSpace => ByteFormatter.Format(_requiredBytes);
    public bool IsValid => _validation.IsValid;
    public string ValidationMessage => _validation.Error switch
    {
        DestinationValidationError.SamePhysicalDevice => Localize("Destination.Error.SameDevice"),
        DestinationValidationError.InsufficientSpace => Localize("Destination.Error.Space"),
        DestinationValidationError.MissingDestination => Localize("Destination.Error.Missing"),
        _ => Localize("Destination.Valid"),
    };

    public DestinationOption? SelectedDestination
    {
        get => _selectedDestination;
        set
        {
            if (SetProperty(ref _selectedDestination, value))
            {
                _validation = RecoveryDestinationPolicy.Validate(
                    _sourceDeviceId,
                    value?.DeviceId,
                    _requiredBytes,
                    value?.AvailableBytes ?? 0);
                OnPropertyChanged(nameof(IsValid));
                OnPropertyChanged(nameof(ValidationMessage));
            }
        }
    }

    private string Localize(string key) => Localization?[key] ?? key;
}
