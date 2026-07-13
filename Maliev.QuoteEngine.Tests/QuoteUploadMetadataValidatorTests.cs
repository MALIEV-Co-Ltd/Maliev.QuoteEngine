using Maliev.QuoteEngine.Bff.Clients;
using Maliev.QuoteEngine.Bff.Services;

namespace Maliev.QuoteEngine.Tests;

public sealed class QuoteUploadMetadataValidatorTests
{
    private static readonly Guid CanonicalFileId = Guid.Parse("11111111-2222-3333-4444-555555555555");

    [Fact]
    public void TryValidateCompletedUpload_ExactCanonicalMetadata_ReturnsFileId()
    {
        var upload = Upload();
        var metadata = Metadata();

        var valid = QuoteUploadMetadataValidator.TryValidateCompletedUpload(
            upload,
            metadata,
            out var fileId);

        Assert.True(valid);
        Assert.Equal(CanonicalFileId, fileId);
    }

    [Theory]
    [InlineData("foreign-upload", "customers/customer/quotes/session/upload/part.step", 1234, "model/step")]
    [InlineData("downstream-upload", "customers/customer/quotes/session/other/part.step", 1234, "model/step")]
    [InlineData("downstream-upload", "customers/customer/quotes/session/upload/part.step", 1235, "model/step")]
    [InlineData("downstream-upload", "customers/customer/quotes/session/upload/part.step", 1234, "text/plain")]
    public void TryValidateCompletedUpload_MismatchedCanonicalFact_FailsClosed(
        string downstreamUploadId,
        string storagePath,
        long fileSize,
        string contentType)
    {
        var metadata = Metadata() with
        {
            UploadId = downstreamUploadId,
            StoragePath = storagePath,
            FileSize = fileSize,
            ContentType = contentType
        };

        Assert.False(QuoteUploadMetadataValidator.TryValidateCompletedUpload(
            Upload(),
            metadata,
            out _));
    }

    [Fact]
    public void TryValidateCompletedUpload_InvalidCanonicalFileId_FailsClosed()
    {
        Assert.False(QuoteUploadMetadataValidator.TryValidateCompletedUpload(
            Upload(),
            Metadata() with { FileId = "not-a-guid" },
            out _));
    }

    [Fact]
    public void TryValidateCompletedUpload_ForeignServiceOwner_FailsClosed()
    {
        Assert.False(QuoteUploadMetadataValidator.TryValidateCompletedUpload(
            Upload(),
            Metadata() with { ServiceId = "ForeignService" },
            out _));
    }

    private static UploadState Upload() => new(
        "browser-upload",
        Guid.NewGuid(),
        "part.step",
        "model/step; charset=binary",
        1234,
        "customers/customer/quotes/session/upload/part.step",
        Guid.NewGuid(),
        IsTemporary: false,
        VisitorId: Guid.NewGuid(),
        QuoteSessionId: Guid.NewGuid().ToString("D"))
    {
        DownstreamUploadId = "downstream-upload",
        ReceivedBytes = 1234,
        Status = "Uploaded"
    };

    private static QuoteUploadMetadata Metadata() => new(
        CanonicalFileId.ToString("D"),
        "downstream-upload",
        "QuoteEngine",
        "customers/customer/quotes/session/upload/part.step",
        1234,
        "model/step");
}
