using System.Globalization;
using System.Text;
using DataRecoveryStudio.Core;

namespace DataRecoveryStudio.Infrastructure;

public sealed class PdfCandidateValidator : ICarvedCandidateValidator
{
    private static readonly byte[] HeaderPrefix = "%PDF-"u8.ToArray();
    private static readonly byte[] EofToken = "%%EOF"u8.ToArray();

    public async ValueTask<CandidateValidationResult> ValidateAsync(
        IReadOnlyRandomAccessSource source,
        CandidateValidationContext context,
        CancellationToken cancellationToken)
    {
        var reader = new BoundedCandidateReader(source, context);
        var cursor = new CandidateCursor(reader);
        var evidence = new List<string>();
        try
        {
            var header = new byte[8];
            await cursor.ReadExactlyAsync(header, cancellationToken);
            if (!header.AsSpan(0, 5).SequenceEqual(HeaderPrefix) || !IsSupportedVersion(header.AsSpan(5, 3)))
            {
                return ValidationResults.Invalid("PDF_LENGTH_UNCERTAIN", reader.BytesInspected, "Invalid PDF header/version");
            }

            evidence.Add($"PDF version {Encoding.ASCII.GetString(header, 5, 3)}");
            var tail = new Queue<byte>(Math.Min(context.MaximumMetadataStringLength, 4096));
            var match = 0;
            long? lastPlausibleEnd = null;
            var searchLimit = Math.Min(cursor.Length, context.MaximumTerminatorSearchDistance);
            while (cursor.Position < searchLimit)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var value = await cursor.ReadByteAsync(cancellationToken);
                if (tail.Count >= Math.Min(context.MaximumMetadataStringLength, 4096)) tail.Dequeue();
                tail.Enqueue(value);
                match = value == EofToken[match] ? match + 1 : value == EofToken[0] ? 1 : 0;
                if (match != EofToken.Length) continue;

                match = 0;
                var eofEnd = cursor.Position;
                var text = Encoding.ASCII.GetString(tail.ToArray());
                if (!TryFindStartXref(text, out var xrefOffset) || xrefOffset < 0 || xrefOffset >= eofEnd)
                {
                    continue;
                }

                if (!await IsPlausibleXrefAsync(reader, xrefOffset, cancellationToken).ConfigureAwait(false))
                {
                    continue;
                }

                if (!text.Contains("trailer", StringComparison.Ordinal) && !text.Contains("/Type/XRef", StringComparison.Ordinal))
                {
                    continue;
                }

                lastPlausibleEnd = eofEnd;
            }

            if (lastPlausibleEnd is not null)
            {
                evidence.Add("Plausible startxref, trailer, and EOF");
                return ValidationResults.Complete(lastPlausibleEnd.Value, reader.BytesInspected, evidence.ToArray());
            }

            if (cursor.Position >= context.MaximumTerminatorSearchDistance && cursor.Position < cursor.Length)
            {
                return ValidationResults.Budget(reader.BytesInspected, evidence.ToArray());
            }

            return new(
                DeepScanValidationState.SignatureOnly,
                DeepScanCompleteness.LengthUnknown,
                DeepScanConfidence.Low,
                null,
                DeepScanConfidence.Low,
                evidence,
                ["PDF_LENGTH_UNCERTAIN"],
                reader.BytesInspected);
        }
        catch (EndOfStreamException)
        {
            return new(
                DeepScanValidationState.SignatureOnly,
                DeepScanCompleteness.LengthUnknown,
                DeepScanConfidence.Low,
                null,
                DeepScanConfidence.Low,
                evidence,
                ["PDF_LENGTH_UNCERTAIN"],
                reader.BytesInspected);
        }
        catch (CandidateBudgetExceededException)
        {
            return ValidationResults.Budget(reader.BytesInspected, evidence.ToArray());
        }
    }

    private static bool IsSupportedVersion(ReadOnlySpan<byte> version) =>
        version.Length == 3 && version[1] == (byte)'.' &&
        ((version[0] == (byte)'1' && version[2] is >= (byte)'0' and <= (byte)'7') ||
         (version[0] == (byte)'2' && version[2] == (byte)'0'));

    private static bool TryFindStartXref(string text, out long value)
    {
        value = 0;
        var marker = text.LastIndexOf("startxref", StringComparison.Ordinal);
        if (marker < 0) return false;
        var remainder = text.AsSpan(marker + "startxref".Length);
        var start = 0;
        while (start < remainder.Length && remainder[start] is '\r' or '\n' or ' ' or '\t') start++;
        remainder = remainder[start..];
        var length = 0;
        while (length < remainder.Length && char.IsAsciiDigit(remainder[length])) length++;
        return length > 0 && long.TryParse(remainder[..length], NumberStyles.None, CultureInfo.InvariantCulture, out value);
    }

    private static async ValueTask<bool> IsPlausibleXrefAsync(
        BoundedCandidateReader reader,
        long relativeOffset,
        CancellationToken cancellationToken)
    {
        var count = checked((int)Math.Min(32, reader.Length - relativeOffset));
        if (count < 4) return false;
        var bytes = new byte[count];
        await reader.ReadExactlyAsync(relativeOffset, bytes, cancellationToken).ConfigureAwait(false);
        var text = Encoding.ASCII.GetString(bytes);
        return text.StartsWith("xref", StringComparison.Ordinal) || text.Contains(" obj", StringComparison.Ordinal);
    }
}
