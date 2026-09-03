using System.Globalization;
using DataRecoveryStudio.Core;

namespace DataRecoveryStudio.Infrastructure;

public sealed class Fat32RecoveryPlanResolver : IFat32RecoveryPlanResolver
{
    public string ResolveOutputName(Fat32DeletedCandidate candidate, uint parentDirectoryCluster, long directorySlotSourceOffset)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        var fallback = $"FAT32_Deleted_{parentDirectoryCluster.ToString("X8", CultureInfo.InvariantCulture)}_{directorySlotSourceOffset.ToString("X16", CultureInfo.InvariantCulture)}.bin";
        var proposed = candidate.NameState switch
        {
            Fat32NameState.VerifiedLongName or Fat32NameState.ProbableDeletedLongName => candidate.LongNameEvidence?.Name,
            Fat32NameState.ShortNameWithMissingFirstCharacter => candidate.ShortName,
            _ => fallback,
        };
        return RecoveryDestination.SanitizeComponent(proposed, fallback);
    }
}
