using System.Globalization;
using DataRecoveryStudio.Core;

namespace DataRecoveryStudio.Application;

public static class ByteFormatter
{
    public static string Format(long bytes)
    {
        string[] suffixes = ["B", "KB", "MB", "GB", "TB"];
        var value = Math.Max(0, bytes);
        var display = (double)value;
        var suffix = 0;
        while (display >= 1024 && suffix < suffixes.Length - 1)
        {
            display /= 1024;
            suffix++;
        }

        return string.Create(CultureInfo.CurrentCulture, $"{display:0.#} {suffixes[suffix]}");
    }
}

public sealed record RecoverabilityVisual(string LabelKey, string Tone)
{
    public static RecoverabilityVisual From(RecoverabilityStatus status) => status switch
    {
        RecoverabilityStatus.Excellent => new("Status.Excellent", "Positive"),
        RecoverabilityStatus.Good => new("Status.Good", "Informative"),
        RecoverabilityStatus.Poor => new("Status.Poor", "Critical"),
        _ => new("Status.Unknown", "Neutral"),
    };
}
