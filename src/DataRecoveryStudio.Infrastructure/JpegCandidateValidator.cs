using DataRecoveryStudio.Core;

namespace DataRecoveryStudio.Infrastructure;

public sealed class JpegCandidateValidator : ICarvedCandidateValidator
{
    public async ValueTask<CandidateValidationResult> ValidateAsync(
        IReadOnlyRandomAccessSource source,
        CandidateValidationContext context,
        CancellationToken cancellationToken)
    {
        var reader = new BoundedCandidateReader(source, context);
        var cursor = new CandidateCursor(reader);
        var evidence = new List<string>();
        var markerCount = 0;
        var sawFrame = false;
        var sawScan = false;
        var progressive = false;
        try
        {
            if (await cursor.ReadByteAsync(cancellationToken) != 0xFF || await cursor.ReadByteAsync(cancellationToken) != 0xD8)
            {
                return ValidationResults.Invalid("JPEG_MALFORMED_MARKER", reader.BytesInspected, "SOI missing");
            }

            evidence.Add("JPEG SOI");
            int? pendingMarker = null;
            while (pendingMarker is not null || cursor.Position < cursor.Length)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (++markerCount > context.MaximumStructuralElements)
                {
                    throw new CandidateBudgetExceededException("StructuralElements");
                }

                var marker = pendingMarker ?? await ReadMarkerAsync(cursor, cancellationToken);
                pendingMarker = null;
                if (marker < 0 || marker == 0xD8 || marker is >= 0xD0 and <= 0xD7)
                {
                    return ValidationResults.Invalid("JPEG_MALFORMED_MARKER", reader.BytesInspected, evidence.ToArray());
                }

                if (marker == 0xD9)
                {
                    if (!sawFrame || !sawScan)
                    {
                        return ValidationResults.Invalid("JPEG_MALFORMED_MARKER", reader.BytesInspected, evidence.ToArray());
                    }

                    evidence.Add("JPEG EOI");
                    if (progressive) evidence.Add("Progressive SOF2");
                    return ValidationResults.Complete(cursor.Position, reader.BytesInspected, evidence.ToArray());
                }

                if (marker == 0x01)
                {
                    continue;
                }

                var lengthBytes = new byte[2];
                await cursor.ReadExactlyAsync(lengthBytes, cancellationToken);
                var segmentLength = BinaryRead.UInt16Big(lengthBytes);
                if (segmentLength < 2)
                {
                    return ValidationResults.Invalid("JPEG_MALFORMED_MARKER", reader.BytesInspected, evidence.ToArray());
                }

                var payloadLength = segmentLength - 2;
                if (marker is 0xC0 or 0xC2)
                {
                    if (payloadLength < 6)
                    {
                        return ValidationResults.Invalid("JPEG_MALFORMED_MARKER", reader.BytesInspected, evidence.ToArray());
                    }

                    var frame = new byte[6];
                    await cursor.ReadExactlyAsync(frame, cancellationToken);
                    if (frame[0] != 8 || BinaryRead.UInt16Big(frame.AsSpan(1, 2)) == 0 || BinaryRead.UInt16Big(frame.AsSpan(3, 2)) == 0 || frame[5] == 0)
                    {
                        return ValidationResults.Invalid("JPEG_MALFORMED_MARKER", reader.BytesInspected, evidence.ToArray());
                    }

                    await cursor.SkipAsync(payloadLength - frame.Length, cancellationToken);
                    sawFrame = true;
                    progressive |= marker == 0xC2;
                    evidence.Add(marker == 0xC2 ? "JPEG progressive frame" : "JPEG baseline frame");
                    continue;
                }

                if (IsUnsupportedFrame(marker))
                {
                    return new(
                        DeepScanValidationState.UnsupportedVariant,
                        DeepScanCompleteness.LengthUnknown,
                        DeepScanConfidence.Low,
                        null,
                        DeepScanConfidence.Low,
                        [.. evidence, $"Unsupported JPEG frame marker FF{marker:X2}"],
                        ["JPEG_UNSUPPORTED_VARIANT"],
                        reader.BytesInspected);
                }

                if (marker == 0xDA)
                {
                    if (!sawFrame || payloadLength < 3)
                    {
                        return ValidationResults.Invalid("JPEG_MALFORMED_MARKER", reader.BytesInspected, evidence.ToArray());
                    }

                    await cursor.SkipAsync(payloadLength, cancellationToken);
                    sawScan = true;
                    evidence.Add("JPEG SOS and entropy stream");
                    pendingMarker = await ScanEntropyAsync(cursor, context, cancellationToken);
                    if (pendingMarker is null)
                    {
                        return ValidationResults.Truncated(reader.BytesInspected, reader.Length, evidence.ToArray());
                    }

                    continue;
                }

                await cursor.SkipAsync(payloadLength, cancellationToken);
            }

            return sawFrame ? ValidationResults.Truncated(reader.BytesInspected, reader.Length, evidence.ToArray()) : ValidationResults.Invalid("JPEG_MALFORMED_MARKER", reader.BytesInspected, evidence.ToArray());
        }
        catch (EndOfStreamException)
        {
            return sawFrame ? ValidationResults.Truncated(reader.BytesInspected, reader.Length, evidence.ToArray()) : ValidationResults.Invalid("JPEG_MALFORMED_MARKER", reader.BytesInspected, evidence.ToArray());
        }
        catch (CandidateBudgetExceededException)
        {
            return ValidationResults.Budget(reader.BytesInspected, evidence.ToArray());
        }
    }

    private static async ValueTask<int> ReadMarkerAsync(CandidateCursor cursor, CancellationToken cancellationToken)
    {
        if (await cursor.ReadByteAsync(cancellationToken) != 0xFF)
        {
            return -1;
        }

        byte marker;
        do
        {
            marker = await cursor.ReadByteAsync(cancellationToken);
        }
        while (marker == 0xFF);
        return marker == 0 ? -1 : marker;
    }

    private static async ValueTask<int?> ScanEntropyAsync(
        CandidateCursor cursor,
        CandidateValidationContext context,
        CancellationToken cancellationToken)
    {
        var start = cursor.Position;
        while (cursor.Position < cursor.Length && cursor.Position - start <= context.MaximumTerminatorSearchDistance)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (await cursor.ReadByteAsync(cancellationToken) != 0xFF)
            {
                continue;
            }

            byte next;
            do
            {
                if (cursor.Position >= cursor.Length) return null;
                next = await cursor.ReadByteAsync(cancellationToken);
            }
            while (next == 0xFF);
            if (next == 0x00 || next is >= 0xD0 and <= 0xD7)
            {
                continue;
            }

            return next;
        }

        if (cursor.Position - start > context.MaximumTerminatorSearchDistance)
        {
            throw new CandidateBudgetExceededException("TerminatorSearch");
        }

        return null;
    }

    private static bool IsUnsupportedFrame(int marker) =>
        marker is >= 0xC1 and <= 0xCF && marker is not 0xC2 and not 0xC4 and not 0xC8 and not 0xCC;
}
