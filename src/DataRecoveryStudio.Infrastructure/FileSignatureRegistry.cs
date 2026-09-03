using DataRecoveryStudio.Core;

namespace DataRecoveryStudio.Infrastructure;

public sealed class FileSignatureRegistry : IFileSignatureRegistry
{
    public const int MaximumSignatureLength = 64;
    private readonly IReadOnlyList<FileSignatureDescriptor> _descriptors;

    public FileSignatureRegistry(IEnumerable<FileSignatureDescriptor> descriptors)
    {
        ArgumentNullException.ThrowIfNull(descriptors);
        var values = descriptors.ToArray();
        Validate(values);
        _descriptors = Array.AsReadOnly(values
            .OrderByDescending(descriptor => descriptor.Priority)
            .ThenBy(descriptor => descriptor.FormatId, StringComparer.Ordinal)
            .ToArray());
        LongestSignatureLength = _descriptors.SelectMany(item => item.Signatures).Max(item => item.Bytes.Length);
    }

    public IReadOnlyList<FileSignatureDescriptor> Descriptors => _descriptors;
    public int LongestSignatureLength { get; }

    public static FileSignatureRegistry CreateDefault() => new(
        [
            new("PNG", ".png", "image", 500, 45, 128L * 1024 * 1024,
                [new([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A])],
                ["PNG 1.x with bounded standard chunks"], ["unknown critical chunks"], new PngCandidateValidator(), "1"),
            new("JPEG", ".jpg", "image", 400, 20, 128L * 1024 * 1024,
                [new([0xFF, 0xD8, 0xFF])],
                ["baseline DCT", "progressive DCT"], ["arithmetic and uncommon SOF variants"], new JpegCandidateValidator(), "1"),
            new("GIF", ".gif", "image", 300, 14, 128L * 1024 * 1024,
                [new("GIF87a"u8.ToArray()), new("GIF89a"u8.ToArray())],
                ["GIF87a", "GIF89a"], [], new GifCandidateValidator(), "1"),
            new("PDF", ".pdf", "document", 200, 16, 128L * 1024 * 1024,
                [new("%PDF-"u8.ToArray())],
                ["PDF 1.0-1.7", "PDF 2.0", "incremental updates"], ["content usability is not evaluated"], new PdfCandidateValidator(), "1"),
            new("ZIP", ".zip", "archive", 100, 30, 128L * 1024 * 1024,
                [new([0x50, 0x4B, 0x03, 0x04])],
                ["ordinary single-disk ZIP", "data descriptors", "encrypted entry metadata"], ["ZIP64", "multi-disk ZIP"], new ZipCandidateValidator(), "1"),
        ]);

    private static void Validate(IReadOnlyList<FileSignatureDescriptor> descriptors)
    {
        if (descriptors.Count == 0) throw new ArgumentException("At least one file signature descriptor is required.", nameof(descriptors));
        var formatIds = new HashSet<string>(StringComparer.Ordinal);
        var signatures = new Dictionary<string, HashSet<int>>(StringComparer.Ordinal);
        foreach (var descriptor in descriptors)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(descriptor.FormatId);
            ArgumentException.ThrowIfNullOrWhiteSpace(descriptor.SuggestedExtension);
            ArgumentNullException.ThrowIfNull(descriptor.Validator);
            if (descriptor.SuggestedExtension.Length is < 2 or > 16 || descriptor.SuggestedExtension[0] != '.' ||
                descriptor.SuggestedExtension.Skip(1).Any(character => character is not (>= 'a' and <= 'z') and not (>= '0' and <= '9')))
            {
                throw new ArgumentException($"Descriptor '{descriptor.FormatId}' does not declare a safe lowercase extension.", nameof(descriptors));
            }

            if (!formatIds.Add(descriptor.FormatId)) throw new ArgumentException($"Duplicate format ID '{descriptor.FormatId}'.", nameof(descriptors));
            if (descriptor.Signatures is null || descriptor.Signatures.Count == 0 || descriptor.MinimumCandidateSize <= 0 ||
                descriptor.MaximumValidationSize < descriptor.MinimumCandidateSize)
            {
                throw new ArgumentException($"Descriptor '{descriptor.FormatId}' has invalid bounds or signatures.", nameof(descriptors));
            }

            foreach (var signature in descriptor.Signatures)
            {
                if (signature.Bytes.Length is <= 0 or > MaximumSignatureLength || signature.CandidateRelativeOffset < 0 ||
                    signature.CandidateRelativeOffset > 4096)
                {
                    throw new ArgumentException($"Descriptor '{descriptor.FormatId}' has an invalid signature.", nameof(descriptors));
                }

                var key = $"{signature.CandidateRelativeOffset}:{Convert.ToHexString(signature.Bytes.Span)}";
                if (!signatures.TryGetValue(key, out var priorities))
                {
                    priorities = [];
                    signatures[key] = priorities;
                }

                if (!priorities.Add(descriptor.Priority))
                {
                    throw new ArgumentException("Indistinguishable signatures require distinct priorities.", nameof(descriptors));
                }
            }
        }
    }
}
