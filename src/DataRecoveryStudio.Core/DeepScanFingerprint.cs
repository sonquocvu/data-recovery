using System.Security.Cryptography;
using System.Text;

namespace DataRecoveryStudio.Core;

public static class DeepScanFingerprint
{
    public static string ComputeEvidence(IEnumerable<string> evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        var normalized = string.Join('\n', evidence.Select(item => item ?? string.Empty));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)));
    }
}
