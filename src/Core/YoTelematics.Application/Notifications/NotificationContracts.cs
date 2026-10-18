namespace YoTelematics.Application.Notifications;

public sealed record ResolvedAlertMessage(
    string TemplateKey,
    string Locale,
    string Title,
    string Body,
    string SmsText,
    string PreCachedAudioUri,
    string TtsScript);

public interface ILocaleTemplateResolver
{
    ResolvedAlertMessage Resolve(
        string templateKey,
        string? locale,
        IReadOnlyDictionary<string, string>? parameters = null);
}

public sealed record TenantSipAriConfig(
    string AriEndpointUrl,
    string AriUsername,
    string AriPassword,
    string CallerIdNumber,
    string TrunkName);

public sealed record TenantTtsConfig(
    string ProviderName, // AzureCognitiveServices or GoogleCloudTts
    string ApiKey,
    string RegionOrProject,
    string VoiceName);

public interface ITenantProviderConfigStore
{
    Task UpsertConfigAsync<TConfig>(
        Guid tenantId,
        string providerType,
        TConfig config,
        CancellationToken cancellationToken = default);

    Task<TConfig?> GetActiveConfigAsync<TConfig>(
        Guid tenantId,
        string providerType,
        CancellationToken cancellationToken = default);
}
