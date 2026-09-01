using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace DataRecoveryStudio.Core;

public static class RecoveryFingerprint
{
    public static string ComputeCandidate(DeletedFileCandidate candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        var builder = new StringBuilder(512);
        Append(builder, candidate.MftRecordNumber);
        Append(builder, candidate.SequenceNumber);
        Append(builder, candidate.IsDirectory);
        Append(builder, candidate.Name);
        Append(builder, candidate.OriginalPath);
        Append(builder, candidate.LogicalSize);
        Append(builder, candidate.AllocatedSize);
        Append(builder, (int)candidate.Recoverability);
        foreach (var stream in candidate.DataStreams.OrderBy(stream => stream.Name is null ? 0 : 1).ThenBy(stream => stream.Name, StringComparer.Ordinal))
        {
            Append(builder, stream.Name);
            Append(builder, (int)stream.Storage);
            Append(builder, stream.LogicalSize);
            Append(builder, stream.AllocatedSize);
            Append(builder, stream.InitializedSize);
            Append(builder, stream.MetadataIsComplete);
            Append(builder, stream.IsSparse);
            Append(builder, stream.IsCompressed);
            Append(builder, stream.IsEncrypted);
            Append(builder, stream.AttributeIdentity);
            foreach (var run in stream.Runs)
            {
                Append(builder, run.VirtualCluster);
                Append(builder, run.ClusterCount);
                Append(builder, run.LogicalCluster);
                Append(builder, run.IsSparse);
            }
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())));
    }

    public static string ComputeGeometry(NtfsBootGeometry geometry)
    {
        ArgumentNullException.ThrowIfNull(geometry);
        var value = string.Join('|',
            geometry.BytesPerSector.ToString(CultureInfo.InvariantCulture),
            geometry.SectorsPerCluster.ToString(CultureInfo.InvariantCulture),
            geometry.ClusterSize.ToString(CultureInfo.InvariantCulture),
            geometry.TotalSectors.ToString(CultureInfo.InvariantCulture),
            geometry.MftLogicalClusterNumber.ToString(CultureInfo.InvariantCulture),
            geometry.MftMirrorLogicalClusterNumber.ToString(CultureInfo.InvariantCulture),
            geometry.ClustersPerFileRecordEncoding.ToString(CultureInfo.InvariantCulture),
            geometry.FileRecordSize.ToString(CultureInfo.InvariantCulture));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    }

    private static void Append(StringBuilder builder, object? value)
    {
        var text = value switch
        {
            null => "<null>",
            IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
            _ => value.ToString() ?? string.Empty,
        };
        builder.Append(text.Length.ToString(CultureInfo.InvariantCulture)).Append(':').Append(text).Append('|');
    }
}
