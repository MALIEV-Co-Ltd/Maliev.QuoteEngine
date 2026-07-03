using Maliev.QuoteEngine.Client.Components;
using Maliev.QuoteEngine.Client.Components.QuoteEngine;
using Maliev.QuoteEngine.Client.Models;

namespace Maliev.QuoteEngine.Tests;

public sealed class QuotePartViewModelShippingTests
{
    [Fact]
    public void OrderDetail_ShippingRequestSendsPackageParts()
    {
        var source = ReadRepoFile("Maliev.QuoteEngine.Client", "Components", "QuoteAgent", "QeOrderDetail.razor");

        Assert.Contains("Parts = BuildShippingPackageParts()", source, StringComparison.Ordinal);
        Assert.Contains("private List<ShippingPackagePartDto> BuildShippingPackageParts()", source, StringComparison.Ordinal);
    }

    [Fact]
    public void ToDraft_CarriesBoundingBoxMmForOrderShipping()
    {
        var part = new QuotePartViewModel
        {
            PartId = Guid.NewGuid(),
            FileId = Guid.NewGuid(),
            UploadId = "upload-1",
            FileName = "bracket.stl",
            ProcessId = "fdm",
            MaterialId = "pla-black",
            Quantity = 12,
            VolumeCc = 18.25m,
            SurfaceAreaCm2 = 95.4m,
            BoundingBox = new QuoteBoundingBoxDto(80m, 120m, 40m)
        };

        var draft = part.ToDraft();

        Assert.NotNull(draft.BoundingBoxMm);
        Assert.Equal(80m, draft.BoundingBoxMm.X);
        Assert.Equal(120m, draft.BoundingBoxMm.Y);
        Assert.Equal(40m, draft.BoundingBoxMm.Z);
    }

    [Fact]
    public void TryApply_CopiesLocalDfmBoundingBoxMmToPart()
    {
        var part = new QuotePartViewModel
        {
            ProcessId = "fdm",
            MaterialId = "pla-black"
        };

        var applied = QeLocalDfmMapper.TryApply(part, new LocalGeometryRuntimeResult
        {
            ProcessCode = "fdm",
            Authority = "local_primary",
            ExecutionMode = "primary_interactive",
            IsAuthoritative = false,
            Metrics = new LocalGeometryRuntimeMetrics
            {
                VolumeMm3 = 12_500,
                SurfaceAreaMm2 = 8_000,
                BoundingBoxMm = new QuoteBoundingBoxDto(80m, 120m, 40m),
                IsManifold = true
            }
        });

        Assert.True(applied);
        Assert.NotNull(part.BoundingBox);
        Assert.Equal(80m, part.BoundingBox.X);
        Assert.Equal(120m, part.BoundingBox.Y);
        Assert.Equal(40m, part.BoundingBox.Z);
    }

    private static string ReadRepoFile(params string[] pathParts)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "Maliev.QuoteEngine.Client")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return File.ReadAllText(Path.Combine([directory.FullName, .. pathParts]));
    }
}
