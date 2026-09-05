namespace DataRecoveryStudio.Core;

// Presentation evidence only: no source offsets, clusters, payload, or recovery authority.
public sealed record LiveExFatMetadata(long ValidDataLength, ExFatNameEvidence NameEvidence,
    ExFatPathState PathState, ExFatLayout Layout, ExFatAllocationEvidence Allocation,
    ExFatRecoverabilityState Recoverability, ushort Attributes, ExFatTimestamp Created,
    ExFatTimestamp Modified, ExFatTimestamp Accessed, bool MetadataDamaged, bool IsPartial);

public sealed record LiveExFatEvidence(string ScannerVersion, bool GeometryValidated,
    bool MainValid, bool BackupValid, bool UsedBackup, int SampleCount, long SampleBytes,
    long BootBytes, long FatBytes, long BitmapBytes, long UpCaseBytes, long DirectoryBytes,
    long ConsistencyBytes, bool BudgetLimited);

public static class LiveExFatValidation
{
    public static bool ValidExtents(IReadOnlyList<VolumeDiskExtent>? extents, long capacity,
        IEnumerable<int> diskNumbers)
    {
        if (extents is null || extents.Count is < 1 or > 128 || capacity <= 0) return false;
        try
        {
            long total = 0;
            foreach (var extent in extents)
            {
                if (extent is null || extent.DiskNumber < 0 || extent.StartingOffset < 0 || extent.Length <= 0) return false;
                _ = checked(extent.StartingOffset + extent.Length);
                total = checked(total + extent.Length);
            }
            foreach (var group in extents.GroupBy(e => e.DiskNumber))
            {
                long end = 0;
                foreach (var extent in group.OrderBy(e => e.StartingOffset))
                {
                    if (extent.StartingOffset < end) return false;
                    end = checked(extent.StartingOffset + extent.Length);
                }
            }
            return total >= capacity && extents.Select(e => e.DiskNumber).ToHashSet().SetEquals(diskNumbers);
        }
        catch (OverflowException) { return false; }
    }

    public static bool ValidCandidate(LiveScanCandidateDto candidate)
    {
        var e = candidate.ExFat;
        return e is not null && candidate.ScannerKind == LiveScanScannerKind.ExFatStandardMetadata &&
            string.Equals(candidate.FileSystem, "exFAT", StringComparison.OrdinalIgnoreCase) &&
            candidate.MftRecordNumber == 0 && candidate.SequenceNumber == 0 && candidate.Streams is { Count: 0 } &&
            candidate.Recoverability == CandidateRecoverability.Unknown && candidate.PathState == CandidatePathState.Invalid &&
            !string.IsNullOrEmpty(candidate.Name) &&
            candidate.IsDeleted && !candidate.IsDirectory && candidate.LogicalSize >= 0 &&
            e.ValidDataLength >= 0 && e.ValidDataLength <= candidate.LogicalSize &&
            Enum.IsDefined(e.NameEvidence) && Enum.IsDefined(e.PathState) && Enum.IsDefined(e.Layout) &&
            Enum.IsDefined(e.Allocation) && Enum.IsDefined(e.Recoverability) && (e.Attributes & ~0x27) == 0 &&
            ValidTimestamp(e.Created) && ValidTimestamp(e.Modified) && ValidTimestamp(e.Accessed) &&
            (!e.MetadataDamaged || e.IsPartial) &&
            (!e.IsPartial || e.Recoverability != ExFatRecoverabilityState.AllocationSuggestsPossibleContent) &&
            candidate.Fat32Kind is null && candidate.Fat32Allocation is null && candidate.Fat32NameState is null &&
            candidate.Fat32PathState is null && candidate.AttributeFlags == 0 && candidate.CreatedAt is null &&
            candidate.ModifiedAt is null && candidate.LastAccessedAt is null;
    }

    private static bool ValidTimestamp(ExFatTimestamp? t) => t is not null && Enum.IsDefined(t.State) && t.State switch
    {
        ExFatTimestampState.Invalid => t.LocalTime is null && t.UtcOffsetMinutes is null,
        ExFatTimestampState.OffsetUnknown => t.LocalTime is { Year: >= 1980 and <= 2107 } && t.UtcOffsetMinutes is null,
        ExFatTimestampState.Valid => t.LocalTime is { Year: >= 1980 and <= 2107 } &&
            t.UtcOffsetMinutes is >= -960 and <= 945 && t.UtcOffsetMinutes % 15 == 0,
        _ => false,
    };
}
