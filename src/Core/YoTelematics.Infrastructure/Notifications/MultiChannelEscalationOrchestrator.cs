using System.Text.Json;
using Npgsql;
using YoTelematics.Application.Notifications;
using YoTelematics.Rules.Abstractions;

namespace YoTelematics.Infrastructure.Notifications;

/// <summary>
/// Production Multi-Channel Alert Escalation Engine (v3.4-FINAL Phase 4):
/// 1. Queries asset_members ordered strictly by call_priority_order ASC.
/// 2. Sends localized High-Priority FCM Push to all member push tokens (bn-BD / en-US per user).
/// 3. For CRITICAL alerts, runs sequential Asterisk ARI voice call cascade (Pre-cached Audio + Bangla/English TTS):
///    - Stops cascade immediately when a member answers and acknowledges.
///    - Sends localized SMS fallback to any member who does not answer before dialing the next member.
/// 4. Persists an immutable audit trail in security_audit_logs.
/// </summary>
public sealed class MultiChannelEscalationOrchestrator : IMultiChannelEscalationService
{
    private readonly string _workerConnectionString;
    private readonly ILocaleTemplateResolver _templateResolver;
    private readonly IFcmPushGateway _fcmGateway;
    private readonly IAsteriskAriVoiceGateway _voiceGateway;
    private readonly ISmsGateway _smsGateway;

    public MultiChannelEscalationOrchestrator(
        string workerConnectionString,
        ILocaleTemplateResolver templateResolver,
        IFcmPushGateway fcmGateway,
        IAsteriskAriVoiceGateway voiceGateway,
        ISmsGateway smsGateway)
    {
        _workerConnectionString = workerConnectionString;
        _templateResolver = templateResolver;
        _fcmGateway = fcmGateway;
        _voiceGateway = voiceGateway;
        _smsGateway = smsGateway;
    }

    private sealed record RecipientMember(
        Guid UserId,
        string Phone,
        string Locale,
        short CallPriorityOrder,
        List<(string FcmToken, string Platform)> PushTokens);

    public async Task<EscalationExecutionReport> EscalateAlertAsync(
        Guid tenantId,
        Guid assetId,
        AlertTemplateInstruction alert,
        CancellationToken cancellationToken = default)
    {
        await using var conn = new NpgsqlConnection(_workerConnectionString);
        await conn.OpenAsync(cancellationToken);

        var members = await LoadOrderedAssetMembersAsync(conn, assetId, cancellationToken);
        if (members.Count == 0)
        {
            return new EscalationExecutionReport(assetId, alert.TemplateKey, 0, 0, null, 0);
        }

        int pushSentCount = 0;
        int voiceCallsAttempted = 0;
        int smsFallbacksSent = 0;
        Guid? acknowledgedByUserId = null;

        // Stage 1: Send localized High-Priority FCM Push to all members' registered devices
        for (int i = 0; i < members.Count; i++)
        {
            var member = members[i];
            var localized = _templateResolver.Resolve(alert.TemplateKey, member.Locale, alert.Parameters);

            for (int t = 0; t < member.PushTokens.Count; t++)
            {
                var (token, platform) = member.PushTokens[t];
                bool pushOk = await _fcmGateway.SendHighPriorityPushAsync(
                    new FcmPushDispatchRequest(
                        TenantId: tenantId,
                        UserId: member.UserId,
                        FcmToken: token,
                        Platform: platform,
                        Title: localized.Title,
                        Body: localized.Body,
                        TemplateKey: alert.TemplateKey,
                        Severity: alert.Severity),
                    cancellationToken);

                if (pushOk)
                {
                    pushSentCount++;
                }
            }
        }

        // Stage 2: Sequential Asterisk ARI Voice Call Cascade + SMS Fallback for CRITICAL alerts
        bool isCritical = string.Equals(alert.Severity, "CRITICAL", StringComparison.OrdinalIgnoreCase);
        if (isCritical)
        {
            for (int i = 0; i < members.Count; i++)
            {
                var member = members[i];
                var localized = _templateResolver.Resolve(alert.TemplateKey, member.Locale, alert.Parameters);

                voiceCallsAttempted++;
                var callOutcome = await _voiceGateway.PlaceEscalationCallAsync(
                    new AsteriskVoiceCallRequest(
                        TenantId: tenantId,
                        AssetId: assetId,
                        MemberUserId: member.UserId,
                        CallPriorityOrder: member.CallPriorityOrder,
                        PhoneNumber: member.Phone,
                        PreCachedAudioUri: localized.PreCachedAudioUri,
                        TtsFallbackScript: localized.TtsScript,
                        Locale: localized.Locale),
                    cancellationToken);

                if (callOutcome.AnsweredAndAcknowledged)
                {
                    acknowledgedByUserId = member.UserId;
                    break; // Member acknowledged alarm -> stop cascading to lower-priority members!
                }

                // Member did not answer/acknowledge -> send localized SMS fallback and escalate to next member
                bool smsOk = await _smsGateway.SendSmsAsync(
                    new SmsDispatchRequest(
                        TenantId: tenantId,
                        MemberUserId: member.UserId,
                        PhoneNumber: member.Phone,
                        MessageText: localized.SmsText,
                        TemplateKey: alert.TemplateKey),
                    cancellationToken);

                if (smsOk)
                {
                    smsFallbacksSent++;
                }
            }
        }

        // Stage 3: Write Immutable Security Audit Log
        await using (var auditCmd = new NpgsqlCommand(@"
            INSERT INTO security_audit_logs (
                id, tenant_id, asset_id, actor_user_id, action_type, source_ip_or_cli, details_jsonb, created_at_utc
            ) VALUES (
                @id, @tenantId, @assetId, @ackedBy, 'ALERT_ESCALATION_EXECUTED', 'EscalationOrchestrator', @details::jsonb, NOW()
            );", conn))
        {
            string detailsJson = JsonSerializer.Serialize(new
            {
                template_key = alert.TemplateKey,
                severity = alert.Severity,
                push_sent = pushSentCount,
                voice_calls_attempted = voiceCallsAttempted,
                acknowledged_by_user_id = acknowledgedByUserId,
                sms_fallbacks_sent = smsFallbacksSent
            });

            auditCmd.Parameters.AddWithValue("id", Guid.NewGuid());
            auditCmd.Parameters.AddWithValue("tenantId", tenantId);
            auditCmd.Parameters.AddWithValue("assetId", assetId);
            auditCmd.Parameters.AddWithValue("ackedBy", (object?)acknowledgedByUserId ?? DBNull.Value);
            auditCmd.Parameters.AddWithValue("details", detailsJson);
            await auditCmd.ExecuteNonQueryAsync(cancellationToken);
        }

        return new EscalationExecutionReport(
            AssetId: assetId,
            TemplateKey: alert.TemplateKey,
            PushNotificationsSent: pushSentCount,
            VoiceCallsAttempted: voiceCallsAttempted,
            AcknowledgedByUserId: acknowledgedByUserId,
            SmsFallbacksSent: smsFallbacksSent);
    }

    private static async Task<List<RecipientMember>> LoadOrderedAssetMembersAsync(
        NpgsqlConnection conn,
        Guid assetId,
        CancellationToken cancellationToken)
    {
        var membersById = new Dictionary<Guid, RecipientMember>();
        var orderedList = new List<RecipientMember>();

        await using (var memberCmd = new NpgsqlCommand(@"
            SELECT am.user_id, u.phone, u.locale, am.call_priority_order
            FROM asset_members am
            JOIN users u ON u.id = am.user_id
            WHERE am.asset_id = @assetId
              AND u.is_active = true
            ORDER BY am.call_priority_order ASC, u.created_at_utc ASC;", conn))
        {
            memberCmd.Parameters.AddWithValue("assetId", assetId);
            await using var r = await memberCmd.ExecuteReaderAsync(cancellationToken);
            while (await r.ReadAsync(cancellationToken))
            {
                var uid = r.GetGuid(0);
                var item = new RecipientMember(
                    UserId: uid,
                    Phone: r.GetString(1),
                    Locale: r.GetString(2),
                    CallPriorityOrder: r.GetInt16(3),
                    PushTokens: new List<(string, string)>());

                membersById[uid] = item;
                orderedList.Add(item);
            }
        }

        if (orderedList.Count == 0)
        {
            return orderedList;
        }

        await using (var tokenCmd = new NpgsqlCommand(@"
            SELECT user_id, fcm_token, platform
            FROM user_push_tokens
            WHERE user_id = ANY(@userIds);", conn))
        {
            tokenCmd.Parameters.AddWithValue("userIds", membersById.Keys.ToArray());
            await using var tr = await tokenCmd.ExecuteReaderAsync(cancellationToken);
            while (await tr.ReadAsync(cancellationToken))
            {
                var uid = tr.GetGuid(0);
                if (membersById.TryGetValue(uid, out var member))
                {
                    member.PushTokens.Add((tr.GetString(1), tr.GetString(2)));
                }
            }
        }

        return orderedList;
    }
}
