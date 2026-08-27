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

    public DestinationViewModel(PhysicalDeviceId sourceDeviceId, long requiredBytes, IReadOnlyList<DestinationOption> destinations)
    {
        _sourceDeviceId = sourceDeviceId;
        _requiredBytes = requiredBytes;
        Destinations = destinations;
        _validation = RecoveryDestinationPolicy.Validate(sourceDeviceId, null, requiredBytes, 0);
    }

    public IReadOnlyList<DestinationOption> Destinations { get; }
    public string RequiredSpace => ByteFormatter.Format(_requiredBytes);
    public bool IsValid => _validation.IsValid;
    public string ValidationMessage => _validation.Error switch
    {
        DestinationValidationError.SamePhysicalDevice => "Choose a destination on a different physical device.",
        DestinationValidationError.InsufficientSpace => "The selected destination does not have enough free space.",
        DestinationValidationError.MissingDestination => "Select a recovery destination.",
        _ => "Destination is safe for this mock recovery.",
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
}
