using DataRecoveryStudio.Core;

namespace DataRecoveryStudio.Infrastructure;

public sealed class ZipCandidateValidator : ICarvedCandidateValidator
{
    private const uint LocalHeader = 0x04034B50;
    private const uint CentralHeader = 0x02014B50;
    private const uint EndOfCentralDirectory = 0x06054B50;

    public async ValueTask<CandidateValidationResult> ValidateAsync(
        IReadOnlyRandomAccessSource source,
        CandidateValidationContext context,
        CancellationToken cancellationToken)
    {
        var reader = new BoundedCandidateReader(source, context);
        var evidence = new List<string>();
        try
        {
            var local = new byte[30];
            await reader.ReadExactlyAsync(0, local, cancellationToken).ConfigureAwait(false);
            if (BinaryRead.UInt32Little(local) != LocalHeader)
            {
                return ValidationResults.Invalid("ZIP_INCONSISTENT_DIRECTORY", reader.BytesInspected, "ZIP local header missing");
            }

            var initialNameLength = BinaryRead.UInt16Little(local.AsSpan(26));
            var initialExtraLength = BinaryRead.UInt16Little(local.AsSpan(28));
            if (initialNameLength > context.MaximumMetadataStringLength || initialExtraLength > context.MaximumMetadataStringLength ||
                30L + initialNameLength + initialExtraLength > reader.Length)
            {
                return ValidationResults.Invalid("ZIP_INCONSISTENT_DIRECTORY", reader.BytesInspected, "Invalid ZIP local metadata bounds");
            }

            evidence.Add("ZIP local file header");
            var cursor = new CandidateCursor(reader);
            cursor.Seek(4);
            uint window = LocalHeader;
            long? acceptedLength = null;
            var encrypted = false;
            var dataDescriptor = false;
            var searchLimit = Math.Min(reader.Length, context.MaximumTerminatorSearchDistance);
            while (cursor.Position < searchLimit)
            {
                cancellationToken.ThrowIfCancellationRequested();
                window = (window >> 8) | ((uint)await cursor.ReadByteAsync(cancellationToken) << 24);
                if (window != EndOfCentralDirectory) continue;

                var eocdOffset = cursor.Position - 4;
                var tail = new byte[18];
                if (cursor.Position > cursor.Length - tail.Length) break;
                await cursor.ReadExactlyAsync(tail, cancellationToken);
                var diskNumber = BinaryRead.UInt16Little(tail);
                var centralDisk = BinaryRead.UInt16Little(tail.AsSpan(2));
                var diskEntries = BinaryRead.UInt16Little(tail.AsSpan(4));
                var totalEntries = BinaryRead.UInt16Little(tail.AsSpan(6));
                var centralSize = BinaryRead.UInt32Little(tail.AsSpan(8));
                var centralOffset = BinaryRead.UInt32Little(tail.AsSpan(12));
                var commentLength = BinaryRead.UInt16Little(tail.AsSpan(16));
                if (diskEntries == ushort.MaxValue || totalEntries == ushort.MaxValue || centralSize == uint.MaxValue || centralOffset == uint.MaxValue)
                {
                    return new(
                        DeepScanValidationState.UnsupportedVariant,
                        DeepScanCompleteness.LengthUnknown,
                        DeepScanConfidence.Low,
                        null,
                        DeepScanConfidence.Low,
                        evidence,
                        ["ZIP64_UNSUPPORTED"],
                        reader.BytesInspected);
                }

                if (diskNumber != 0 || centralDisk != 0 || diskEntries != totalEntries || totalEntries == 0 ||
                    totalEntries > context.MaximumStructuralElements || commentLength > context.MaximumMetadataStringLength)
                {
                    continue;
                }

                long centralEnd;
                long archiveEnd;
                try
                {
                    centralEnd = checked((long)centralOffset + centralSize);
                    archiveEnd = checked(eocdOffset + 22 + commentLength);
                }
                catch (OverflowException)
                {
                    continue;
                }

                if (centralEnd != eocdOffset || archiveEnd > reader.Length) continue;
                var directory = await ValidateDirectoryAsync(
                    reader,
                    centralOffset,
                    centralSize,
                    totalEntries,
                    context,
                    cancellationToken).ConfigureAwait(false);
                if (!directory.Valid) continue;
                encrypted |= directory.Encrypted;
                dataDescriptor |= directory.DataDescriptor;
                acceptedLength = archiveEnd;
            }

            if (acceptedLength is not null)
            {
                evidence.Add("Consistent ZIP central directory and EOCD");
                if (encrypted) evidence.Add("Encrypted entry metadata present");
                if (dataDescriptor) evidence.Add("Data-descriptor entry metadata present");
                return ValidationResults.Complete(acceptedLength.Value, reader.BytesInspected, evidence.ToArray());
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
                ["ZIP_INCONSISTENT_DIRECTORY"],
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
                ["ZIP_INCONSISTENT_DIRECTORY"],
                reader.BytesInspected);
        }
        catch (CandidateBudgetExceededException)
        {
            return ValidationResults.Budget(reader.BytesInspected, evidence.ToArray());
        }
        catch (Zip64DetectedException)
        {
            return new(
                DeepScanValidationState.UnsupportedVariant,
                DeepScanCompleteness.LengthUnknown,
                DeepScanConfidence.Low,
                null,
                DeepScanConfidence.Low,
                evidence,
                ["ZIP64_UNSUPPORTED"],
                reader.BytesInspected);
        }
    }

    private static async ValueTask<(bool Valid, bool Encrypted, bool DataDescriptor)> ValidateDirectoryAsync(
        BoundedCandidateReader reader,
        long centralOffset,
        long centralSize,
        int totalEntries,
        CandidateValidationContext context,
        CancellationToken cancellationToken)
    {
        var offset = centralOffset;
        var end = checked(centralOffset + centralSize);
        var encrypted = false;
        var descriptor = false;
        for (var index = 0; index < totalEntries; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (offset > end - 46) return (false, false, false);
            var entry = new byte[46];
            await reader.ReadExactlyAsync(offset, entry, cancellationToken).ConfigureAwait(false);
            if (BinaryRead.UInt32Little(entry) != CentralHeader) return (false, false, false);
            var flags = BinaryRead.UInt16Little(entry.AsSpan(8));
            var compressedSize = BinaryRead.UInt32Little(entry.AsSpan(20));
            var uncompressedSize = BinaryRead.UInt32Little(entry.AsSpan(24));
            var nameLength = BinaryRead.UInt16Little(entry.AsSpan(28));
            var extraLength = BinaryRead.UInt16Little(entry.AsSpan(30));
            var commentLength = BinaryRead.UInt16Little(entry.AsSpan(32));
            var diskStart = BinaryRead.UInt16Little(entry.AsSpan(34));
            var localOffset = BinaryRead.UInt32Little(entry.AsSpan(42));
            if (compressedSize == uint.MaxValue || uncompressedSize == uint.MaxValue || localOffset == uint.MaxValue || diskStart == ushort.MaxValue)
            {
                throw new Zip64DetectedException();
            }

            if (diskStart != 0 || nameLength == 0 || nameLength > context.MaximumMetadataStringLength ||
                extraLength > context.MaximumMetadataStringLength || commentLength > context.MaximumMetadataStringLength)
            {
                return (false, false, false);
            }

            var next = checked(offset + 46L + nameLength + extraLength + commentLength);
            if (next > end || localOffset > centralOffset - 30) return (false, false, false);
            var local = new byte[30];
            await reader.ReadExactlyAsync(localOffset, local, cancellationToken).ConfigureAwait(false);
            if (BinaryRead.UInt32Little(local) != LocalHeader || BinaryRead.UInt16Little(local.AsSpan(6)) != flags)
            {
                return (false, false, false);
            }

            encrypted |= (flags & 0x0001) != 0;
            descriptor |= (flags & 0x0008) != 0;
            offset = next;
        }

        return (offset == end, encrypted, descriptor);
    }

    private sealed class Zip64DetectedException : Exception
    {
    }
}
