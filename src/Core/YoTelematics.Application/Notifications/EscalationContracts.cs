using YoTelematics.Rules.Abstractions;

namespace YoTelematics.Application.Notifications;

public sealed record FcmPushDispatchRequest(
    Guid TenantId,
    Guid UserId,
    string FcmToken,
    string Platform,
    string Title,
    string Body,
    string TemplateKey,
    string Severity);

public sealed record AsteriskVoiceCallRequest(
    Guid TenantId,
    Guid AssetId,
    Guid MemberUserId,
    short CallPriorityOrder,
    string PhoneNumber,
    string PreCachedAudioUri,
    string TtsFallbackScript,
    string Locale);

public sealed record AsteriskVoiceCallResult(
    bool CallConnected,
    bool AnsweredAndAcknowledged,
    string? HangupCause);

public sealed record SmsDispatchRequest(
    Guid TenantId,
    Guid MemberUserId,
    string PhoneNumber,
    string MessageText,
    string TemplateKey);

public interface IFcmPushGateway
{
    Task<bool> SendHighPriorityPushAsync(
        FcmPushDispatchRequest request,
        CancellationToken cancellationToken = default);
}

public interface IAsteriskAriVoiceGateway
{
    Task<AsteriskVoiceCallResult> PlaceEscalationCallAsync(
        AsteriskVoiceCallRequest request,
        CancellationToken cancellationToken = default);
}

public interface ISmsGateway
{
    Task<bool> SendSmsAsync(
        SmsDispatchRequest request,
        CancellationToken cancellationToken = default);
}

public sealed record EscalationExecutionReport(
    Guid AssetId,
    string TemplateKey,
    int PushNotificationsSent,
    int VoiceCallsAttempted,
    Guid? AcknowledgedByUserId,
    int SmsFallbacksSent);

public interface IMultiChannelEscalationService
{
    Task<EscalationExecutionReport> EscalateAlertAsync(
        Guid tenantId,
        Guid assetId,
        AlertTemplateInstruction alert,
        CancellationToken cancellationToken = default);
}
