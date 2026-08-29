using System.Collections.ObjectModel;
using DataRecoveryStudio.Core;

namespace DataRecoveryStudio.Application;

public sealed class DeviceCardViewModel : ObservableObject, IDisposable
{
    private readonly ILocalizationService? _localization;
    private readonly EventHandler? _languageChangedHandler;

    public DeviceCardViewModel(StorageDevice device, ILocalizationService? localization = null)
    {
        Device = device;
        PrimaryVolume = device.Volumes.First();
        _localization = localization;
        if (_localization is not null)
        {
            _languageChangedHandler = (_, _) =>
            {
                OnPropertyChanged(nameof(Type));
                OnPropertyChanged(nameof(Status));
                OnPropertyChanged(nameof(UnsupportedReason));
            };
            _localization.LanguageChanged += _languageChangedHandler;
        }
    }

    public StorageDevice Device { get; }
    public Volume PrimaryVolume { get; }
    public string Name => Device.DisplayName;
    public string Model => Device.Model;
    public string Type => Localize(Device.Type switch
    {
        StorageDeviceType.InternalSsd => "Device.Type.InternalSsd",
        StorageDeviceType.InternalHdd => "Device.Type.InternalHdd",
        StorageDeviceType.ExternalDrive => "Device.Type.External",
        StorageDeviceType.UsbDevice => "Device.Type.Removable",
        _ => "Device.Type.Unknown",
    });
    public string Status => Localize(Device.ConnectionStatus switch
    {
        DeviceConnectionStatus.Online when Device.IsSupported => "Device.Status.Connected",
        DeviceConnectionStatus.Online => "Device.Status.Unsupported",
        DeviceConnectionStatus.Disconnected => "Device.Status.Disconnected",
        _ => "Device.Status.AccessDenied",
    });
    public string UnsupportedReason => PrimaryVolume.UnsupportedReasonKey is null
        ? string.Empty
        : Localize(PrimaryVolume.UnsupportedReasonKey);
    public string Capacity => ByteFormatter.Format(Device.CapacityBytes);
    public string Used => ByteFormatter.Format(Device.UsedBytes);
    public string Free => ByteFormatter.Format(Device.FreeBytes);
    public string FileSystem => string.Join(" · ", Device.Volumes.Select(volume =>
        string.IsNullOrWhiteSpace(volume.FileSystem) ? Localize("Common.Unknown") : volume.FileSystem).Distinct(StringComparer.OrdinalIgnoreCase));
    public string MountPath
    {
        get
        {
            var paths = string.Join(", ", Device.Volumes.SelectMany(volume => volume.MountPaths).Distinct(StringComparer.OrdinalIgnoreCase));
            return paths.Length > 0 ? paths : PrimaryVolume.MountPath.Length > 0 ? PrimaryVolume.MountPath : Localize("Device.NoMountPath");
        }
    }
    public double UsedPercentage => Device.CapacityBytes <= 0 ? 0 : Device.UsedBytes * 100d / Device.CapacityBytes;
    public bool IsAvailable => Device.IsSupported;
    public bool IsUnsupported => !IsAvailable;
    public bool IsInternal => Device.Type is StorageDeviceType.InternalSsd or StorageDeviceType.InternalHdd;
    public bool IsExternal => Device.Type == StorageDeviceType.ExternalDrive;
    public bool IsRemovable => Device.Type == StorageDeviceType.UsbDevice;
    public bool IsUnknown => Device.Type == StorageDeviceType.Unknown;

    public void Dispose()
    {
        if (_localization is not null && _languageChangedHandler is not null)
        {
            _localization.LanguageChanged -= _languageChangedHandler;
        }
    }

    private string Localize(string key) => _localization?[key] ?? key;
}

public sealed class DeviceSelectionViewModel : ObservableObject, IDisposable
{
    private readonly IDeviceDiscoveryService _devices;
    private readonly Action<StorageDevice> _selectDevice;
    private readonly ILocalizationService? _localization;
    private CancellationTokenSource? _refreshCancellation;
    private long _refreshGeneration;
    private bool _isLoading;
    private bool _isNavigating;
    private string? _errorMessage;
    private string? _noticeMessage;

    public DeviceSelectionViewModel(IDeviceDiscoveryService devices, Action<StorageDevice> selectDevice, ILocalizationService? localization = null)
    {
        _devices = devices;
        _selectDevice = selectDevice;
        _localization = localization;
        SelectDeviceCommand = new RelayCommand<DeviceCardViewModel>(SelectDevice, CanSelectDevice);
        RefreshCommand = new AsyncRelayCommand(LoadAsync);
    }

    public event Action<IReadOnlyList<StorageDevice>>? DevicesRefreshed;

    public ObservableCollection<DeviceCardViewModel> Devices { get; } = [];
    public RelayCommand<DeviceCardViewModel> SelectDeviceCommand { get; }
    public AsyncRelayCommand RefreshCommand { get; }

    public bool IsLoading
    {
        get => _isLoading;
        private set
        {
            if (SetProperty(ref _isLoading, value))
            {
                OnPropertyChanged(nameof(IsEmpty));
            }
        }
    }

    public string? ErrorMessage
    {
        get => _errorMessage;
        private set
        {
            if (SetProperty(ref _errorMessage, value))
            {
                OnPropertyChanged(nameof(HasError));
            }
        }
    }

    public bool HasError => !string.IsNullOrWhiteSpace(ErrorMessage);

    public string? NoticeMessage
    {
        get => _noticeMessage;
        private set
        {
            if (SetProperty(ref _noticeMessage, value))
            {
                OnPropertyChanged(nameof(HasNotice));
            }
        }
    }

    public bool HasNotice => !string.IsNullOrWhiteSpace(NoticeMessage);

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
    public bool IsEmpty => !IsLoading && Devices.Count == 0 && !HasError;
    public bool HasNoMountedVolumes => !IsLoading && Devices.Count > 0 && Devices.All(card => card.PrimaryVolume.MountPaths.Count == 0);
    public bool HasNoSupportedDevices => !IsLoading && Devices.Count > 0 && !HasNoMountedVolumes && Devices.All(card => !card.IsAvailable);

    public async Task LoadAsync()
    {
        var generation = Interlocked.Increment(ref _refreshGeneration);
        var cancellation = new CancellationTokenSource();
        var previous = Interlocked.Exchange(ref _refreshCancellation, cancellation);
        previous?.Cancel();
        previous?.Dispose();

        IsLoading = true;
        ErrorMessage = null;
        try
        {
            var devices = await _devices.GetDevicesAsync(cancellation.Token).ConfigureAwait(true);
            if (generation != Volatile.Read(ref _refreshGeneration) || cancellation.IsCancellationRequested)
            {
                return;
            }

            var normalized = devices
                .Where(device => device.Volumes.Count > 0)
                .GroupBy(device => device.Volumes[0].VolumeGuidPath, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .ToArray();
            DisposeCards();
            foreach (var device in normalized)
            {
                Devices.Add(new DeviceCardViewModel(device, _localization));
            }

            OnPropertyChanged(nameof(HasDevices));
            OnPropertyChanged(nameof(IsEmpty));
            OnPropertyChanged(nameof(HasNoMountedVolumes));
            OnPropertyChanged(nameof(HasNoSupportedDevices));
            DevicesRefreshed?.Invoke(normalized);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        catch
        {
            if (generation != Volatile.Read(ref _refreshGeneration))
            {
                return;
            }

            DisposeCards();
            ErrorMessage = Localize("Device.DiscoveryError");
            OnPropertyChanged(nameof(HasDevices));
            OnPropertyChanged(nameof(IsEmpty));
            OnPropertyChanged(nameof(HasNoMountedVolumes));
            OnPropertyChanged(nameof(HasNoSupportedDevices));
        }
        finally
        {
            if (generation == Volatile.Read(ref _refreshGeneration))
            {
                IsLoading = false;
                OnPropertyChanged(nameof(HasNoMountedVolumes));
                OnPropertyChanged(nameof(HasNoSupportedDevices));
            }
        }
    }

    public void ShowNotice(string localizationKey) => NoticeMessage = Localize(localizationKey);

    public void ClearNotice() => NoticeMessage = null;

    public void PrepareForDisplay() => IsNavigating = false;

    public void Dispose()
    {
        var cancellation = Interlocked.Exchange(ref _refreshCancellation, null);
        cancellation?.Cancel();
        cancellation?.Dispose();
        DisposeCards();
    }

    private void SelectDevice(DeviceCardViewModel card)
    {
        if (!CanSelectDevice(card))
        {
            return;
        }

        IsNavigating = true;
        NoticeMessage = null;
        _selectDevice(card.Device);
    }

    private bool CanSelectDevice(DeviceCardViewModel card) => card.IsAvailable && !IsNavigating;

    private string Localize(string key) => _localization?[key] ?? key;

    private void DisposeCards()
    {
        foreach (var card in Devices)
        {
            card.Dispose();
        }

        Devices.Clear();
    }
}
