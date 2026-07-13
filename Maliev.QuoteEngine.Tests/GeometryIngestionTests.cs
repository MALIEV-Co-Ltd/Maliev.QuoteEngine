using System.Text.Json;
using MassTransit;
using MassTransit.Serialization;
using Maliev.MessagingContracts;
using Maliev.MessagingContracts.Contracts.Geometry;
using Maliev.QuoteEngine.Bff;
using Maliev.QuoteEngine.Bff.Clients;
using Maliev.QuoteEngine.Bff.Consumers;
using Maliev.QuoteEngine.Bff.Hubs;
using Maliev.QuoteEngine.Bff.Services;
using Maliev.QuoteEngine.Shared.Quotes;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Maliev.QuoteEngine.Tests;

public sealed class GeometryIngestionTests
{
    private const string StoragePath = "quotes/temp/session/part.step";

    [Fact]
    public void Program_UsesOneDurableGeometryQueue_WithExplicitPublisherBindings()
    {
        var source = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "Maliev.QuoteEngine.Bff", "Program.cs"));

        Assert.Equal(1, Count(source, "ReceiveEndpoint(\"quote-engine-geometry-analysis-v1\""));
        Assert.Contains("ConfigureConsumeTopology = false", source, StringComparison.Ordinal);
        Assert.Contains("maliev.geometryservice.v1.metrics.ready", source, StringComparison.Ordinal);
        Assert.Contains("maliev.geometryservice.v1.analysis.completed", source, StringComparison.Ordinal);
        Assert.Contains("maliev.geometryservice.v1.analysis.failed", source, StringComparison.Ordinal);
        Assert.Contains("maliev.geometryservice.v1.dfm.ready", source, StringComparison.Ordinal);
        Assert.Contains("e.ConfigureConsumer<QuoteFileMetricsReadyConsumer>(context)", source, StringComparison.Ordinal);
        Assert.Contains("e.ConfigureConsumer<QuoteFileAnalyzedConsumer>(context)", source, StringComparison.Ordinal);
        Assert.Contains("e.ConfigureConsumer<QuoteFileAnalysisFailedConsumer>(context)", source, StringComparison.Ordinal);
        Assert.Contains("e.ConfigureConsumer<QuoteDfmAnalysisReadyConsumer>(context)", source, StringComparison.Ordinal);
        Assert.Equal(4, Count(source, ".ExcludeFromConfigureEndpoints()"));
        Assert.Contains("cfg.ConfigureEndpoints(context)", source, StringComparison.Ordinal);
        Assert.Contains("e.ConcurrentMessageLimit = 1", source, StringComparison.Ordinal);
    }

    [Fact]
    public void FileMetricsReadyEvent_PythonMassTransitEnvelope_DeserializesGeneratedContract()
    {
        var envelopeId = Guid.NewGuid();
        var messageId = Guid.NewGuid();
        var correlationId = Guid.NewGuid();
        var json = $$"""
            {
              "messageId": "{{envelopeId}}",
              "correlationId": "{{correlationId}}",
              "messageType": [
                "urn:message:Maliev.MessagingContracts.Contracts.Geometry:FileMetricsReadyEvent"
              ],
              "headers": { "source": "GeometryService" },
              "message": {
                "messageId": "{{messageId}}",
                "messageName": "FileMetricsReadyEvent",
                "messageType": "Event",
                "messageVersion": "1.0.0",
                "publishedBy": "GeometryService",
                "consumedBy": ["QuoteEngineBff"],
                "correlationId": "{{correlationId}}",
                "causationId": null,
                "occurredAtUtc": "2026-07-13T01:02:03Z",
                "isPublic": false,
                "payload": {
                  "fileId": "file-wire-123",
                  "storagePath": "{{StoragePath}}",
                  "metrics": {
                    "volumeCm3": 12.5,
                    "supportVolumeCm3": 1.25,
                    "surfaceAreaCm2": 42.5,
                    "boundingBox": { "x": 10, "y": 20, "z": 30 },
                    "isManifold": true,
                    "triangleCount": 456,
                    "eulerNumber": 2,
                    "nonManifoldReason": null,
                    "nonManifoldFaceCount": null
                  },
                  "processedAt": "2026-07-13T01:02:04Z",
                  "bodyCount": 1,
                  "bodies": []
                }
              }
            }
            """;
        var deserializer = SystemTextJsonMessageSerializer.Instance;

        var serializerContext = deserializer.Deserialize(
            deserializer.GetMessageBody(json),
            EmptyHeaders.Instance);

        Assert.Equal("application/vnd.masstransit+json", deserializer.ContentType.MediaType);
        Assert.Contains(
            "urn:message:Maliev.MessagingContracts.Contracts.Geometry:FileMetricsReadyEvent",
            serializerContext.SupportedMessageTypes);
        Assert.True(serializerContext.TryGetMessage<FileMetricsReadyEvent>(out var message));
        var typed = Assert.IsType<FileMetricsReadyEvent>(message);
        Assert.Equal(messageId, typed.MessageId);
        Assert.Equal("file-wire-123", typed.Payload.FileId);
        Assert.Equal(1.25, typed.Payload.Metrics.SupportVolumeCm3);
        Assert.Equal(30, typed.Payload.Metrics.BoundingBox.Z);
    }

    [Fact]
    public void FileAnalyzedEvent_PythonMassTransitEnvelope_DeserializesOptionalRequiredDrift()
    {
        const string payload = """
            {
              "fileId": "file-123",
              "storagePath": "quotes/temp/session/part.step",
              "metrics": {
                "volumeCm3": 12.5,
                "supportVolumeCm3": 1.25,
                "surfaceAreaCm2": 42.5,
                "boundingBox": { "x": 10, "y": 20, "z": 30 },
                "isManifold": true,
                "triangleCount": 456,
                "eulerNumber": 2
              },
              "processedAt": "2026-07-13T01:02:04Z",
              "glbStoragePath": "processed/part.glb",
              "bodyCount": 1,
              "bodies": []
            }
            """;

        var message = DeserializePythonEnvelope<FileAnalyzedEvent>(nameof(FileAnalyzedEvent), payload);

        Assert.Equal("file-123", message.Payload.FileId);
        Assert.Equal(StoragePath, message.Payload.StoragePath);
        Assert.Equal("processed/part.glb", message.Payload.GlbStoragePath);
        Assert.Equal(12.5, message.Payload.Metrics.VolumeCm3);
        Assert.Equal(Guid.Empty, message.Payload.CustomerId);
        Assert.Equal(Guid.Empty, message.Payload.MaterialId);
        Assert.Equal(string.Empty, message.Payload.MaterialCode);
        Assert.Equal(Guid.Empty, message.Payload.ManufacturingProcessId);
        Assert.Equal(string.Empty, message.Payload.ManufacturingProcessName);
    }

    [Fact]
    public void FileAnalysisFailedEvent_PythonMassTransitEnvelope_DeserializesGeneratedContract()
    {
        const string payload = """
            {
              "fileId": "file-123",
              "storagePath": "quotes/temp/session/part.step",
              "errorCode": "FILE_CORRUPT",
              "details": "provider-only diagnostic"
            }
            """;

        var message = DeserializePythonEnvelope<FileAnalysisFailedEvent>(nameof(FileAnalysisFailedEvent), payload);

        Assert.Equal("file-123", message.Payload.FileId);
        Assert.Equal(StoragePath, message.Payload.StoragePath);
        Assert.Equal("FILE_CORRUPT", message.Payload.ErrorCode);
        Assert.Equal("provider-only diagnostic", message.Payload.Details);
    }

    [Fact]
    public void DfmAnalysisReadyEvent_PythonMassTransitEnvelope_PreservesObjectOverlayPaths()
    {
        const string payload = """
            {
              "fileId": "file-123",
              "storagePath": "quotes/temp/session/part.step",
              "fdmReport": {
                "thinWallCount": 1,
                "overhangFaceCount": 0,
                "overhangAreaCm2": 0,
                "supportRequired": false,
                "smallDetailCount": 0,
                "issues": []
              },
              "slaReport": null,
              "cncReport": null,
              "analyzedAt": "2026-07-13T01:02:04Z",
              "overlayPaths": {
                "FDM__thin_wall": "processed/overlays/thin-wall.glb"
              },
              "bodyCount": 2,
              "nonManifoldReason": "open shell"
            }
            """;

        var message = DeserializePythonEnvelope<DfmAnalysisReadyEvent>(nameof(DfmAnalysisReadyEvent), payload);

        Assert.Equal("file-123", message.Payload.FileId);
        Assert.Equal(2, message.Payload.BodyCount);
        Assert.Equal("open shell", message.Payload.NonManifoldReason);
        var overlays = Assert.IsType<JsonElement>(message.Payload.OverlayPaths);
        Assert.Equal(JsonValueKind.Object, overlays.ValueKind);
        Assert.Equal(
            "processed/overlays/thin-wall.glb",
            overlays.GetProperty("FDM__thin_wall").GetString());
    }

    [Fact]
    public async Task MetricsConsumer_StoresFullAuthoritativeMetricsAndOrderingMetadata()
    {
        var status = new QuoteFileAnalysisStatusService();
        var occurredAt = DateTimeOffset.Parse("2026-07-13T01:02:03Z");
        var processedAt = occurredAt.AddSeconds(1);
        var messageId = Guid.NewGuid();
        var context = Substitute.For<ConsumeContext<FileMetricsReadyEvent>>();
        context.Message.Returns(BuildMetricsEvent(messageId, occurredAt, processedAt));
        context.CancellationToken.Returns(CancellationToken.None);

        await new QuoteFileMetricsReadyConsumer(
            status,
            NullLogger<QuoteFileMetricsReadyConsumer>.Instance).Consume(context);

        var stored = await status.GetStatusAsync(StoragePath);
        Assert.NotNull(stored);
        Assert.Equal("file-123", stored.FileId);
        Assert.Equal(12.5m, stored.VolumeCc);
        Assert.Equal(1.25m, stored.SupportVolumeCc);
        Assert.Equal(42.5m, stored.SurfaceAreaCm2);
        Assert.Equal(10m, stored.BoundingBoxXmm);
        Assert.Equal(20m, stored.BoundingBoxYmm);
        Assert.Equal(30m, stored.BoundingBoxZmm);
        Assert.Equal(456, stored.TriangleCount);
        Assert.Equal(messageId, stored.LastGeometryEventId);
        Assert.Equal(occurredAt, stored.LastGeometryEventOccurredAtUtc);
        Assert.Equal(processedAt, stored.LastGeometryProcessedAtUtc);
        Assert.True(stored.HasAuthoritativeGeometry);
    }

    [Fact]
    public async Task StatusService_RejectsOlderFailureAfterNewerMetrics()
    {
        var status = new QuoteFileAnalysisStatusService();
        var newer = DateTimeOffset.Parse("2026-07-13T01:02:05Z");
        await status.SetGeometryMetricsAsync(
            StoragePath, "file-123", 12.5m, 1.25m, 42.5m, 10m, 20m, 30m, 456, 1, true, null,
            Guid.NewGuid(), newer, newer);

        await status.SetFailedAsync(
            StoragePath, "file-123", "worker_failed", Guid.NewGuid(), newer.AddSeconds(-2));

        var stored = await status.GetStatusAsync(StoragePath);
        Assert.NotNull(stored);
        Assert.NotEqual("Failed", stored.Status);
        Assert.Null(stored.AnalysisErrorCode);
        Assert.Equal(12.5m, stored.VolumeCc);
    }

    [Fact]
    public async Task StatusService_LegacyUnorderedFailureCannotRegressOrderedState()
    {
        var status = new QuoteFileAnalysisStatusService();
        var at = DateTimeOffset.Parse("2026-07-13T01:02:03Z");
        await status.SetGeometryMetricsAsync(
            StoragePath, "file-123", 12.5m, 1.25m, 42.5m, 10m, 20m, 30m, 456, 1, true, null,
            Guid.NewGuid(), at, at);

        await status.SetFailedAsync(StoragePath, "legacy_failure");

        var stored = await status.GetStatusAsync(StoragePath);
        Assert.NotNull(stored);
        Assert.Equal("Processing", stored.Status);
        Assert.Null(stored.AnalysisErrorCode);
    }

    [Fact]
    public async Task StatusService_DuplicateEventAndDifferentFileIdentityCannotOverwriteState()
    {
        var status = new QuoteFileAnalysisStatusService();
        var eventId = Guid.NewGuid();
        var at = DateTimeOffset.Parse("2026-07-13T01:02:03Z");
        await status.SetGeometryMetricsAsync(
            StoragePath, "file-123", 12.5m, 1.25m, 42.5m, 10m, 20m, 30m, 456, 1, true, null,
            eventId, at, at);

        await status.SetGeometryMetricsAsync(
            StoragePath, "file-123", 99m, 9m, 99m, 99m, 99m, 99m, 999, 9, false, "duplicate",
            eventId, at.AddMinutes(1), at.AddMinutes(1));
        await status.SetGeometryMetricsAsync(
            StoragePath, "different-file", 88m, 8m, 88m, 88m, 88m, 88m, 888, 8, false, "wrong identity",
            Guid.NewGuid(), at.AddMinutes(2), at.AddMinutes(2));

        var stored = await status.GetStatusAsync(StoragePath);
        Assert.NotNull(stored);
        Assert.Equal("file-123", stored.FileId);
        Assert.Equal(12.5m, stored.VolumeCc);
        Assert.Equal(eventId, stored.LastGeometryEventId);
        Assert.True(stored.IsManifold);
    }

    [Fact]
    public async Task StatusService_NewerMetricsAfterFailureRestoresProcessingState()
    {
        var status = new QuoteFileAnalysisStatusService();
        var failedAt = DateTimeOffset.Parse("2026-07-13T01:02:03Z");
        await status.SetFailedAsync(StoragePath, "file-123", "worker_failed", Guid.NewGuid(), failedAt);

        await status.SetGeometryMetricsAsync(
            StoragePath, "file-123", 12.5m, 1.25m, 42.5m, 10m, 20m, 30m, 456, 1, true, null,
            Guid.NewGuid(), failedAt.AddSeconds(1), failedAt.AddSeconds(1));

        var stored = await status.GetStatusAsync(StoragePath);
        Assert.NotNull(stored);
        Assert.Equal("Processing", stored.Status);
        Assert.Null(stored.AnalysisErrorCode);
    }

    [Fact]
    public async Task StatusService_EqualTimestampFailureWinsOverCompletion()
    {
        var status = new QuoteFileAnalysisStatusService();
        var at = DateTimeOffset.Parse("2026-07-13T01:02:03Z");
        await status.SetGlbReadyAsync(
            StoragePath, "https://signed/part.glb", null, 1, true,
            fileId: "file-123", eventId: Guid.Parse("ffffffff-ffff-ffff-ffff-ffffffffffff"),
            occurredAtUtc: at, processedAtUtc: at);

        await status.SetFailedAsync(
            StoragePath, "file-123", "worker_failed", Guid.Parse("00000000-0000-0000-0000-000000000001"), at);

        var stored = await status.GetStatusAsync(StoragePath);
        Assert.NotNull(stored);
        Assert.Equal("Failed", stored.Status);
        Assert.Equal("worker_failed", stored.AnalysisErrorCode);
    }

    [Fact]
    public async Task StatusService_NewerCompletionClearsEarlierFailure()
    {
        var status = new QuoteFileAnalysisStatusService();
        var failedAt = DateTimeOffset.Parse("2026-07-13T01:02:03Z");
        await status.SetFailedAsync(StoragePath, "file-123", "worker_failed", Guid.NewGuid(), failedAt);

        await status.SetGlbReadyAsync(
            StoragePath, "https://signed/part.glb", null, 1, true,
            fileId: "file-123", eventId: Guid.NewGuid(),
            occurredAtUtc: failedAt.AddSeconds(1), processedAtUtc: failedAt.AddSeconds(1));

        var stored = await status.GetStatusAsync(StoragePath);
        Assert.NotNull(stored);
        Assert.Equal("GlbReady", stored.Status);
        Assert.Null(stored.AnalysisErrorCode);
    }

    [Fact]
    public async Task FileAnalyzedConsumer_PreservesEarlierFullMetricsWhenCompletionIsSparse()
    {
        var status = new QuoteFileAnalysisStatusService();
        var metricsAt = DateTimeOffset.Parse("2026-07-13T01:02:03Z");
        await status.SetGeometryMetricsAsync(
            StoragePath, "file-123", 12.5m, 1.25m, 42.5m, 10m, 20m, 30m, 456, 1, true, null,
            Guid.NewGuid(), metricsAt, metricsAt);
        var hub = CreateHub();
        var completion = new FileAnalyzedEvent
        {
            MessageId = Guid.NewGuid(),
            OccurredAtUtc = metricsAt.AddSeconds(1),
            Payload = new FileAnalyzedEventPayload
            {
                FileId = "file-123",
                StoragePath = StoragePath,
                ProcessedAt = metricsAt.AddSeconds(1),
                GlbStoragePath = "processed/part.glb",
                Metrics = new FileAnalyzedEventPayloadMetrics
                {
                    VolumeCm3 = 0,
                    SupportVolumeCm3 = 0,
                    SurfaceAreaCm2 = 0,
                    BoundingBox = new FileAnalyzedEventPayloadMetricsBoundingBox(),
                    IsManifold = true,
                    TriangleCount = 0
                }
            }
        };
        var context = Substitute.For<ConsumeContext<FileAnalyzedEvent>>();
        context.Message.Returns(completion);
        context.CancellationToken.Returns(CancellationToken.None);

        await new QuoteFileAnalyzedConsumer(
            status,
            new RecordingUploadClient(),
            hub,
            NullLogger<QuoteFileAnalyzedConsumer>.Instance).Consume(context);

        var stored = await status.GetStatusAsync(StoragePath);
        Assert.NotNull(stored);
        Assert.Equal("GlbReady", stored.Status);
        Assert.Equal(12.5m, stored.VolumeCc);
        Assert.Equal(1.25m, stored.SupportVolumeCc);
        Assert.Equal(42.5m, stored.SurfaceAreaCm2);
        Assert.Equal(10m, stored.BoundingBoxXmm);
        Assert.Equal(456, stored.TriangleCount);
        Assert.True(stored.IsManifold);
        Assert.Equal(1, stored.BodyCount);
    }

    [Fact]
    public async Task StatusService_DelayedFullMetricsBackfillSparseCompletionWithoutRegressingPreview()
    {
        var status = new QuoteFileAnalysisStatusService();
        var completionAt = DateTimeOffset.Parse("2026-07-13T01:02:05Z");
        await status.SetGlbReadyAsync(
            StoragePath, "https://signed/part.glb", null, 1, true,
            fileId: "file-123", eventId: Guid.NewGuid(),
            occurredAtUtc: completionAt, processedAtUtc: completionAt);

        await status.SetGeometryMetricsAsync(
            StoragePath, "file-123", 12.5m, 1.25m, 42.5m, 10m, 20m, 30m, 456, 2, false, "open shell",
            Guid.NewGuid(), completionAt.AddSeconds(-2), completionAt.AddSeconds(-1));

        var stored = await status.GetStatusAsync(StoragePath);
        Assert.NotNull(stored);
        Assert.Equal("GlbReady", stored.Status);
        Assert.Equal("https://signed/part.glb", stored.GlbUrl);
        Assert.True(stored.HasAuthoritativeGeometry);
        Assert.Equal(12.5m, stored.VolumeCc);
        Assert.Equal(2, stored.BodyCount);
        Assert.False(stored.IsManifold);
        Assert.Equal("open shell", stored.NonManifoldReason);
        Assert.Equal(QuoteGeometryEventPhase.Completion, (QuoteGeometryEventPhase)stored.LastGeometryEventPhase);
    }

    [Fact]
    public async Task FailureConsumer_StoresCanonicalIdentityAndRejectsOlderCompletion()
    {
        var status = new QuoteFileAnalysisStatusService();
        var failedAt = DateTimeOffset.Parse("2026-07-13T01:02:10Z");
        var failedId = Guid.NewGuid();
        var failed = new FileAnalysisFailedEvent
        {
            MessageId = failedId,
            OccurredAtUtc = failedAt,
            Payload = new FileAnalysisFailedEventPayload("file-123", StoragePath, "invalid_mesh", "internal detail")
        };
        var failedContext = Substitute.For<ConsumeContext<FileAnalysisFailedEvent>>();
        failedContext.Message.Returns(failed);
        failedContext.CancellationToken.Returns(CancellationToken.None);

        await new QuoteFileAnalysisFailedConsumer(
            status,
            NullLogger<QuoteFileAnalysisFailedConsumer>.Instance).Consume(failedContext);
        await status.SetGlbReadyAsync(
            StoragePath, "https://signed/part.glb", null, 1, true,
            fileId: "file-123", eventId: Guid.NewGuid(), occurredAtUtc: failedAt.AddSeconds(-2),
            processedAtUtc: failedAt.AddSeconds(-1));

        var stored = await status.GetStatusAsync(StoragePath);
        Assert.NotNull(stored);
        Assert.Equal("Failed", stored.Status);
        Assert.Equal("file-123", stored.FileId);
        Assert.Equal("invalid_mesh", stored.AnalysisErrorCode);
        Assert.Equal(failedId, stored.LastGeometryEventId);
    }

    [Fact]
    public async Task MetricsConsumer_RejectsMalformedNumbersWithoutClaimingAuthoritativeGeometry()
    {
        var status = new QuoteFileAnalysisStatusService();
        var message = BuildMetricsEvent(Guid.NewGuid(), DateTimeOffset.UtcNow, DateTimeOffset.UtcNow) with
        {
            Payload = BuildMetricsEvent(Guid.NewGuid(), DateTimeOffset.UtcNow, DateTimeOffset.UtcNow).Payload with
            {
                Metrics = new FileMetricsReadyEventPayloadMetrics
                {
                    VolumeCm3 = double.NaN,
                    SupportVolumeCm3 = double.NegativeInfinity,
                    SurfaceAreaCm2 = double.MaxValue,
                    BoundingBox = new FileMetricsReadyEventPayloadMetricsBoundingBox(-1, 0, double.PositiveInfinity),
                    IsManifold = false,
                    TriangleCount = -1
                }
            }
        };
        var context = Substitute.For<ConsumeContext<FileMetricsReadyEvent>>();
        context.Message.Returns(message);
        context.CancellationToken.Returns(CancellationToken.None);

        await new QuoteFileMetricsReadyConsumer(
            status,
            NullLogger<QuoteFileMetricsReadyConsumer>.Instance).Consume(context);

        var stored = await status.GetStatusAsync(StoragePath);
        Assert.NotNull(stored);
        Assert.False(stored.HasAuthoritativeGeometry);
        Assert.Null(stored.VolumeCc);
        Assert.Null(stored.SupportVolumeCc);
        Assert.Null(stored.SurfaceAreaCm2);
        Assert.Null(stored.BoundingBoxXmm);
        Assert.Null(stored.TriangleCount);
    }

    [Fact]
    public async Task StatusService_IncompleteNewerMetricsPreserveEntireValidGeometrySnapshot()
    {
        var status = new QuoteFileAnalysisStatusService();
        var at = DateTimeOffset.Parse("2026-07-13T01:02:03Z");
        var originalId = Guid.NewGuid();
        await status.SetGeometryMetricsAsync(
            StoragePath, "file-123", 12.5m, 1.25m, 42.5m, 10m, 20m, 30m, 456, 2, false, "open shell",
            originalId, at, at);

        await status.SetGeometryMetricsAsync(
            StoragePath, "file-123", null, null, null, null, null, null, null, 99, true, null,
            Guid.NewGuid(), at.AddMinutes(1), at.AddMinutes(1));

        var stored = await status.GetStatusAsync(StoragePath);
        Assert.NotNull(stored);
        Assert.Equal(originalId, stored.LastGeometryEventId);
        Assert.Equal(12.5m, stored.VolumeCc);
        Assert.Equal(2, stored.BodyCount);
        Assert.False(stored.IsManifold);
        Assert.Equal("open shell", stored.NonManifoldReason);
    }

    [Fact]
    public async Task FileAnalyzedConsumer_CallerCancellationIsRethrown()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var message = new FileAnalyzedEvent
        {
            MessageId = Guid.NewGuid(),
            OccurredAtUtc = DateTimeOffset.UtcNow,
            Payload = new FileAnalyzedEventPayload
            {
                FileId = "file-123",
                StoragePath = StoragePath,
                ProcessedAt = DateTimeOffset.UtcNow,
                GlbStoragePath = "processed/part.glb",
                Metrics = new FileAnalyzedEventPayloadMetrics()
            }
        };
        var context = Substitute.For<ConsumeContext<FileAnalyzedEvent>>();
        context.Message.Returns(message);
        context.CancellationToken.Returns(cancellation.Token);

        var consumer = new QuoteFileAnalyzedConsumer(
            new QuoteFileAnalysisStatusService(),
            new CancelingUploadClient(),
            CreateHub(),
            NullLogger<QuoteFileAnalyzedConsumer>.Instance);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => consumer.Consume(context));
    }

    [Fact]
    public async Task FileAnalyzedConsumer_DuplicateReplayDoesNotResignViewerArtifact()
    {
        var status = new QuoteFileAnalysisStatusService();
        var upload = new RecordingUploadClient();
        var at = DateTimeOffset.Parse("2026-07-13T01:02:03Z");
        var message = new FileAnalyzedEvent
        {
            MessageId = Guid.NewGuid(),
            OccurredAtUtc = at,
            Payload = new FileAnalyzedEventPayload
            {
                FileId = "file-123",
                StoragePath = StoragePath,
                ProcessedAt = at,
                GlbStoragePath = "processed/part.glb",
                Metrics = new FileAnalyzedEventPayloadMetrics
                {
                    VolumeCm3 = 12.5,
                    BoundingBox = new FileAnalyzedEventPayloadMetricsBoundingBox(10, 20, 30),
                    TriangleCount = 456,
                    IsManifold = true
                }
            }
        };
        var consumer = new QuoteFileAnalyzedConsumer(
            status, upload, CreateHub(), NullLogger<QuoteFileAnalyzedConsumer>.Instance);

        await consumer.Consume(ContextFor(message));
        await consumer.Consume(ContextFor(message));

        Assert.Single(upload.Paths);
    }

    [Fact]
    public async Task DfmConsumer_CallerCancellationIsRethrown()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var context = Substitute.For<ConsumeContext<DfmAnalysisReadyEvent>>();
        context.Message.Returns(new DfmAnalysisReadyEvent
        {
            MessageId = Guid.NewGuid(),
            OccurredAtUtc = DateTimeOffset.UtcNow,
            Payload = new DfmAnalysisReadyEventPayload
            {
                FileId = "file-123",
                StoragePath = StoragePath,
                AnalyzedAt = DateTimeOffset.UtcNow,
                FdmReport = new FdmDfmReportPayload { Issues = [] },
                OverlayPaths = new Dictionary<string, string> { ["FDM__thin_wall"] = "overlay.glb" }
            }
        });
        context.CancellationToken.Returns(cancellation.Token);
        var consumer = new QuoteDfmAnalysisReadyConsumer(
            new QuoteFileAnalysisStatusService(),
            new CancelingUploadClient(),
            CreateHub(),
            CreateMetrics(),
            NullLogger<QuoteDfmAnalysisReadyConsumer>.Instance);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => consumer.Consume(context));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DfmConsumer_SignsDictionaryOverlayValues(bool jsonElement)
    {
        var status = new QuoteFileAnalysisStatusService();
        var upload = new RecordingUploadClient();
        var raw = new Dictionary<string, string>
        {
            ["FDM__thin_wall"] = "processed/overlays/thin-wall.glb",
            ["CNC__undercut"] = "processed/overlays/undercut.glb"
        };
        object overlayPaths = jsonElement
            ? JsonSerializer.SerializeToElement(raw)
            : raw;
        var message = new DfmAnalysisReadyEvent
        {
            MessageId = Guid.NewGuid(),
            OccurredAtUtc = DateTimeOffset.Parse("2026-07-13T01:02:03Z"),
            Payload = new DfmAnalysisReadyEventPayload
            {
                FileId = "file-123",
                StoragePath = StoragePath,
                AnalyzedAt = DateTimeOffset.Parse("2026-07-13T01:02:04Z"),
                FdmReport = new FdmDfmReportPayload { Issues = [] },
                OverlayPaths = overlayPaths
            }
        };
        var context = Substitute.For<ConsumeContext<DfmAnalysisReadyEvent>>();
        context.Message.Returns(message);
        context.CancellationToken.Returns(CancellationToken.None);

        await new QuoteDfmAnalysisReadyConsumer(
            status,
            upload,
            CreateHub(),
            CreateMetrics(),
            NullLogger<QuoteDfmAnalysisReadyConsumer>.Instance).Consume(context);

        Assert.Equal(2, upload.Paths.Count);
        Assert.Contains("processed/overlays/thin-wall.glb", upload.Paths);
        Assert.Contains("processed/overlays/undercut.glb", upload.Paths);
        var stored = await status.GetStatusAsync(StoragePath);
        Assert.NotNull(stored);
        Assert.Equal(2, stored.OverlayGlbUrls.Count);
        Assert.All(stored.OverlayGlbUrls, url => Assert.StartsWith("https://signed/", url, StringComparison.Ordinal));
    }

    [Fact]
    public async Task DfmConsumer_DuplicateReplayDoesNotResignOrDuplicateOverlays()
    {
        var status = new QuoteFileAnalysisStatusService();
        var upload = new RecordingUploadClient();
        var message = new DfmAnalysisReadyEvent
        {
            MessageId = Guid.NewGuid(),
            OccurredAtUtc = DateTimeOffset.Parse("2026-07-13T01:02:03Z"),
            Payload = new DfmAnalysisReadyEventPayload
            {
                FileId = "file-123",
                StoragePath = StoragePath,
                AnalyzedAt = DateTimeOffset.Parse("2026-07-13T01:02:04Z"),
                FdmReport = new FdmDfmReportPayload { Issues = [] },
                OverlayPaths = new Dictionary<string, string> { ["FDM__thin_wall"] = "overlay.glb" }
            }
        };
        var consumer = new QuoteDfmAnalysisReadyConsumer(
            status, upload, CreateHub(), CreateMetrics(),
            NullLogger<QuoteDfmAnalysisReadyConsumer>.Instance);

        await consumer.Consume(ContextFor(message));
        await consumer.Consume(ContextFor(message with
        {
            MessageId = Guid.NewGuid(),
            Payload = message.Payload with
            {
                OverlayPaths = new Dictionary<string, string> { ["CNC__undercut"] = "second-overlay.glb" }
            }
        }));
        await consumer.Consume(ContextFor(message));

        Assert.Equal(["overlay.glb", "second-overlay.glb"], upload.Paths);
        var stored = await status.GetStatusAsync(StoragePath);
        Assert.NotNull(stored);
        Assert.Equal(2, stored.OverlayGlbUrls.Count);
    }

    [Fact]
    public async Task DfmConsumer_MalformedObjectValuesAndNonFiniteReportDoNotPoisonDelivery()
    {
        var status = new QuoteFileAnalysisStatusService();
        var upload = new RecordingUploadClient();
        var overlay = JsonSerializer.SerializeToElement(new Dictionary<string, object?>
        {
            ["valid"] = "overlay.glb",
            ["number"] = 42,
            ["nested"] = new { storagePath = "must-not-be-used.glb" }
        });
        var message = new DfmAnalysisReadyEvent
        {
            MessageId = Guid.NewGuid(),
            OccurredAtUtc = DateTimeOffset.UtcNow,
            Payload = new DfmAnalysisReadyEventPayload
            {
                FileId = "file-123",
                StoragePath = StoragePath,
                AnalyzedAt = DateTimeOffset.UtcNow,
                OverlayPaths = overlay,
                FdmReport = new FdmDfmReportPayload
                {
                    OverhangAreaCm2 = double.PositiveInfinity,
                    Issues = []
                }
            }
        };
        var consumer = new QuoteDfmAnalysisReadyConsumer(
            status, upload, CreateHub(), CreateMetrics(),
            NullLogger<QuoteDfmAnalysisReadyConsumer>.Instance);

        await consumer.Consume(ContextFor(message));

        Assert.Empty(upload.Paths);
        var stored = await status.GetStatusAsync(StoragePath);
        Assert.Null(stored);
    }


    [Fact]
    public async Task DfmConsumer_UsesAuthoritativeBodyCountAndNonManifoldReason()
    {
        var status = new QuoteFileAnalysisStatusService();
        var message = new DfmAnalysisReadyEvent
        {
            MessageId = Guid.NewGuid(),
            OccurredAtUtc = DateTimeOffset.UtcNow,
            Payload = new DfmAnalysisReadyEventPayload
            {
                FileId = "file-123",
                StoragePath = StoragePath,
                AnalyzedAt = DateTimeOffset.UtcNow,
                BodyCount = 3,
                NonManifoldReason = "open shell",
                FdmReport = new FdmDfmReportPayload { Issues = [] },
                OverlayPaths = new Dictionary<string, string>()
            }
        };

        await new QuoteDfmAnalysisReadyConsumer(
            status, new RecordingUploadClient(), CreateHub(), CreateMetrics(),
            NullLogger<QuoteDfmAnalysisReadyConsumer>.Instance).Consume(ContextFor(message));

        var stored = await status.GetStatusAsync(StoragePath);
        Assert.NotNull(stored);
        Assert.Equal(3, stored.BodyCount);
        Assert.False(stored.IsManifold);
        Assert.Equal("open shell", stored.NonManifoldReason);
    }

    [Fact]
    public async Task StatusService_GuidEmptyDfmEventsRemainMergeableForLegacyCallers()
    {
        var status = new QuoteFileAnalysisStatusService();
        await status.SetDfmReportsAsync(
            StoragePath, new QeFdmDfmReport(1, 0, 0, false, 0, []), null, null,
            [], null, null, fileId: "file-123", eventId: Guid.Empty);
        await status.SetDfmReportsAsync(
            StoragePath, null, null, new QeCncDfmReport(2, false, false, 0, false, false, false, []),
            [], null, null, fileId: "file-123", eventId: Guid.Empty);

        var stored = await status.GetStatusAsync(StoragePath);
        Assert.NotNull(stored);
        Assert.NotNull(stored.FdmReport);
        Assert.NotNull(stored.CncReport);
    }

    [Fact]
    public async Task StatusService_DfmFirstThenMetricsAndCompletionNeverRegressesDfmReadiness()
    {
        var status = new QuoteFileAnalysisStatusService();
        var at = DateTimeOffset.Parse("2026-07-13T01:02:03Z");
        await status.SetDfmReportsAsync(
            StoragePath, new QeFdmDfmReport(1, 0, 0, false, 0, []), null, null,
            [], null, null, fileId: "file-123", eventId: Guid.NewGuid(),
            occurredAtUtc: at, analyzedAtUtc: at);

        await status.SetGeometryMetricsAsync(
            StoragePath, "file-123", 12.5m, 1.25m, 42.5m, 10m, 20m, 30m, 456, 1, true, null,
            Guid.NewGuid(), at.AddSeconds(1), at.AddSeconds(1));
        Assert.Equal("DfmAnalysisReady", (await status.GetStatusAsync(StoragePath))!.Status);

        await status.SetGlbReadyAsync(
            StoragePath, "https://signed/part.glb", null, 1, true,
            fileId: "file-123", eventId: Guid.NewGuid(),
            occurredAtUtc: at.AddSeconds(2), processedAtUtc: at.AddSeconds(2));
        Assert.Equal("DfmAnalysisReady", (await status.GetStatusAsync(StoragePath))!.Status);
    }

    private static FileMetricsReadyEvent BuildMetricsEvent(
        Guid messageId,
        DateTimeOffset occurredAt,
        DateTimeOffset processedAt) => new()
        {
            MessageId = messageId,
            OccurredAtUtc = occurredAt,
            Payload = new FileMetricsReadyEventPayload
            {
                FileId = "file-123",
                StoragePath = StoragePath,
                ProcessedAt = processedAt,
                BodyCount = 1,
                Metrics = new FileMetricsReadyEventPayloadMetrics
                {
                    VolumeCm3 = 12.5,
                    SupportVolumeCm3 = 1.25,
                    SurfaceAreaCm2 = 42.5,
                    BoundingBox = new FileMetricsReadyEventPayloadMetricsBoundingBox(10, 20, 30),
                    IsManifold = true,
                    TriangleCount = 456
                }
            }
        };

    private static T DeserializePythonEnvelope<T>(string eventName, string payloadJson)
        where T : class
    {
        var envelopeId = Guid.NewGuid();
        var messageId = Guid.NewGuid();
        var correlationId = Guid.NewGuid();
        var json = $$"""
            {
              "messageId": "{{envelopeId}}",
              "correlationId": "{{correlationId}}",
              "conversationId": null,
              "sourceAddress": null,
              "destinationAddress": null,
              "messageType": [
                "urn:message:Maliev.MessagingContracts.Contracts.Geometry:{{eventName}}"
              ],
              "headers": { "source": "GeometryService" },
              "message": {
                "messageId": "{{messageId}}",
                "messageName": "{{eventName}}",
                "messageType": "Event",
                "messageVersion": "1.0.0",
                "publishedBy": "GeometryService",
                "consumedBy": ["QuoteEngineBff"],
                "correlationId": "{{correlationId}}",
                "causationId": null,
                "occurredAtUtc": "2026-07-13T01:02:03Z",
                "isPublic": false,
                "payload": {{payloadJson}}
              }
            }
            """;
        var deserializer = SystemTextJsonMessageSerializer.Instance;
        var serializerContext = deserializer.Deserialize(
            deserializer.GetMessageBody(json),
            EmptyHeaders.Instance);
        var urn = $"urn:message:Maliev.MessagingContracts.Contracts.Geometry:{eventName}";

        Assert.Equal("application/vnd.masstransit+json", deserializer.ContentType.MediaType);
        Assert.Contains(urn, serializerContext.SupportedMessageTypes);
        Assert.True(serializerContext.TryGetMessage<T>(out var message));
        return Assert.IsType<T>(message);
    }

    private static ConsumeContext<DfmAnalysisReadyEvent> ContextFor(DfmAnalysisReadyEvent message)
    {
        var context = Substitute.For<ConsumeContext<DfmAnalysisReadyEvent>>();
        context.Message.Returns(message);
        context.CancellationToken.Returns(CancellationToken.None);
        return context;
    }

    private static ConsumeContext<FileAnalyzedEvent> ContextFor(FileAnalyzedEvent message)
    {
        var context = Substitute.For<ConsumeContext<FileAnalyzedEvent>>();
        context.Message.Returns(message);
        context.CancellationToken.Returns(CancellationToken.None);
        return context;
    }

    private static IHubContext<QuoteNotificationsHub> CreateHub()
    {
        var clients = Substitute.For<IHubClients>();
        clients.Group(Arg.Any<string>()).Returns(Substitute.For<IClientProxy>());
        var hub = Substitute.For<IHubContext<QuoteNotificationsHub>>();
        hub.Clients.Returns(clients);
        return hub;
    }

    private static BffMetrics CreateMetrics()
    {
        var provider = new ServiceCollection().AddMetrics().BuildServiceProvider();
        return new BffMetrics(provider.GetRequiredService<System.Diagnostics.Metrics.IMeterFactory>());
    }

    private static int Count(string source, string value)
    {
        var count = 0;
        for (var index = 0; (index = source.IndexOf(value, index, StringComparison.Ordinal)) >= 0; index += value.Length)
        {
            count++;
        }

        return count;
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Maliev.QuoteEngine.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new DirectoryNotFoundException("Could not locate QuoteEngine repository root.");
    }

    private sealed class RecordingUploadClient()
        : QuoteUploadServiceClient(new HttpClient(), NullLogger<QuoteUploadServiceClient>.Instance)
    {
        public List<string> Paths { get; } = [];

        public override Task<string> GetDownloadUrlByPathAsync(
            string storagePath,
            int expirationMinutes = 60,
            CancellationToken ct = default)
        {
            Paths.Add(storagePath);
            return Task.FromResult($"https://signed/{storagePath}");
        }
    }

    private sealed class CancelingUploadClient()
        : QuoteUploadServiceClient(new HttpClient(), NullLogger<QuoteUploadServiceClient>.Instance)
    {
        public override Task<string> GetDownloadUrlByPathAsync(
            string storagePath,
            int expirationMinutes = 60,
            CancellationToken ct = default) => Task.FromCanceled<string>(ct);
    }
}
