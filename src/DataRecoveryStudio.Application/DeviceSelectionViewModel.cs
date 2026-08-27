using System.Collections.ObjectModel;
using DataRecoveryStudio.Core;

namespace DataRecoveryStudio.Application;

public sealed class DeviceCardViewModel
{
    public DeviceCardViewModel(StorageDevice device)
    {
        Device = device;
        PrimaryVolume = device.Volumes.First();
    }

    public StorageDevice Device { get; }
    public Volume PrimaryVolume { get; }
    public string Name => Device.DisplayName;
    public string Model => Device.Model;
    public string Type => Device.Type switch
    {
        StorageDeviceType.Internal => "Internal drive",
        StorageDeviceType.External => "External drive",
        _ => "Removable USB",
    };
    public string Status => Device.ConnectionStatus == DeviceConnectionStatus.Online ? "Connected" : Device.ConnectionStatus.ToString();
    public string Capacity => ByteFormatter.Format(Device.CapacityBytes);
    public string Used => ByteFormatter.Format(Device.UsedBytes);
    public string Free => ByteFormatter.Format(Device.FreeBytes);
    public string FileSystem => string.Join(" · ", Device.Volumes.Select(volume => volume.FileSystem).Distinct(StringComparer.OrdinalIgnoreCase));
    public string MountPath => string.Join(", ", Device.Volumes.Select(volume => volume.MountPath));
    public double UsedPercentage => Device.CapacityBytes <= 0 ? 0 : Device.UsedBytes * 100d / Device.CapacityBytes;
}

public sealed class DeviceSelectionViewModel : ObservableObject
{
    private readonly IDeviceDiscoveryService _devices;
    private readonly Action<StorageDevice> _selectDevice;
    private bool _isLoading;
    private string? _errorMessage;

    public DeviceSelectionViewModel(IDeviceDiscoveryService devices, Action<StorageDevice> selectDevice)
    {
        _devices = devices;
        _selectDevice = selectDevice;
        SelectDeviceCommand = new RelayCommand<DeviceCardViewModel>(card => _selectDevice(card.Device));
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
                Devices.Add(new DeviceCardViewModel(device));
            }
        }
        catch (Exception exception)
        {
            ErrorMessage = exception.Message;
        }
        finally
        {
            IsLoading = false;
        }
    }
}
