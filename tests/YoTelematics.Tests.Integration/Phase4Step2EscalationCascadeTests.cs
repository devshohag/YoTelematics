using FluentAssertions;
using Npgsql;
using YoTelematics.Application.Notifications;
using YoTelematics.Infrastructure.Notifications;
using YoTelematics.Rules.Abstractions;

namespace YoTelematics.Tests.Integration;

public sealed class Phase4Step2EscalationCascadeTests
{
    private const string WorkerConn =
        "Host=localhost;Port=5432;Database=yotelematics_db;Username=yoworker_service;Password=YoWorkerPass2026!";

    [Fact]
    public async Task AT_4_2_1_SequentialVoiceCascade_CallsPriority1First_SendsSmsFallbackOnNoAnswer_ThenStopsWhenPriority2Answers()
    {
        var tenantId = Guid.NewGuid();
        var assetId = Guid.NewGuid();
        var member1Id = Guid.NewGuid(); // Priority 1 (bn-BD) -> Does NOT answer call
        var member2Id = Guid.NewGuid(); // Priority 2 (en-US) -> Answers & acknowledges call!
        var member3Id = Guid.NewGuid(); // Priority 3 (bn-BD) -> Must NOT be called because Member 2 acknowledged!

        string suffix = Guid.NewGuid().ToString("N")[..8];

        await using (var conn = new NpgsqlConnection(WorkerConn))
        {
            await conn.OpenAsync();
            await using var cmd = new NpgsqlCommand($@"
                INSERT INTO tenants (id, hierarchy_path, name, slug)
                VALUES ('{tenantId}', 'root.esc_{tenantId:N}', 'Escalation Tenant', 'esc-{tenantId:N}');

                INSERT INTO users (id, tenant_id, phone, role, locale) VALUES
                ('{member1Id}', '{tenantId}', '+8801711{suffix[..6]}', 'Customer', 'bn-BD'),
                ('{member2Id}', '{tenantId}', '+8801811{suffix[..6]}', 'Customer', 'en-US'),
                ('{member3Id}', '{tenantId}', '+8801911{suffix[..6]}', 'Customer', 'bn-BD');

                INSERT INTO user_push_tokens (id, tenant_id, user_id, fcm_token, platform) VALUES
                ('{Guid.NewGuid()}', '{tenantId}', '{member1Id}', 'fcm-token-m1-{suffix}', 'android'),
                ('{Guid.NewGuid()}', '{tenantId}', '{member2Id}', 'fcm-token-m2-{suffix}', 'ios'),
                ('{Guid.NewGuid()}', '{tenantId}', '{member3Id}', 'fcm-token-m3-{suffix}', 'android');

                INSERT INTO assets (id, tenant_id, category, name, subscription_expires_at_utc)
                VALUES ('{assetId}', '{tenantId}', 'BIKE', 'Suzuki Gixxer Dhaka', NOW() + INTERVAL '30 days');

                INSERT INTO asset_members (asset_id, tenant_id, user_id, member_role, call_priority_order, can_control_engine) VALUES
                ('{assetId}', '{tenantId}', '{member1Id}', 'Owner', 1, true),
                ('{assetId}', '{tenantId}', '{member2Id}', 'Spouse', 2, true),
                ('{assetId}', '{tenantId}', '{member3Id}', 'Brother', 3, false);
            ", conn);
            await cmd.ExecuteNonQueryAsync();
        }

        var fcmSpy = new SpyFcmPushGateway();
        var voiceSpy = new SpyAsteriskVoiceGateway(answeringUserId: member2Id);
        var smsSpy = new SpySmsGateway();
        var resolver = new LocaleTemplateResolver();

        var orchestrator = new MultiChannelEscalationOrchestrator(
            WorkerConn, resolver, fcmSpy, voiceSpy, smsSpy);

        var theftAlert = new AlertTemplateInstruction(
            TemplateKey: "alert.bike.theft",
            Severity: "CRITICAL",
            Parameters: new Dictionary<string, string>
            {
                ["asset_id"] = assetId.ToString("D"),
                ["asset_name"] = "Suzuki Gixxer Dhaka"
            });

        var report = await orchestrator.EscalateAlertAsync(tenantId, assetId, theftAlert);

        // 1. All 3 members received High-Priority FCM Push in their own locale (bn-BD / en-US)
        report.PushNotificationsSent.Should().Be(3);
        fcmSpy.SentPushes.Should().HaveCount(3);
        fcmSpy.SentPushes.Single(p => p.UserId == member1Id).Title.Should().Contain("জরুরি সতর্কতা");
        fcmSpy.SentPushes.Single(p => p.UserId == member2Id).Title.Should().Contain("CRITICAL ALERT");

        // 2. Voice Cascade: Called Member 1 (Priority 1, no answer) -> Called Member 2 (Priority 2, answered!) -> Stopped before Member 3!
        report.VoiceCallsAttempted.Should().Be(2);
        report.AcknowledgedByUserId.Should().Be(member2Id);
        voiceSpy.CallAttempts.Select(c => c.MemberUserId).Should().Equal(member1Id, member2Id);
        voiceSpy.CallAttempts[0].PreCachedAudioUri.Should().Be("sound:yotelematics/bn-BD/alert_bike_theft");
        voiceSpy.CallAttempts[1].PreCachedAudioUri.Should().Be("sound:yotelematics/en-US/alert_bike_theft");

        // 3. SMS Fallback: Sent ONLY to Member 1 (who missed the voice call), never to Member 2 or Member 3
        report.SmsFallbacksSent.Should().Be(1);
        smsSpy.SentSms.Should().ContainSingle(s => s.MemberUserId == member1Id);

        // 4. Verify immutable audit log entry in security_audit_logs
        await using (var conn = new NpgsqlConnection(WorkerConn))
        {
            await conn.OpenAsync();
            await using var auditCmd = new NpgsqlCommand(
                "SELECT COUNT(*) FROM security_audit_logs WHERE asset_id = @a AND action_type = 'ALERT_ESCALATION_EXECUTED';",
                conn);
            auditCmd.Parameters.AddWithValue("a", assetId);
            long auditCount = (long)(await auditCmd.ExecuteScalarAsync())!;
            auditCount.Should().Be(1);
        }
    }

    private sealed class SpyFcmPushGateway : IFcmPushGateway
    {
        public List<FcmPushDispatchRequest> SentPushes { get; } = new();

        public Task<bool> SendHighPriorityPushAsync(FcmPushDispatchRequest request, CancellationToken cancellationToken = default)
        {
            SentPushes.Add(request);
            return Task.FromResult(true);
        }
    }

    private sealed class SpyAsteriskVoiceGateway : IAsteriskAriVoiceGateway
    {
        private readonly Guid _answeringUserId;
        public List<AsteriskVoiceCallRequest> CallAttempts { get; } = new();

        public SpyAsteriskVoiceGateway(Guid answeringUserId) => _answeringUserId = answeringUserId;

        public Task<AsteriskVoiceCallResult> PlaceEscalationCallAsync(
            AsteriskVoiceCallRequest request,
            CancellationToken cancellationToken = default)
        {
            CallAttempts.Add(request);
            bool answered = request.MemberUserId == _answeringUserId;
            return Task.FromResult(new AsteriskVoiceCallResult(
                CallConnected: answered,
                AnsweredAndAcknowledged: answered,
                HangupCause: answered ? "NORMAL_CLEARING_DTMF_ACK" : "NO_ANSWER"));
        }
    }

    private sealed class SpySmsGateway : ISmsGateway
    {
        public List<SmsDispatchRequest> SentSms { get; } = new();

        public Task<bool> SendSmsAsync(SmsDispatchRequest request, CancellationToken cancellationToken = default)
        {
            SentSms.Add(request);
            return Task.FromResult(true);
        }
    }
}
