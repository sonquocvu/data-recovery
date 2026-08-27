using DataRecoveryStudio.Application;
using DataRecoveryStudio.Core;
using DataRecoveryStudio.Infrastructure;

namespace DataRecoveryStudio.Tests;

public sealed class PresentationAndFilteringTests
{
    [Theory]
    [InlineData(RecoverabilityStatus.Excellent, "Status.Excellent", "Positive")]
    [InlineData(RecoverabilityStatus.Good, "Status.Good", "Informative")]
    [InlineData(RecoverabilityStatus.Poor, "Status.Poor", "Critical")]
    [InlineData(RecoverabilityStatus.Unknown, "Status.Unknown", "Neutral")]
    public void Recoverability_HasHonestPresentation(RecoverabilityStatus status, string key, string tone)
    {
        var presentation = RecoverabilityVisual.From(status);

        Assert.Equal(key, presentation.LabelKey);
        Assert.Equal(tone, presentation.Tone);
    }

    [Fact]
    public void SearchAndCategoryFilter_CombineAndRemainCaseInsensitive()
    {
        var source = new PhysicalDeviceId("mock:test-source");
        var viewModel = new ResultsViewModel(new MockRecoveryCatalogService());
        viewModel.LoadFiles(
        [
            Create("Holiday.JPG", @"Pictures\Trips", FileCategory.Image, source),
            Create("holiday-plan.docx", @"Documents", FileCategory.Document, source),
            Create("portrait.png", @"Pictures\Studio", FileCategory.Image, source),
        ]);

        viewModel.SelectedCategory = FileCategory.Image;
        viewModel.SearchText = "holiday";

        var visible = Assert.Single(viewModel.VisibleResults);
        Assert.Equal("Holiday.JPG", visible.Name);
    }

    [Fact]
    public void MultiSelection_TracksCountAndRequiredBytes()
    {
        var source = new PhysicalDeviceId("mock:test-source");
        var viewModel = new ResultsViewModel(new MockRecoveryCatalogService());
        viewModel.LoadFiles(
        [
            Create("one.jpg", "Pictures", FileCategory.Image, source, 100),
            Create("two.pdf", "Documents", FileCategory.Document, source, 250),
        ]);

        viewModel.VisibleResults[0].IsSelected = true;
        viewModel.VisibleResults[1].IsSelected = true;

        Assert.Equal(2, viewModel.SelectedCount);
        Assert.Equal(350, viewModel.SelectedBytes);
    }

    private static RecoverableFile Create(string name, string path, FileCategory category, PhysicalDeviceId source, long size = 10) =>
        new(Guid.NewGuid(), name, path, size, DateTimeOffset.UtcNow, category, RecoverabilityStatus.Good, source, "Mock preview");
}
