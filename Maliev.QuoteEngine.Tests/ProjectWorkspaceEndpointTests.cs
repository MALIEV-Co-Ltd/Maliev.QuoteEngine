using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using Maliev.QuoteEngine.Bff.Clients;
using Maliev.QuoteEngine.Shared.Account;
using Maliev.QuoteEngine.Shared.Quotes;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NSubstitute;

namespace Maliev.QuoteEngine.Tests;

public sealed class ProjectWorkspaceEndpointTests(QuoteEngineWebApplicationFactory factory)
    : IClassFixture<QuoteEngineWebApplicationFactory>
{
    [Fact]
    public async Task GetProjectDetail_ReportsUnavailableCommercialSourcesInsteadOfPretendingTheyAreEmpty()
    {
        const string email = "workspace-partial@example.com";
        var customerId = new Guid(MD5.HashData(Encoding.UTF8.GetBytes(email)));
        var projectId = Guid.NewGuid();
        var projectClient = Substitute.For<IProjectServiceClient>();
        projectClient.GetProjectDetailAsync(customerId, projectId, Arg.Any<CancellationToken>())
            .Returns(new CustomerProjectDetailResponse(
                projectId,
                "PRJ-PARTIAL",
                "Draft",
                "Partial workspace",
                false,
                false,
                DateTimeOffset.UtcNow,
                []));
        var quotationClient = Substitute.For<IQuotationServiceClient>();
        quotationClient.LookupBySourceProjectAsync(customerId, projectId, Arg.Any<CancellationToken>())
            .Returns(new DownstreamLookupResult<QuotationCreatedResult?>(false, null));
        var customerClient = Substitute.For<ICustomerServiceClient>();
        customerClient.LookupCustomerDocumentsAsync(customerId, Arg.Any<CancellationToken>())
            .Returns(new DownstreamLookupResult<IReadOnlyList<CustomerDocumentDto>>(false, []));

        await using var scopedFactory = factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureTestServices(services =>
            {
                Replace(services, projectClient);
                Replace(services, quotationClient);
                Replace(services, customerClient);
            });
        });
        using var client = scopedFactory.CreateClient();
        (await client.GetAsync($"/test/sign-in?email={Uri.EscapeDataString(email)}")).EnsureSuccessStatusCode();

        var response = await client.GetFromJsonAsync<CustomerProjectDetailResponse>(
            $"/quote/v1/projects/{projectId:D}");

        Assert.NotNull(response);
        Assert.Contains(response.DataWarnings, warning => warning.Source == "Quotation");
        Assert.Contains(response.DataWarnings, warning => warning.Source == "Documents");
    }

    [Fact]
    public async Task GetProjectDetail_DoesNotExposeCommercialRecordsFromAForeignQuotation()
    {
        const string email = "workspace-owner-foreign-guard@example.com";
        var customerId = new Guid(MD5.HashData(Encoding.UTF8.GetBytes(email)));
        var projectId = Guid.NewGuid();

        var projectClient = Substitute.For<IProjectServiceClient>();
        projectClient.GetProjectDetailAsync(customerId, projectId, Arg.Any<CancellationToken>())
            .Returns(new CustomerProjectDetailResponse(
                projectId,
                "PRJ-WORKSPACE-GUARD",
                "Draft",
                "Guarded project",
                IsPinned: false,
                IsArchived: false,
                DateTimeOffset.UtcNow,
                []));

        var quotationClient = Substitute.For<IQuotationServiceClient>();
        quotationClient.GetBySourceProjectAsync(customerId, projectId, Arg.Any<CancellationToken>())
            .Returns(new QuotationCreatedResult
            {
                Id = Guid.NewGuid(),
                CustomerId = Guid.NewGuid(),
                SourceProjectId = projectId,
                QuotationNumber = "QT-FOREIGN",
                Status = "Accepted",
                Total = 999m,
                CurrencyCode = "THB",
                UpdatedAt = DateTime.UtcNow
            });
        quotationClient.LookupBySourceProjectAsync(customerId, projectId, Arg.Any<CancellationToken>())
            .Returns(new DownstreamLookupResult<QuotationCreatedResult?>(true, new QuotationCreatedResult
            {
                Id = Guid.NewGuid(),
                CustomerId = Guid.NewGuid(),
                SourceProjectId = projectId,
                QuotationNumber = "QT-FOREIGN",
                Status = "Accepted",
                Total = 999m,
                CurrencyCode = "THB",
                UpdatedAt = DateTime.UtcNow
            }));

        var orderClient = Substitute.For<IOrderServiceClient>();
        var customerClient = Substitute.For<ICustomerServiceClient>();
        customerClient.GetCustomerDocumentsAsync(customerId, Arg.Any<CancellationToken>())
            .Returns([]);
        customerClient.LookupCustomerDocumentsAsync(customerId, Arg.Any<CancellationToken>())
            .Returns(new DownstreamLookupResult<IReadOnlyList<CustomerDocumentDto>>(true, []));

        await using var scopedFactory = factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureTestServices(services =>
            {
                Replace(services, projectClient);
                Replace(services, quotationClient);
                Replace(services, orderClient);
                Replace(services, customerClient);
            });
        });
        using var client = scopedFactory.CreateClient();
        var signIn = await client.GetAsync($"/test/sign-in?email={Uri.EscapeDataString(email)}");
        signIn.EnsureSuccessStatusCode();

        var response = await client.GetFromJsonAsync<CustomerProjectDetailResponse>(
            $"/quote/v1/projects/{projectId:D}");

        Assert.NotNull(response);
        Assert.Null(response.Quote);
        Assert.Null(response.Order);
        Assert.Null(response.Invoice);
        Assert.Empty(response.Receipts);
        Assert.Empty(response.Documents);
        await orderClient.DidNotReceiveWithAnyArgs()
            .LookupByCustomerAsync(default!, default);
    }

    [Fact]
    public async Task GetProjectDetail_AggregatesOnlyCustomerOwnedLinkedCommercialRecords()
    {
        const string email = "workspace-owner@example.com";
        var customerId = new Guid(MD5.HashData(Encoding.UTF8.GetBytes(email)));
        var projectId = Guid.NewGuid();
        var quoteId = Guid.NewGuid();
        var quoteVersionId = Guid.NewGuid();
        var orderId = Guid.NewGuid();
        var invoiceId = Guid.NewGuid();
        var receiptId = Guid.NewGuid();
        const string orderNumber = "ORD-WORKSPACE-1001";

        var projectClient = Substitute.For<IProjectServiceClient>();
        projectClient.GetProjectDetailAsync(customerId, projectId, Arg.Any<CancellationToken>())
            .Returns(new CustomerProjectDetailResponse(
                projectId,
                "PRJ-WORKSPACE-1001",
                "Manufacturing",
                "Workspace bracket",
                IsPinned: true,
                IsArchived: false,
                DateTimeOffset.UtcNow,
                [new QuotePartDraftDto { FileName = "bracket.step", Quantity = 5, VolumeCc = 12m }])
            {
                CreatedAt = DateTimeOffset.UtcNow.AddDays(-4)
            });

        var quotationClient = Substitute.For<IQuotationServiceClient>();
        quotationClient.GetBySourceProjectAsync(customerId, projectId, Arg.Any<CancellationToken>())
            .Returns(new QuotationCreatedResult
            {
                Id = quoteId,
                CustomerId = customerId,
                SourceProjectId = projectId,
                QuotationNumber = "QT-WORKSPACE-1001",
                Status = "Accepted",
                Total = 535m,
                CurrencyCode = "THB",
                UpdatedAt = DateTime.UtcNow,
                QuoteVersionId = quoteVersionId,
                QuoteVersionNumber = 2,
                PdfArtifactUrl = "/quote/v1/account/quotes/QT-WORKSPACE-1001/pdf"
            });
        quotationClient.LookupBySourceProjectAsync(customerId, projectId, Arg.Any<CancellationToken>())
            .Returns(new DownstreamLookupResult<QuotationCreatedResult?>(true, new QuotationCreatedResult
            {
                Id = quoteId,
                CustomerId = customerId,
                SourceProjectId = projectId,
                QuotationNumber = "QT-WORKSPACE-1001",
                Status = "Accepted",
                Total = 535m,
                CurrencyCode = "THB",
                UpdatedAt = DateTime.UtcNow,
                QuoteVersionId = quoteVersionId,
                QuoteVersionNumber = 2,
                PdfArtifactUrl = "/quote/v1/account/quotes/QT-WORKSPACE-1001/pdf"
            }));

        var orderSummary = new CustomerOrderSummaryDto(
            orderId,
            orderNumber,
            "Manufacturing",
            DateTimeOffset.UtcNow,
            orderNumber)
        {
            QuoteId = quoteId,
            QuoteNumber = "QT-WORKSPACE-1001"
        };
        var orderDetail = new CustomerOrderDetailDto(
            orderId,
            orderNumber,
            "Manufacturing",
            "Paid",
            535m,
            "THB",
            DateTimeOffset.UtcNow.AddDays(3),
            null,
            null,
            "Workspace bracket order",
            DateTimeOffset.UtcNow.AddDays(-2),
            DateTimeOffset.UtcNow,
            [])
        {
            QuoteId = quoteId,
            QuoteNumber = "QT-WORKSPACE-1001",
            QuoteVersionId = quoteVersionId,
            QuoteVersionNumber = 2,
            OrderFiles =
            [
                new CustomerOrderFileDto(
                    17,
                    "production-drawing.pdf",
                    "Drawing",
                    "Production",
                    "orders/workspace/production-drawing.pdf",
                    "application/pdf",
                    4096,
                    DateTimeOffset.UtcNow)
            ]
        };
        var orderClient = Substitute.For<IOrderServiceClient>();
        orderClient.GetByCustomerAsync(customerId.ToString("D"), Arg.Any<CancellationToken>())
            .Returns([orderSummary]);
        orderClient.LookupByCustomerAsync(customerId.ToString("D"), Arg.Any<CancellationToken>())
            .Returns(new DownstreamLookupResult<IReadOnlyList<CustomerOrderSummaryDto>>(true, [orderSummary]));
        orderClient.GetDetailForCustomerAsync(
                orderNumber,
                customerId.ToString("D"),
                Arg.Any<CancellationToken>())
            .Returns(orderDetail);
        orderClient.LookupDetailForCustomerAsync(
                orderNumber,
                customerId.ToString("D"),
                Arg.Any<CancellationToken>())
            .Returns(new DownstreamLookupResult<CustomerOrderDetailDto?>(true, orderDetail));

        var invoiceClient = Substitute.For<IInvoiceServiceClient>();
        invoiceClient.GetForOrderAsync(customerId, orderNumber, Arg.Any<CancellationToken>())
            .Returns(new CustomerProjectInvoiceResult
            {
                InvoiceId = invoiceId,
                InvoiceNumber = "INV-WORKSPACE-1001",
                Status = "FullyPaid",
                GrandTotal = 535m,
                Currency = "THB",
                IssueDate = DateTimeOffset.UtcNow.AddDays(-1),
                PdfFileReference = "pdfs/invoice/workspace.pdf"
            });
        invoiceClient.LookupForOrderAsync(customerId, orderNumber, Arg.Any<CancellationToken>())
            .Returns(new CustomerProjectInvoiceLookupResult(true, new CustomerProjectInvoiceResult
            {
                InvoiceId = invoiceId,
                InvoiceNumber = "INV-WORKSPACE-1001",
                Status = "FullyPaid",
                GrandTotal = 535m,
                Currency = "THB",
                IssueDate = DateTimeOffset.UtcNow.AddDays(-1),
                PdfFileReference = "pdfs/invoice/workspace.pdf"
            }));

        var receiptClient = Substitute.For<IReceiptServiceClient>();
        receiptClient.GetByInvoiceAsync(invoiceId, Arg.Any<CancellationToken>())
            .Returns(
            [
                new CustomerProjectReceiptResult
                {
                    ReceiptId = receiptId,
                    ReceiptNumber = "RCPT-WORKSPACE-1001",
                    InvoiceId = invoiceId,
                    Status = "Active",
                    TotalAmount = 535m,
                    Currency = "THB",
                    IssueDate = DateTimeOffset.UtcNow,
                    PdfReferenceId = Guid.NewGuid()
                }
            ]);
        receiptClient.LookupByInvoiceAsync(invoiceId, Arg.Any<CancellationToken>())
            .Returns(new CustomerProjectReceiptLookupResult(true,
            [
                new CustomerProjectReceiptResult
                {
                    ReceiptId = receiptId,
                    ReceiptNumber = "RCPT-WORKSPACE-1001",
                    InvoiceId = invoiceId,
                    Status = "Active",
                    TotalAmount = 535m,
                    Currency = "THB",
                    IssueDate = DateTimeOffset.UtcNow,
                    PdfReferenceId = Guid.NewGuid()
                }
            ]));

        var linkedDocument = new CustomerDocumentDto(
            Guid.NewGuid(),
            "receipt-workspace.pdf",
            "Receipt",
            DateTimeOffset.UtcNow,
            "customers/workspace/receipt.pdf",
            "application/pdf",
            2048,
            orderNumber);
        var unrelatedDocument = linkedDocument with
        {
            DocumentId = Guid.NewGuid(),
            FileName = "other-order.pdf",
            OrderNumber = "ORD-OTHER"
        };
        var customerClient = Substitute.For<ICustomerServiceClient>();
        customerClient.GetCustomerDocumentsAsync(customerId, Arg.Any<CancellationToken>())
            .Returns([linkedDocument, unrelatedDocument]);
        customerClient.LookupCustomerDocumentsAsync(customerId, Arg.Any<CancellationToken>())
            .Returns(new DownstreamLookupResult<IReadOnlyList<CustomerDocumentDto>>(
                true,
                [linkedDocument, unrelatedDocument]));

        var pdfClient = Substitute.For<IPdfServiceClient>();
        pdfClient.GetLatestAsync("Invoice", invoiceId, Arg.Any<CancellationToken>())
            .Returns(new PdfGenerationResult
            {
                RequestId = Guid.NewGuid(),
                StoragePath = "pdfs/invoice/workspace.pdf",
                StorageUrl = "https://storage.example.test/invoice.pdf"
            });
        pdfClient.GetLatestAsync("Receipt", receiptId, Arg.Any<CancellationToken>())
            .Returns(new PdfGenerationResult
            {
                RequestId = Guid.NewGuid(),
                StoragePath = "pdfs/receipt/workspace.pdf",
                StorageUrl = "https://storage.example.test/receipt.pdf"
            });

        await using var scopedFactory = factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureTestServices(services =>
            {
                Replace(services, projectClient);
                Replace(services, quotationClient);
                Replace(services, orderClient);
                Replace(services, invoiceClient);
                Replace(services, receiptClient);
                Replace(services, customerClient);
                Replace(services, pdfClient);
            });
        });
        using var client = scopedFactory.CreateClient(new() { AllowAutoRedirect = false });
        var signIn = await client.GetAsync($"/test/sign-in?email={Uri.EscapeDataString(email)}");
        signIn.EnsureSuccessStatusCode();

        var response = await client.GetFromJsonAsync<CustomerProjectDetailResponse>(
            $"/quote/v1/projects/{projectId:D}");

        Assert.NotNull(response);
        Assert.Equal(projectId, response.ProjectId);
        Assert.Equal("QT-WORKSPACE-1001", response.Quote?.QuoteNumber);
        Assert.Equal(orderNumber, response.Order?.OrderNumber);
        Assert.Equal("INV-WORKSPACE-1001", response.Invoice?.InvoiceNumber);
        Assert.Equal("RCPT-WORKSPACE-1001", Assert.Single(response.Receipts).ReceiptNumber);
        Assert.Equal(
            $"/quote/v1/projects/{projectId:D}/invoices/{invoiceId:D}/document",
            response.Invoice?.DocumentUrl);
        Assert.Equal(
            $"/quote/v1/projects/{projectId:D}/receipts/{receiptId:D}/document",
            Assert.Single(response.Receipts).DocumentUrl);
        Assert.Equal("receipt-workspace.pdf", Assert.Single(response.Documents).FileName);
        Assert.Equal("production-drawing.pdf", Assert.Single(response.Order!.OrderFiles).FileName);

        using var invoiceDocument = await client.GetAsync(response.Invoice!.DocumentUrl);
        using var receiptDocument = await client.GetAsync(Assert.Single(response.Receipts).DocumentUrl);
        Assert.Equal(System.Net.HttpStatusCode.Redirect, invoiceDocument.StatusCode);
        Assert.Equal(System.Net.HttpStatusCode.Redirect, receiptDocument.StatusCode);
        Assert.StartsWith("https://test-cdn.example.com/", invoiceDocument.Headers.Location?.AbsoluteUri, StringComparison.Ordinal);
        Assert.StartsWith("https://test-cdn.example.com/", receiptDocument.Headers.Location?.AbsoluteUri, StringComparison.Ordinal);
    }

    private static void Replace<TService>(IServiceCollection services, TService implementation)
        where TService : class
    {
        services.RemoveAll<TService>();
        services.AddSingleton(implementation);
    }
}
