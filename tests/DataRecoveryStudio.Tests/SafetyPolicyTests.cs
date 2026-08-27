using DataRecoveryStudio.Core;

namespace DataRecoveryStudio.Tests;

public sealed class SafetyPolicyTests
{
    [Fact]
    public void SamePhysicalDevice_IsBlocked_EvenWhenDestinationPathDiffers()
    {
        var source = new PhysicalDeviceId("physical:disk:42");

        var result = RecoveryDestinationPolicy.Validate(source, source, 100, 10_000);

        Assert.False(result.IsValid);
        Assert.Equal(DestinationValidationError.SamePhysicalDevice, result.Error);
    }

    [Fact]
    public void DifferentDevice_WithSufficientSpace_IsAllowed()
    {
        var result = RecoveryDestinationPolicy.Validate(
            new PhysicalDeviceId("physical:source"),
            new PhysicalDeviceId("physical:destination"),
            5_000,
            5_000);

        Assert.Equal(DestinationValidationResult.Valid, result);
    }

    [Fact]
    public void InsufficientSpace_IsBlocked()
    {
        var result = RecoveryDestinationPolicy.Validate(
            new PhysicalDeviceId("physical:source"),
            new PhysicalDeviceId("physical:destination"),
            5_001,
            5_000);

        Assert.Equal(DestinationValidationError.InsufficientSpace, result.Error);
    }
}
