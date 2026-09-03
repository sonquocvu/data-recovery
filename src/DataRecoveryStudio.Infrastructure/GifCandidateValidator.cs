using DataRecoveryStudio.Core;

namespace DataRecoveryStudio.Infrastructure;

public sealed class GifCandidateValidator : ICarvedCandidateValidator
{
    public async ValueTask<CandidateValidationResult> ValidateAsync(
        IReadOnlyRandomAccessSource source,
        CandidateValidationContext context,
        CancellationToken cancellationToken)
    {
        var reader = new BoundedCandidateReader(source, context);
        var cursor = new CandidateCursor(reader);
        var evidence = new List<string>();
        var elements = 0;
        var sawImage = false;
        try
        {
            var header = new byte[6];
            await cursor.ReadExactlyAsync(header, cancellationToken);
            var version = System.Text.Encoding.ASCII.GetString(header);
            if (version is not "GIF87a" and not "GIF89a")
            {
                return ValidationResults.Invalid("GIF_MALFORMED_BLOCK", reader.BytesInspected, "GIF header missing");
            }

            evidence.Add(version);
            var screen = new byte[7];
            await cursor.ReadExactlyAsync(screen, cancellationToken);
            if (BinaryRead.UInt16Little(screen) == 0 || BinaryRead.UInt16Little(screen.AsSpan(2)) == 0)
            {
                return ValidationResults.Invalid("GIF_MALFORMED_BLOCK", reader.BytesInspected, evidence.ToArray());
            }

            if ((screen[4] & 0x80) != 0)
            {
                await cursor.SkipAsync(ColorTableLength(screen[4]), cancellationToken);
            }

            while (cursor.Position < cursor.Length)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (++elements > context.MaximumStructuralElements) throw new CandidateBudgetExceededException("StructuralElements");
                var introducer = await cursor.ReadByteAsync(cancellationToken);
                if (introducer == 0x3B)
                {
                    if (!sawImage) return ValidationResults.Invalid("GIF_MALFORMED_BLOCK", reader.BytesInspected, evidence.ToArray());
                    evidence.Add("GIF image data and trailer");
                    return ValidationResults.Complete(cursor.Position, reader.BytesInspected, evidence.ToArray());
                }

                if (introducer == 0x2C)
                {
                    var descriptor = new byte[9];
                    await cursor.ReadExactlyAsync(descriptor, cancellationToken);
                    if (BinaryRead.UInt16Little(descriptor.AsSpan(4)) == 0 || BinaryRead.UInt16Little(descriptor.AsSpan(6)) == 0)
                    {
                        return ValidationResults.Invalid("GIF_MALFORMED_BLOCK", reader.BytesInspected, evidence.ToArray());
                    }

                    if ((descriptor[8] & 0x80) != 0)
                    {
                        await cursor.SkipAsync(ColorTableLength(descriptor[8]), cancellationToken);
                    }

                    var lzwMinimum = await cursor.ReadByteAsync(cancellationToken);
                    if (lzwMinimum is < 2 or > 8)
                    {
                        return ValidationResults.Invalid("GIF_MALFORMED_BLOCK", reader.BytesInspected, evidence.ToArray());
                    }

                    await SkipSubBlocksAsync(cursor, context, () => ++elements, cancellationToken);
                    sawImage = true;
                    continue;
                }

                if (introducer == 0x21)
                {
                    var label = await cursor.ReadByteAsync(cancellationToken);
                    if (label == 0)
                    {
                        return ValidationResults.Invalid("GIF_MALFORMED_BLOCK", reader.BytesInspected, evidence.ToArray());
                    }

                    await SkipSubBlocksAsync(cursor, context, () => ++elements, cancellationToken);
                    continue;
                }

                return ValidationResults.Invalid("GIF_MALFORMED_BLOCK", reader.BytesInspected, evidence.ToArray());
            }

            return ValidationResults.Truncated(reader.BytesInspected, reader.Length, evidence.ToArray());
        }
        catch (EndOfStreamException)
        {
            return ValidationResults.Truncated(reader.BytesInspected, reader.Length, evidence.ToArray());
        }
        catch (CandidateBudgetExceededException)
        {
            return ValidationResults.Budget(reader.BytesInspected, evidence.ToArray());
        }
    }

    private static int ColorTableLength(byte packed) => checked(3 * (1 << ((packed & 0x07) + 1)));

    private static async ValueTask SkipSubBlocksAsync(
        CandidateCursor cursor,
        CandidateValidationContext context,
        Func<int> increment,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (increment() > context.MaximumStructuralElements) throw new CandidateBudgetExceededException("StructuralElements");
            var length = await cursor.ReadByteAsync(cancellationToken);
            if (length == 0) return;
            await cursor.SkipAsync(length, cancellationToken);
        }
    }
}
