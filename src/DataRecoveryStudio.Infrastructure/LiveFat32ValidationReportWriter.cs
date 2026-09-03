using System.Text.Json;
using System.Text.Json.Serialization;
using DataRecoveryStudio.Core;

namespace DataRecoveryStudio.Infrastructure;

public sealed class LiveFat32ValidationReportWriter
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
    };

    public async Task<string> WriteAsync(
        LiveFat32ValidationReport report,
        string outputDirectory,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        var output = Path.GetFullPath(outputDirectory);
        EnsureOrdinaryDiagnosticsPath(output, report.Target?.MountPath);
        Directory.CreateDirectory(output);
        EnsureOrdinaryDiagnosticsPath(output, report.Target?.MountPath);

        var timestamp = report.StartedAt.UtcDateTime.ToString("yyyyMMddTHHmmssfffZ", System.Globalization.CultureInfo.InvariantCulture);
        var fileName = $"live-fat32-validation-{timestamp}-{Guid.NewGuid():N}.json";
        var finalPath = Path.Combine(output, fileName);
        var temporaryPath = Path.Combine(output, $".{fileName}.tmp");
        try
        {
            await using (var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                16 * 1024,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, report, JsonOptions, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            File.Move(temporaryPath, finalPath, overwrite: false);
            return finalPath;
        }
        catch
        {
            try { File.Delete(temporaryPath); } catch { }
            throw;
        }
    }

    private static void EnsureOrdinaryDiagnosticsPath(string output, string? targetMountPath)
    {
        var root = Path.GetPathRoot(output);
        if (string.IsNullOrWhiteSpace(root) || output.Equals(root, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The validation report requires a dedicated diagnostics directory.");
        if (!string.IsNullOrWhiteSpace(targetMountPath))
        {
            var targetRoot = Path.GetPathRoot(Path.GetFullPath(targetMountPath));
            if (!string.IsNullOrWhiteSpace(targetRoot) && root.Equals(targetRoot, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The validation report cannot be written to the scanned volume.");
        }

        for (var current = output; current is not null; current = Path.GetDirectoryName(current))
        {
            if (!Directory.Exists(current)) continue;
            var attributes = File.GetAttributes(current);
            if ((attributes & (FileAttributes.ReparsePoint | FileAttributes.Device)) != 0)
                throw new InvalidOperationException("The validation report path must be an ordinary directory without reparse points.");
        }
    }
}
