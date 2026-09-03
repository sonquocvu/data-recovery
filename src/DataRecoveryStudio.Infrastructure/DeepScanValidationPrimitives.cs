using System.Buffers.Binary;
using DataRecoveryStudio.Core;

namespace DataRecoveryStudio.Infrastructure;

internal sealed class CandidateBudgetExceededException(string budgetName) : Exception(budgetName)
{
    public string BudgetName { get; } = budgetName;
}

internal sealed class BoundedCandidateReader(
    IReadOnlyRandomAccessSource source,
    CandidateValidationContext context)
{
    private const int MaximumSingleRead = 64 * 1024;

    public long BytesInspected { get; private set; }
    public int ReadCount { get; private set; }
    public long Length => Math.Min(context.MaximumAvailableBytes, context.MaximumBytesInspected);

    public async ValueTask ReadExactlyAsync(long relativeOffset, Memory<byte> destination, CancellationToken cancellationToken)
    {
        Check(cancellationToken);
        if (destination.Length > MaximumSingleRead)
        {
            throw new CandidateBudgetExceededException("MaximumSingleRead");
        }

        if (relativeOffset < 0 || relativeOffset > Length - destination.Length)
        {
            throw new EndOfStreamException("The candidate read exceeds its bounded source extent.");
        }

        if (ReadCount >= context.MaximumRandomReads || BytesInspected > context.MaximumBytesInspected - destination.Length)
        {
            throw new CandidateBudgetExceededException("ValidatorBudget");
        }

        ReadCount++;
        await source.ReadExactlyAsync(checked(context.CandidateOffset + relativeOffset), destination, cancellationToken).ConfigureAwait(false);
        BytesInspected = checked(BytesInspected + destination.Length);
        Check(cancellationToken);
    }

    public void Check(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (DateTimeOffset.UtcNow > context.DeadlineUtc)
        {
            throw new CandidateBudgetExceededException("ValidationDuration");
        }
    }
}

internal sealed class CandidateCursor(BoundedCandidateReader reader)
{
    private readonly byte[] _buffer = new byte[8192];
    private long _bufferOffset = -1;
    private int _bufferCount;

    public long Position { get; private set; }
    public long Length => reader.Length;

    public async ValueTask<byte> ReadByteAsync(CancellationToken cancellationToken)
    {
        if (Position < 0 || Position >= Length)
        {
            throw new EndOfStreamException("The candidate ended before validation completed.");
        }

        if (_bufferOffset < 0 || Position < _bufferOffset || Position >= _bufferOffset + _bufferCount)
        {
            _bufferOffset = Position;
            _bufferCount = checked((int)Math.Min(_buffer.Length, Length - Position));
            await reader.ReadExactlyAsync(_bufferOffset, _buffer.AsMemory(0, _bufferCount), cancellationToken).ConfigureAwait(false);
        }

        return _buffer[checked((int)(Position++ - _bufferOffset))];
    }

    public async ValueTask ReadExactlyAsync(Memory<byte> destination, CancellationToken cancellationToken)
    {
        for (var index = 0; index < destination.Length; index++)
        {
            var value = await ReadByteAsync(cancellationToken).ConfigureAwait(false);
            destination.Span[index] = value;
        }
    }

    public ValueTask SkipAsync(long count, CancellationToken cancellationToken)
    {
        if (count < 0 || Position > Length - count)
        {
            throw new EndOfStreamException("The candidate ended inside a declared structure.");
        }

        Position = checked(Position + count);
        reader.Check(cancellationToken);
        return ValueTask.CompletedTask;
    }

    public void Seek(long position)
    {
        if (position < 0 || position > Length)
        {
            throw new EndOfStreamException("The candidate seek exceeds its bounded extent.");
        }

        Position = position;
    }
}

internal static class ValidationResults
{
    public static CandidateValidationResult Invalid(string code, long bytes, params string[] evidence) =>
        new(DeepScanValidationState.StructurallyInvalid, DeepScanCompleteness.Corrupt, DeepScanConfidence.Low,
            null, DeepScanConfidence.Low, evidence, [code], bytes);

    public static CandidateValidationResult Truncated(long bytes, params string[] evidence) =>
        new(DeepScanValidationState.StructurallyPlausible, DeepScanCompleteness.Truncated, DeepScanConfidence.Medium,
            null, DeepScanConfidence.Low, evidence, [], bytes);

    public static CandidateValidationResult Truncated(long bytes, long logicalLength, params string[] evidence) =>
        new(DeepScanValidationState.StructurallyPlausible, DeepScanCompleteness.Truncated, DeepScanConfidence.Medium,
            logicalLength, DeepScanConfidence.Medium, evidence, [], bytes);

    public static CandidateValidationResult Budget(long bytes, params string[] evidence) =>
        new(DeepScanValidationState.StructurallyPlausible, DeepScanCompleteness.BudgetLimited, DeepScanConfidence.Low,
            null, DeepScanConfidence.Low, evidence, ["VALIDATOR_BUDGET_REACHED"], bytes);

    public static CandidateValidationResult Complete(long length, long bytes, params string[] evidence) =>
        new(DeepScanValidationState.StructurallyValidated, DeepScanCompleteness.Complete, DeepScanConfidence.High,
            length, DeepScanConfidence.High, evidence, [], bytes);
}

internal static class BinaryRead
{
    public static ushort UInt16Big(ReadOnlySpan<byte> value) => BinaryPrimitives.ReadUInt16BigEndian(value);
    public static uint UInt32Big(ReadOnlySpan<byte> value) => BinaryPrimitives.ReadUInt32BigEndian(value);
    public static ushort UInt16Little(ReadOnlySpan<byte> value) => BinaryPrimitives.ReadUInt16LittleEndian(value);
    public static uint UInt32Little(ReadOnlySpan<byte> value) => BinaryPrimitives.ReadUInt32LittleEndian(value);
}
