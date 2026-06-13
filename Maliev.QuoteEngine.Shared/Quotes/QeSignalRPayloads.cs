// Maliev.QuoteEngine.Shared/Quotes/QeSignalRPayloads.cs
namespace Maliev.QuoteEngine.Shared.Quotes;

/// <summary>
/// Pushed to the client via SignalR "GlbReady" when geometry analysis completes.
/// When <see cref="Failed"/> is true, <see cref="GlbUrl"/> is empty and
/// <see cref="ErrorCode"/> contains the failure reason.
/// </summary>
public sealed record QeGlbReadyPayload(
    string StoragePath,
    string GlbUrl,
    string? ThumbnailUrl,
    int BodyCount,
    bool IsManifold,
    bool Failed,
    string? ErrorCode,
    string? ViewerStoragePath = null,
    string? ViewerFileExtension = null);

/// <summary>
/// Pushed to the client via SignalR "OrderStatusChanged" when the order transitions to a new lifecycle state.
/// </summary>
public sealed record QeOrderStatusChangedPayload(
    string OrderNumber,
    string PreviousStatus,
    string NewStatus,
    DateTimeOffset ChangedAt,
    string? Reason);

/// <summary>
/// Pushed to the client via SignalR "PaymentCompleted" when the payment gateway confirms payment.
/// </summary>
public sealed record QePaymentCompletedPayload(
    string OrderNumber,
    Guid PaymentId,
    decimal Amount,
    string Currency);

/// <summary>
/// Pushed to the client via SignalR "PaymentPending" when the payment provider keeps a transaction open.
/// </summary>
public sealed record QePaymentPendingPayload(
    string OrderNumber,
    Guid PaymentId,
    decimal Amount,
    string Currency,
    string ProviderEventCode,
    DateTimeOffset PendingAt);

/// <summary>
/// Pushed to the client via SignalR "PaymentFailed" when the payment gateway rejects or cancels a payment.
/// </summary>
public sealed record QePaymentFailedPayload(
    string OrderNumber,
    Guid PaymentId,
    decimal Amount,
    string Currency,
    string ErrorMessage,
    string ProviderErrorCode,
    DateTimeOffset FailedAt);

/// <summary>
/// Pushed to the client via SignalR "PaymentCancelled" when a hosted checkout is cancelled before completion.
/// </summary>
public sealed record QePaymentCancelledPayload(
    string OrderNumber,
    Guid PaymentId,
    decimal Amount,
    string Currency,
    string Reason,
    string ProviderEventCode,
    DateTimeOffset CancelledAt);

/// <summary>
/// Pushed to the client via SignalR "PaymentExpired" when a hosted checkout expires before completion.
/// </summary>
public sealed record QePaymentExpiredPayload(
    string OrderNumber,
    Guid PaymentId,
    decimal Amount,
    string Currency,
    string Reason,
    string ProviderEventCode,
    DateTimeOffset ExpiredAt);

/// <summary>
/// Pushed to the client via SignalR "DfmAnalysisReady" when DFM analysis is ready.
/// Always reflects the merged state of all process reports received so far.
/// </summary>
public sealed record QeDfmAnalysisReadyPayload(
    string StoragePath,
    bool IsManifold,
    string? NonManifoldReason,
    QeFdmDfmReport? FdmReport,
    QeSlaDfmReport? SlaReport,
    QeCncDfmReport? CncReport,
    IReadOnlyList<string> OverlayGlbUrls,
    string? AnalysisErrorCode);
