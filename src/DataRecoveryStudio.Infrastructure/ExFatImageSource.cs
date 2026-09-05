using System.Security.Cryptography;
using DataRecoveryStudio.Core;

namespace DataRecoveryStudio.Infrastructure;

/// <summary>The only production exFAT source boundary: ordinary files, read access, read sharing.</summary>
public sealed class ExFatImageSourceFactory : IReadOnlyImageSourceFactory
{
    private readonly RegularFileRandomAccessSourceFactory _inner;
    public ExFatImageSourceFactory() => _inner = new();
    internal ExFatImageSourceFactory(IRegularFileOpener opener) => _inner = new(opener);

    public async ValueTask<IReadOnlyRandomAccessSource> OpenAsync(string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var canonical = ValidatePath(path);
        var source = await _inner.OpenAsync(canonical, cancellationToken).ConfigureAwait(false);
        try
        {
            var bound = new BoundSource((FileRandomAccessSource)source);
            bound.ValidateIdentity(canonical);
            cancellationToken.ThrowIfCancellationRequested();
            return bound;
        }
        catch { await source.DisposeAsync().ConfigureAwait(false); throw; }
    }

    private sealed class BoundSource(FileRandomAccessSource source) : IReadOnlyRandomAccessSource, IRegularFileIdentitySource
    {
        private readonly RegularFileIdentity _identity = RegularFileIdentity.Capture(source.Handle);
        public string FileIdentity => _identity.Key;
        public long Length => source.Length;
        public void ValidateIdentity(string path)
        {
            if (_identity != RegularFileIdentity.Capture(source.Handle) || _identity != RegularFileIdentity.CapturePath(path))
                throw new IOException("SOURCE_CHANGED: the regular image path no longer identifies the held file.");
        }
        public ValueTask ReadExactlyAsync(long offset, Memory<byte> destination, CancellationToken token) => source.ReadExactlyAsync(offset, destination, token);
        public ValueTask DisposeAsync() => source.DisposeAsync();
    }

    internal static string ValidatePath(string path)
    {
        var full = SafeImagePath.GetValidatedFullPath(path);
        var normalized = path.Replace('/', '\\');
        if (normalized.Contains("PhysicalDrive", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("HarddiskVolume", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("GLOBALROOT", StringComparison.OrdinalIgnoreCase) ||
            full.AsSpan(Path.GetPathRoot(full)!.Length).Contains(':') ||
            string.Equals(full.TrimEnd('\\', '/'), Path.GetPathRoot(full)!.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase))
            throw new NotSupportedException("exFAT accepts ordinary image files only.");
        var file = new FileInfo(full);
        if (!file.Exists) throw new FileNotFoundException("The source image does not exist.", full);
        for (FileSystemInfo? entry = file; entry is not null; entry = entry is FileInfo f ? f.Directory : ((DirectoryInfo)entry).Parent)
        {
            if ((entry.Attributes & (FileAttributes.ReparsePoint | FileAttributes.Device)) != 0 ||
                (entry == file && (entry.Attributes & FileAttributes.Directory) != 0))
                throw new NotSupportedException("Reparse points and non-regular sources are unsupported.");
        }
        return full;
    }
}

public sealed class ExFatSourceMetadataProvider : IExFatSourceMetadataProvider
{
    public async ValueTask<ExFatSourceFingerprint> CaptureAsync(string path, IReadOnlyRandomAccessSource source,
        ExFatScanRequest request, CancellationToken cancellationToken)
    {
        request.EffectiveBudget.Validate();
        var work = request.Work ?? new ExFatWork(request.EffectiveBudget);
        work.Check(cancellationToken);
        var canonical = ExFatImageSourceFactory.ValidatePath(path);
        var identity = source as IRegularFileIdentitySource;
        identity?.ValidateIdentity(canonical);
        var before = new FileInfo(canonical);
        var length = before.Length;
        var ticks = before.LastWriteTimeUtc.Ticks;
        if (source.Length != length) throw new IOException("SOURCE_CHANGED: source length differs from the open image.");
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[64 * 1024];
        for (long offset = 0; offset < length;)
        {
            var count = (int)Math.Min(buffer.Length, length - offset);
            work.BeforeRead(count, cancellationToken);
            await source.ReadExactlyAsync(offset, buffer.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
            work.AfterRead(count, true);
            work.Check(cancellationToken);
            hash.AppendData(buffer, 0, count);
            offset += count;
        }
        var after = new FileInfo(canonical);
        identity?.ValidateIdentity(canonical);
        if (length != after.Length || ticks != after.LastWriteTimeUtc.Ticks || source.Length != length)
            throw new IOException("SOURCE_CHANGED: image changed during fingerprinting.");
        return new(canonical, length, ticks, Convert.ToHexString(hash.GetHashAndReset())) { FileIdentity = identity?.FileIdentity };
    }
}
