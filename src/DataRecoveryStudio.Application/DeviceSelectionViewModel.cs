using System.Collections.ObjectModel;
using DataRecoveryStudio.Core;

namespace DataRecoveryStudio.Application;

public sealed class DeviceCardViewModel : ObservableObject
{
    public DeviceCardViewModel(StorageDevice device, ILocalizationService? localization = null)
    {
        Device = device;
        PrimaryVolume = device.Volumes.First();
        _localization = localization;
        if (_localization is not null)
        {
            _localization.LanguageChanged += (_, _) =>
            {
                OnPropertyChanged(nameof(Type));
                OnPropertyChanged(nameof(Status));
            };
        }
    }

    private readonly ILocalizationService? _localization;

    public StorageDevice Device { get; }
    public Volume PrimaryVolume { get; }
    public string Name => Device.DisplayName;
    public string Model => Device.Model;
    public string Type => Localize(Device.Type switch
    {
        StorageDeviceType.Internal => "Device.Type.Internal",
        StorageDeviceType.External => "Device.Type.External",
        _ => "Device.Type.Removable",
    });
    public string Status => Localize(Device.ConnectionStatus switch
    {
        DeviceConnectionStatus.Online => "Device.Status.Connected",
        DeviceConnectionStatus.Disconnected => "Device.Status.Disconnected",
        _ => "Device.Status.AccessDenied",
    });
    public string Capacity => ByteFormatter.Format(Device.CapacityBytes);
    public string Used => ByteFormatter.Format(Device.UsedBytes);
    public string Free => ByteFormatter.Format(Device.FreeBytes);
    public string FileSystem => string.Join(" · ", Device.Volumes.Select(volume => volume.FileSystem).Distinct(StringComparer.OrdinalIgnoreCase));
    public string MountPath => string.Join(", ", Device.Volumes.Select(volume => volume.MountPath));
    public double UsedPercentage => Device.CapacityBytes <= 0 ? 0 : Device.UsedBytes * 100d / Device.CapacityBytes;
    public bool IsAvailable => Device.ConnectionStatus == DeviceConnectionStatus.Online;
    public bool IsInternal => Device.Type == StorageDeviceType.Internal;
    public bool IsExternal => Device.Type == StorageDeviceType.External;
    public bool IsRemovable => Device.Type == StorageDeviceType.RemovableUsb;

    private string Localize(string key) => _localization?[key] ?? key;
}

public sealed class DeviceSelectionViewModel : ObservableObject
{
    private readonly IDeviceDiscoveryService _devices;
    private readonly Action<StorageDevice> _selectDevice;
    private readonly ILocalizationService? _localization;
    private bool _isLoading;
    private bool _isNavigating;
    private string? _errorMessage;

    public DeviceSelectionViewModel(IDeviceDiscoveryService devices, Action<StorageDevice> selectDevice, ILocalizationService? localization = null)
    {
        _devices = devices;
        _selectDevice = selectDevice;
        _localization = localization;
        SelectDeviceCommand = new RelayCommand<DeviceCardViewModel>(SelectDevice, CanSelectDevice);
        RefreshCommand = new AsyncRelayCommand(LoadAsync);
    }

    public ObservableCollection<DeviceCardViewModel> Devices { get; } = [];
    public RelayCommand<DeviceCardViewModel> SelectDeviceCommand { get; }
    public AsyncRelayCommand RefreshCommand { get; }

    public bool IsLoading
    {
        get => _isLoading;
        private set => SetProperty(ref _isLoading, value);
    }

    public string? ErrorMessage
    {
        get => _errorMessage;
        private set => SetProperty(ref _errorMessage, value);
    }

    public bool IsNavigating
    {
        get => _isNavigating;
        private set
        {
            if (SetProperty(ref _isNavigating, value))
            {
                SelectDeviceCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public bool HasDevices => Devices.Count > 0;
    public bool IsEmpty => !IsLoading && Devices.Count == 0;

    public async Task LoadAsync()
    {
        IsLoading = true;
        ErrorMessage = null;
        try
        {
            var devices = await _devices.GetDevicesAsync(CancellationToken.None).ConfigureAwait(true);
            Devices.Clear();
            foreach (var device in devices)
            {
                Devices.Add(new DeviceCardViewModel(device, _localization));
            }

            OnPropertyChanged(nameof(HasDevices));
            OnPropertyChanged(nameof(IsEmpty));
        }
        catch (Exception exception)
        {
            ErrorMessage = exception.Message;
        }
        finally
        {
            IsLoading = false;
            OnPropertyChanged(nameof(IsEmpty));
        }
    }

    private void SelectDevice(DeviceCardViewModel card)
    {
        if (!CanSelectDevice(card))
        {
            return;
        }

        IsNavigating = true;
        _selectDevice(card.Device);
    }

    private bool CanSelectDevice(DeviceCardViewModel card) => card.IsAvailable && !IsNavigating;

    public void PrepareForDisplay() => IsNavigating = false;
}
