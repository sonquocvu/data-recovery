namespace DataRecoveryStudio.Core;

public enum DestinationValidationError
{
    None,
    MissingDestination,
    SamePhysicalDevice,
    InsufficientSpace,
}

public sealed record DestinationValidationResult(bool IsValid, DestinationValidationError Error)
{
    public static DestinationValidationResult Valid { get; } = new(true, DestinationValidationError.None);
}

public static class RecoveryDestinationPolicy
{
    public static DestinationValidationResult Validate(
        PhysicalDeviceId sourceDeviceId,
        PhysicalDeviceId? destinationDeviceId,
        long requiredBytes,
        long availableBytes)
    {
        if (destinationDeviceId is null)
        {
            return new(false, DestinationValidationError.MissingDestination);
        }

        if (sourceDeviceId == destinationDeviceId.Value)
        {
            return new(false, DestinationValidationError.SamePhysicalDevice);
        }

        if (requiredBytes < 0 || availableBytes < requiredBytes)
        {
            return new(false, DestinationValidationError.InsufficientSpace);
        }

        return DestinationValidationResult.Valid;
    }

    public static DestinationValidationResult Validate(
        IReadOnlySet<PhysicalDeviceId> sourceDeviceIds,
        IReadOnlySet<PhysicalDeviceId>? destinationDeviceIds,
        long requiredBytes,
        long availableBytes)
    {
        ArgumentNullException.ThrowIfNull(sourceDeviceIds);

        if (destinationDeviceIds is null || destinationDeviceIds.Count == 0)
        {
            return new(false, DestinationValidationError.MissingDestination);
        }

        if (sourceDeviceIds.Overlaps(destinationDeviceIds))
        {
            return new(false, DestinationValidationError.SamePhysicalDevice);
        }

        if (requiredBytes < 0 || availableBytes < requiredBytes)
        {
            return new(false, DestinationValidationError.InsufficientSpace);
        }

        return DestinationValidationResult.Valid;
    }
}
