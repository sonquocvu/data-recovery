using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using DataRecoveryStudio.Core;

namespace DataRecoveryStudio.Infrastructure;

internal sealed class RecoveryDestination
{
    private const int MaximumComponentLength = 120;
    private const int MaximumRelativePathLength = 220;
    private static readonly HashSet<string> ReservedNames = BuildReservedNames();
    private readonly string _rootWithSeparator;

    private RecoveryDestination(string root)
    {
        Root = root;
        _rootWithSeparator = Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar;
    }

    public string Root { get; }

    public static RecoveryDestination ValidateAndOpen(
        string requestedRoot,
        string sourceImagePath,
        bool createIfMissing,
        long requiredBytes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(requestedRoot);
        var root = SafeImagePath.GetValidatedFullPath(requestedRoot);
        var source = SafeImagePath.GetValidatedFullPath(sourceImagePath);
        if (string.Equals(root.TrimEnd(Path.DirectorySeparatorChar), source.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
        {
            throw new IOException("The recovery destination cannot be the source image.");
        }

        ValidateExistingAncestors(root);
        if (File.Exists(root))
        {
            throw new IOException("The recovery destination is a file, not a directory.");
        }

        if (!Directory.Exists(root))
        {
            if (!createIfMissing)
            {
                throw new DirectoryNotFoundException("The recovery destination does not exist.");
            }

            Directory.CreateDirectory(root);
        }

        RejectReparsePoint(root);
        var driveRoot = Path.GetPathRoot(root);
        if (!string.IsNullOrWhiteSpace(driveRoot))
        {
            var drive = new DriveInfo(driveRoot);
            if (drive.IsReady && requiredBytes >= 0 && drive.AvailableFreeSpace < requiredBytes)
            {
                throw new IOException("The recovery destination does not have enough estimated available capacity.");
            }
        }

        var probe = Path.Combine(root, $".drs-write-probe-{Guid.NewGuid():N}.tmp");
        try
        {
            using var stream = new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.WriteThrough);
        }
        finally
        {
            if (File.Exists(probe))
            {
                File.Delete(probe);
            }
        }

        return new(root);
    }

    public string PrepareDirectoryAndBasePath(TrustedRecoveryItem item, RecoveryOutputLayout layout)
    {
        var fallback = $"MFT-{item.MftRecordNumber.ToString(CultureInfo.InvariantCulture)}";
        var fileName = SanitizeComponent(item.OriginalName, fallback);
        var components = new List<string>();
        if (layout == RecoveryOutputLayout.OriginalFoldersWhenSafe)
        {
            var sourceComponents = (item.OriginalPath ?? string.Empty)
                .Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar)
                .Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
            foreach (var component in sourceComponents.Take(Math.Max(0, sourceComponents.Length - 1)).Take(32))
            {
                components.Add(SanitizeComponent(component, "folder"));
            }
        }

        components.Add(fileName);
        var relative = Path.Combine(components.ToArray());
        if (relative.Length > MaximumRelativePathLength)
        {
            relative = Path.Combine(components.Take(Math.Max(0, components.Count - 1)).Take(4).ToArray().Append(
                ShortenWithHash(fileName, fallback, MaximumComponentLength)).ToArray());
            if (relative.Length > MaximumRelativePathLength)
            {
                relative = ShortenWithHash(fileName, fallback, MaximumComponentLength);
            }
        }

        var full = EnsureContained(Path.Combine(Root, relative));
        var directory = Path.GetDirectoryName(full) ?? Root;
        CreateDirectoriesWithoutReparsePoints(directory);
        RejectReparsePoint(directory);
        return full;
    }

    public string PrepareFlatBasePath(string trustedGeneratedName, string fallback)
    {
        var fileName = SanitizeComponent(trustedGeneratedName, fallback);
        var full = EnsureContained(Path.Combine(Root, fileName));
        RevalidateParent(full);
        return full;
    }

    public string EnsureContained(string path)
    {
        var full = Path.GetFullPath(path);
        if (!full.StartsWith(_rootWithSeparator, StringComparison.OrdinalIgnoreCase))
        {
            throw new IOException("A recovered path attempted to escape the validated destination root.");
        }

        return full;
    }

    public void RevalidateParent(string path)
    {
        var full = EnsureContained(path);
        var directory = Path.GetDirectoryName(full) ?? throw new IOException("The recovered path has no destination directory.");
        var relative = Path.GetRelativePath(Root, directory);
        var current = Root;
        RejectReparsePoint(current);
        foreach (var component in relative.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, component);
            RejectReparsePoint(current);
        }
    }

    public static string WithCollisionSuffix(string basePath, int attempt)
    {
        if (attempt == 0)
        {
            return basePath;
        }

        var directory = Path.GetDirectoryName(basePath) ?? string.Empty;
        var extension = Path.GetExtension(basePath);
        var stem = Path.GetFileNameWithoutExtension(basePath);
        var suffix = $" ({attempt.ToString(CultureInfo.InvariantCulture)})";
        if (stem.Length + suffix.Length > MaximumComponentLength - extension.Length)
        {
            stem = stem[..Math.Max(1, MaximumComponentLength - extension.Length - suffix.Length)];
        }

        return Path.Combine(directory, stem + suffix + extension);
    }

    internal static string SanitizeComponent(string? value, string fallback)
    {
        var normalized = (value ?? string.Empty).Normalize(NormalizationForm.FormC);
        var invalid = Path.GetInvalidFileNameChars().ToHashSet();
        invalid.UnionWith(['<', '>', ':', '"', '/', '\\', '|', '?', '*']);
        var characters = normalized.Select(character => char.IsControl(character) || invalid.Contains(character) ? '_' : character).ToArray();
        var sanitized = new string(characters).Trim().TrimEnd('.', ' ');
        if (sanitized is "" or "." or "..")
        {
            sanitized = fallback;
        }

        var stem = sanitized.Split('.')[0];
        if (ReservedNames.Contains(stem))
        {
            sanitized = "_" + sanitized;
        }

        return sanitized.Length <= MaximumComponentLength
            ? sanitized
            : ShortenWithHash(sanitized, fallback, MaximumComponentLength);
    }

    private static string ShortenWithHash(string value, string fallback, int maximum)
    {
        var extension = Path.GetExtension(value);
        if (extension.Length > 16)
        {
            extension = string.Empty;
        }

        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..12];
        var prefixLength = Math.Max(1, maximum - extension.Length - hash.Length - 1);
        var prefix = Path.GetFileNameWithoutExtension(value);
        if (prefix.Length > prefixLength)
        {
            prefix = prefix[..prefixLength];
        }

        if (string.IsNullOrWhiteSpace(prefix))
        {
            prefix = fallback[..Math.Min(fallback.Length, prefixLength)];
        }

        return $"{prefix}-{hash}{extension}";
    }

    private void CreateDirectoriesWithoutReparsePoints(string directory)
    {
        var relative = Path.GetRelativePath(Root, directory);
        if (relative == ".")
        {
            return;
        }

        var current = Root;
        foreach (var component in relative.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = EnsureContained(Path.Combine(current, component));
            if (Directory.Exists(current))
            {
                RejectReparsePoint(current);
                continue;
            }

            Directory.CreateDirectory(current);
            RejectReparsePoint(current);
        }
    }

    private static void ValidateExistingAncestors(string path)
    {
        var pathRoot = Path.GetPathRoot(path) ?? throw new IOException("The destination has no ordinary filesystem root.");
        var current = pathRoot;
        if (Directory.Exists(current))
        {
            RejectReparsePoint(current);
        }

        var relative = path[pathRoot.Length..];
        foreach (var component in relative.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, component);
            if (Directory.Exists(current))
            {
                RejectReparsePoint(current);
                continue;
            }

            if (File.Exists(current))
            {
                break;
            }
        }
    }

    private static void RejectReparsePoint(string path)
    {
        var attributes = File.GetAttributes(path);
        if ((attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new IOException("Recovery destinations cannot contain reparse points or symbolic links.");
        }
    }

    private static HashSet<string> BuildReservedNames()
    {
        var values = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "CON", "PRN", "AUX", "NUL", "CLOCK$" };
        for (var index = 1; index <= 9; index++)
        {
            values.Add($"COM{index}");
            values.Add($"LPT{index}");
        }

        return values;
    }
}
