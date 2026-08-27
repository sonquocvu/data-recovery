using DataRecoveryStudio.Core;

namespace DataRecoveryStudio.Infrastructure;

public sealed class MockRecoveryCatalogService : IRecoveryCatalogService
{
    public async Task<IReadOnlyList<RecoverableFile>> GetResultsAsync(ScanSession session, CancellationToken cancellationToken)
    {
        await Task.Delay(350, cancellationToken).ConfigureAwait(false);
        var source = session.SourceDeviceId;
        var now = DateTimeOffset.Now;

        return
        [
            File("mountain-sunrise.jpg", @"Users\Avery\Pictures\Trips\", 8_742_912, now.AddDays(-12), FileCategory.Image, RecoverabilityStatus.Excellent, source, "Photo preview placeholder · 6000 × 4000 JPEG"),
            File("project-brief.docx", @"Users\Avery\Documents\Work\", 1_384_448, now.AddDays(-4), FileCategory.Document, RecoverabilityStatus.Good, source, "Document preview placeholder · Microsoft Word document"),
            File("family-video.mp4", @"Users\Avery\Videos\", 824_633_720, now.AddMonths(-2), FileCategory.Video, RecoverabilityStatus.Poor, source, "Video preview placeholder · MPEG-4 video · 14:32", PreviewState.Damaged),
            File("invoice-2025.pdf", @"Users\Avery\Documents\Invoices\", 482_116, now.AddDays(-31), FileCategory.Document, RecoverabilityStatus.Excellent, source, "PDF preview placeholder · 3 pages"),
            File("interview.wav", @"Users\Avery\Audio\", 146_284_544, now.AddDays(-19), FileCategory.Audio, RecoverabilityStatus.Good, source, "Audio preview placeholder · WAV audio · 08:17"),
            File("design-assets.zip", @"Users\Avery\Downloads\", 94_032_101, now.AddDays(-8), FileCategory.Archive, RecoverabilityStatus.Good, source, "Archive preview placeholder · ZIP archive"),
            File("IMG_4821.png", @"DCIM\Camera\", 4_184_232, now.AddDays(-41), FileCategory.Image, RecoverabilityStatus.Excellent, source, "Photo preview placeholder · 3024 × 4032 PNG"),
            File("quarterly-results.xlsx", @"Users\Avery\Documents\Finance\", 2_783_422, now.AddDays(-17), FileCategory.Document, RecoverabilityStatus.Poor, source, "Spreadsheet preview placeholder · Microsoft Excel workbook"),
            File("voice-note.m4a", @"Users\Avery\Audio\Notes\", 12_821_004, now.AddDays(-6), FileCategory.Audio, RecoverabilityStatus.Excellent, source, "Audio preview placeholder · M4A audio · 02:08"),
            File("RECOVERED_000124.bin", @"Deep Scan\Unknown\", 65_536, null, FileCategory.Unknown, RecoverabilityStatus.Unknown, source, "Preview unavailable · file type is unknown", PreviewState.Unsupported),
            File("presentation.pptx", @"Users\Avery\Documents\Work\", 18_495_218, now.AddDays(-23), FileCategory.Document, RecoverabilityStatus.Good, source, "Presentation preview placeholder · 24 slides"),
            File("holiday-clip.mov", @"Users\Avery\Videos\Trips\", 283_951_104, now.AddMonths(-5), FileCategory.Video, RecoverabilityStatus.Poor, source, "Video preview placeholder · QuickTime video · 03:46", PreviewState.Missing),
            File("portrait-raw.cr2", @"Users\Avery\Pictures\Studio\", 32_501_728, now.AddMonths(-1), FileCategory.Image, RecoverabilityStatus.Good, source, "Image preview placeholder · Canon RAW image"),
            File("research-notes.txt", @"Users\Avery\Documents\Notes\", 48_211, now.AddDays(-2), FileCategory.Document, RecoverabilityStatus.Excellent, source, "Text preview placeholder · UTF-8 plain text"),
            File("sample-library.7z", @"Users\Avery\Music\", 536_870_912, now.AddMonths(-4), FileCategory.Archive, RecoverabilityStatus.Unknown, source, "Archive preview placeholder · 7-Zip archive"),
            File("podcast-edit.flac", @"Users\Avery\Audio\Projects\", 221_904_006, now.AddDays(-27), FileCategory.Audio, RecoverabilityStatus.Good, source, "Audio preview placeholder · FLAC audio · 19:48"),
        ];
    }

    private static RecoverableFile File(
        string name,
        string path,
        long size,
        DateTimeOffset? modified,
        FileCategory category,
        RecoverabilityStatus status,
        PhysicalDeviceId source,
        string preview,
        PreviewState previewState = PreviewState.Supported) => new(Guid.NewGuid(), name, path, size, modified, category, status, source, preview, previewState);
}
