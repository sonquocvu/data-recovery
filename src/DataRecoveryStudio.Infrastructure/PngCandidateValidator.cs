using DataRecoveryStudio.Core;

namespace DataRecoveryStudio.Infrastructure;

public sealed class PngCandidateValidator : ICarvedCandidateValidator
{
    private static readonly byte[] Signature = [137, 80, 78, 71, 13, 10, 26, 10];

    public async ValueTask<CandidateValidationResult> ValidateAsync(
        IReadOnlyRandomAccessSource source,
        CandidateValidationContext context,
        CancellationToken cancellationToken)
    {
        var reader = new BoundedCandidateReader(source, context);
        var cursor = new CandidateCursor(reader);
        var evidence = new List<string>();
        var chunks = 0;
        var sawHeader = false;
        var sawData = false;
        var sawPalette = false;
        byte colorType = 0;
        try
        {
            var signature = new byte[8];
            await cursor.ReadExactlyAsync(signature, cancellationToken);
            if (!signature.AsSpan().SequenceEqual(Signature))
            {
                return ValidationResults.Invalid("PNG_INVALID_CHUNK_OR_CRC", reader.BytesInspected, "PNG signature missing");
            }

            evidence.Add("PNG 8-byte signature");
            var dataBuffer = new byte[8192];
            while (cursor.Position < cursor.Length)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (++chunks > context.MaximumStructuralElements)
                {
                    throw new CandidateBudgetExceededException("StructuralElements");
                }

                var header = new byte[8];
                await cursor.ReadExactlyAsync(header, cancellationToken);
                var length = BinaryRead.UInt32Big(header);
                var type = header[4..8];
                if (!IsValidChunkType(type) || length > int.MaxValue)
                {
                    return ValidationResults.Invalid("PNG_INVALID_CHUNK_OR_CRC", reader.BytesInspected, evidence.ToArray());
                }

                if (length > cursor.Length - cursor.Position - 4)
                {
                    if (reader.Length < context.MaximumAvailableBytes) throw new CandidateBudgetExceededException("ValidatorBudget");
                    return ValidationResults.Truncated(reader.BytesInspected, reader.Length, evidence.ToArray());
                }

                var typeText = System.Text.Encoding.ASCII.GetString(type);
                if (!sawHeader && (typeText != "IHDR" || length != 13))
                {
                    return ValidationResults.Invalid("PNG_INVALID_CHUNK_OR_CRC", reader.BytesInspected, evidence.ToArray());
                }

                var crc = Crc32.Start;
                crc = Crc32.Update(crc, type);
                byte[]? ihdr = null;
                if (typeText == "IHDR") ihdr = new byte[13];
                var remaining = (long)length;
                var copied = 0;
                while (remaining > 0)
                {
                    var count = checked((int)Math.Min(dataBuffer.Length, remaining));
                    await cursor.ReadExactlyAsync(dataBuffer.AsMemory(0, count), cancellationToken);
                    crc = Crc32.Update(crc, dataBuffer.AsSpan(0, count));
                    if (ihdr is not null)
                    {
                        dataBuffer.AsSpan(0, count).CopyTo(ihdr.AsSpan(copied));
                        copied += count;
                    }

                    remaining -= count;
                }

                var expectedCrcBytes = new byte[4];
                await cursor.ReadExactlyAsync(expectedCrcBytes, cancellationToken);
                if (Crc32.Finish(crc) != BinaryRead.UInt32Big(expectedCrcBytes))
                {
                    return ValidationResults.Invalid("PNG_INVALID_CHUNK_OR_CRC", reader.BytesInspected, [.. evidence, $"CRC failure in {typeText}"]);
                }

                if (typeText == "IHDR")
                {
                    if (sawHeader || !ValidateHeader(ihdr!, out colorType))
                    {
                        return ValidationResults.Invalid("PNG_INVALID_CHUNK_OR_CRC", reader.BytesInspected, evidence.ToArray());
                    }

                    sawHeader = true;
                    evidence.Add("Valid PNG IHDR");
                }
                else if (typeText == "PLTE")
                {
                    if (!sawHeader || sawData || length == 0 || length % 3 != 0 || length > 768)
                    {
                        return ValidationResults.Invalid("PNG_INVALID_CHUNK_OR_CRC", reader.BytesInspected, evidence.ToArray());
                    }

                    sawPalette = true;
                }
                else if (typeText == "IDAT")
                {
                    if (!sawHeader || (colorType == 3 && !sawPalette))
                    {
                        return ValidationResults.Invalid("PNG_INVALID_CHUNK_OR_CRC", reader.BytesInspected, evidence.ToArray());
                    }

                    sawData = true;
                }
                else if (typeText == "IEND")
                {
                    if (length != 0 || !sawHeader || !sawData)
                    {
                        return ValidationResults.Invalid("PNG_INVALID_CHUNK_OR_CRC", reader.BytesInspected, evidence.ToArray());
                    }

                    evidence.Add("PNG IDAT and CRC-validated IEND");
                    return ValidationResults.Complete(cursor.Position, reader.BytesInspected, evidence.ToArray());
                }
                else if (char.IsUpper(typeText[0]) && !IsKnownCritical(typeText))
                {
                    return new(
                        DeepScanValidationState.UnsupportedVariant,
                        DeepScanCompleteness.LengthUnknown,
                        DeepScanConfidence.Low,
                        null,
                        DeepScanConfidence.Low,
                        evidence,
                        ["PNG_UNSUPPORTED_CRITICAL_CHUNK"],
                        reader.BytesInspected);
                }
            }

            return sawHeader ? ValidationResults.Truncated(reader.BytesInspected, reader.Length, evidence.ToArray()) : ValidationResults.Invalid("PNG_INVALID_CHUNK_OR_CRC", reader.BytesInspected);
        }
        catch (EndOfStreamException)
        {
            return sawHeader ? ValidationResults.Truncated(reader.BytesInspected, reader.Length, evidence.ToArray()) : ValidationResults.Invalid("PNG_INVALID_CHUNK_OR_CRC", reader.BytesInspected);
        }
        catch (CandidateBudgetExceededException)
        {
            return ValidationResults.Budget(reader.BytesInspected, evidence.ToArray());
        }
    }

    private static bool ValidateHeader(byte[] header, out byte colorType)
    {
        colorType = header[9];
        var width = BinaryRead.UInt32Big(header.AsSpan(0, 4));
        var height = BinaryRead.UInt32Big(header.AsSpan(4, 4));
        var bitDepth = header[8];
        if (width == 0 || height == 0 || header[10] != 0 || header[11] != 0 || header[12] > 1)
        {
            return false;
        }

        return colorType switch
        {
            0 => bitDepth is 1 or 2 or 4 or 8 or 16,
            2 => bitDepth is 8 or 16,
            3 => bitDepth is 1 or 2 or 4 or 8,
            4 => bitDepth is 8 or 16,
            6 => bitDepth is 8 or 16,
            _ => false,
        };
    }

    private static bool IsValidChunkType(ReadOnlySpan<byte> type)
    {
        foreach (var value in type)
        {
            if (value is not (>= (byte)'A' and <= (byte)'Z') and not (>= (byte)'a' and <= (byte)'z')) return false;
        }

        return true;
    }

    private static bool IsKnownCritical(string type) => type is "IHDR" or "PLTE" or "IDAT" or "IEND";
}

internal static class Crc32
{
    public const uint Start = 0xFFFFFFFF;

    public static uint Update(uint crc, ReadOnlySpan<byte> bytes)
    {
        foreach (var value in bytes)
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++) crc = (crc >> 1) ^ (0xEDB88320U & (uint)-(int)(crc & 1));
        }

        return crc;
    }

    public static uint Finish(uint crc) => ~crc;
}
