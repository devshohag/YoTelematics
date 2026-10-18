using YoTelematics.Application.Notifications;

namespace YoTelematics.Infrastructure.Notifications;

/// <summary>
/// Resolves localized push, SMS, pre-cached Asterisk audio URI, and official Bangla/English TTS scripts
/// strictly from template keys (Golden Rule 3: Zero hardcoded message strings in core/rules).
/// </summary>
public sealed class LocaleTemplateResolver : ILocaleTemplateResolver
{
    private sealed record TemplateEntry(
        string Title,
        string Body,
        string SmsText,
        string AudioSlug,
        string TtsScript);

    private static readonly IReadOnlyDictionary<(string Key, string Locale), TemplateEntry> Catalog =
        new Dictionary<(string Key, string Locale), TemplateEntry>
        {
            // Bangla (bn-BD) Templates
            [("alert.bike.theft", "bn-BD")] = new(
                Title: "জরুরি সতর্কতা: {asset_name} চুরি বা অননুমোদিত স্টার্ট!",
                Body: "আপনার {asset_name} আর্মড থাকা অবস্থায় চালু করা হয়েছে। অনুগ্রহ করে এখনই অ্যাপে চেক করুন।",
                SmsText: "YoTracker জরুরি: আপনার {asset_name} অননুমোদিতভাবে স্টার্ট হয়েছে!",
                AudioSlug: "alert_bike_theft",
                TtsScript: "সতর্কতা! আপনার {asset_name} অননুমোদিতভাবে চালু করা হয়েছে। ইঞ্জিন লক করতে অ্যাপে প্রবেশ করুন।"),

            [("alert.bike.power_cut", "bn-BD")] = new(
                Title: "জরুরি সতর্কতা: {asset_name}-এর মেইন পাওয়ার বিচ্ছিন্ন!",
                Body: "আপনার {asset_name}-এর ব্যাটারি বা ডিভাইসের মেইন পাওয়ার লাইন কেটে ফেলা হয়েছে।",
                SmsText: "YoTracker জরুরি: আপনার {asset_name}-এর মেইন পাওয়ার বিচ্ছিন্ন করা হয়েছে!",
                AudioSlug: "alert_bike_power_cut",
                TtsScript: "সতর্কতা! আপনার {asset_name} থেকে জিপিএস ডিভাইসের মেইন পাওয়ার বিচ্ছিন্ন করা হয়েছে।"),

            [("alert.bike.displacement", "bn-BD")] = new(
                Title: "জরুরি সতর্কতা: {asset_name} পার্কিং থেকে সরে গেছে!",
                Body: "আপনার {asset_name} ইগনিশন ছাড়াই পার্কিং অবস্থান থেকে {distance_m} মিটার সরে গেছে।",
                SmsText: "YoTracker জরুরি: আপনার {asset_name} পার্কিং থেকে {distance_m} মিটার সরে গেছে!",
                AudioSlug: "alert_bike_displacement",
                TtsScript: "সতর্কতা! আপনার {asset_name} পার্কিং স্থান থেকে {distance_m} মিটার দূরে সরে গেছে।"),

            [("alert.bike.vibration_warning", "bn-BD")] = new(
                Title: "সতর্কতা: {asset_name}-এ বারবার ঝাঁকুনি শনাক্ত!",
                Body: "আপনার পার্ক করা {asset_name}-এ পরপর {vibration_count} বার ভাইব্রেশন পাওয়া গেছে।",
                SmsText: "YoTracker: আপনার {asset_name}-এ {vibration_count} বার ভাইব্রেশন শনাক্ত হয়েছে।",
                AudioSlug: "alert_bike_vibration",
                TtsScript: "আপনার পার্ক করা {asset_name}-এ অস্বাভাবিক ঝাঁকুনি শনাক্ত হয়েছে।"),

            [("alert.kid.sos", "bn-BD")] = new(
                Title: "জরুরি এসওএস: {asset_name} সাহায্য চেয়েছে!",
                Body: "{asset_name} ঘড়ির এসওএস বাটন চেপেছে। অবিলম্বে লোকেশন দেখুন।",
                SmsText: "YoTracker SOS: {asset_name} জরুরি সাহায্য চেয়েছে!",
                AudioSlug: "alert_kid_sos",
                TtsScript: "জরুরি বার্তা! {asset_name} এসওএস বাটন চেপে সাহায্য চেয়েছে।"),

            [("alert.pet.leash_breach", "bn-BD")] = new(
                Title: "সতর্কতা: {asset_name} নিরাপদ সীমার বাইরে গেছে!",
                Body: "{asset_name} নির্ধারিত সেফ জোন অতিক্রম করেছে।",
                SmsText: "YoTracker: {asset_name} সেফ জোনের বাইরে চলে গেছে!",
                AudioSlug: "alert_pet_leash_breach",
                TtsScript: "সতর্কতা! আপনার {asset_name} নিরাপদ সীমানার বাইরে চলে গেছে।"),

            // English (en-US) Templates
            [("alert.bike.theft", "en-US")] = new(
                Title: "CRITICAL ALERT: Unauthorized ignition on {asset_name}!",
                Body: "Your armed {asset_name} had its ignition turned on without authorization.",
                SmsText: "YoTracker CRITICAL: Unauthorized ignition detected on {asset_name}!",
                AudioSlug: "alert_bike_theft",
                TtsScript: "Critical alert! Unauthorized ignition detected on {asset_name}. Please check your app immediately."),

            [("alert.bike.power_cut", "en-US")] = new(
                Title: "CRITICAL ALERT: Main power disconnected on {asset_name}!",
                Body: "External battery power to the tracker on {asset_name} was cut.",
                SmsText: "YoTracker CRITICAL: Main power cut on {asset_name}!",
                AudioSlug: "alert_bike_power_cut",
                TtsScript: "Critical alert! Main battery power has been disconnected on {asset_name}."),

            [("alert.bike.displacement", "en-US")] = new(
                Title: "CRITICAL ALERT: {asset_name} moved while parked!",
                Body: "{asset_name} moved {distance_m} meters from its parked location without ignition.",
                SmsText: "YoTracker CRITICAL: {asset_name} moved {distance_m}m while parked!",
                AudioSlug: "alert_bike_displacement",
                TtsScript: "Critical alert! {asset_name} has moved {distance_m} meters from its parked location."),

            [("alert.bike.vibration_warning", "en-US")] = new(
                Title: "WARNING: Repeated vibration on {asset_name}",
                Body: "Detected {vibration_count} vibrations on parked {asset_name} within 60 seconds.",
                SmsText: "YoTracker: {vibration_count} vibrations detected on {asset_name}.",
                AudioSlug: "alert_bike_vibration",
                TtsScript: "Warning! Repeated vibration detected on parked {asset_name}."),

            [("alert.kid.sos", "en-US")] = new(
                Title: "EMERGENCY SOS: {asset_name} pressed SOS!",
                Body: "{asset_name} triggered an emergency SOS alert.",
                SmsText: "YoTracker SOS: Emergency triggered by {asset_name}!",
                AudioSlug: "alert_kid_sos",
                TtsScript: "Emergency SOS alert! {asset_name} has pressed the SOS button."),

            [("alert.pet.leash_breach", "en-US")] = new(
                Title: "ALERT: {asset_name} left the safe zone!",
                Body: "{asset_name} has breached the safe geofence boundary.",
                SmsText: "YoTracker: {asset_name} left the safe zone!",
                AudioSlug: "alert_pet_leash_breach",
                TtsScript: "Alert! {asset_name} has left the designated safe zone.")
        };

    public ResolvedAlertMessage Resolve(
        string templateKey,
        string? locale,
        IReadOnlyDictionary<string, string>? parameters = null)
    {
        string normalizedLocale = NormalizeLocale(locale);
        string normalizedKey = templateKey.Trim().ToLowerInvariant();

        if (!Catalog.TryGetValue((normalizedKey, normalizedLocale), out var entry) &&
            !Catalog.TryGetValue((normalizedKey, "bn-BD"), out entry))
        {
            entry = new TemplateEntry(
                Title: $"Alert: {normalizedKey} ({{asset_name}})",
                Body: $"Security event {normalizedKey} on {{asset_name}}.",
                SmsText: $"YoTracker: {normalizedKey} on {{asset_name}}",
                AudioSlug: "alert_generic",
                TtsScript: $"Security alert on {{asset_name}}.");
        }

        string title = SubstituteTokens(entry.Title, parameters);
        string body = SubstituteTokens(entry.Body, parameters);
        string sms = SubstituteTokens(entry.SmsText, parameters);
        string tts = SubstituteTokens(entry.TtsScript, parameters);
        string audioUri = $"sound:yotelematics/{normalizedLocale}/{entry.AudioSlug}";

        return new ResolvedAlertMessage(
            TemplateKey: normalizedKey,
            Locale: normalizedLocale,
            Title: title,
            Body: body,
            SmsText: sms,
            PreCachedAudioUri: audioUri,
            TtsScript: tts);
    }

    private static string NormalizeLocale(string? locale)
    {
        if (string.IsNullOrWhiteSpace(locale))
        {
            return "bn-BD";
        }

        return locale.StartsWith("en", StringComparison.OrdinalIgnoreCase)
            ? "en-US"
            : "bn-BD";
    }

    private static string SubstituteTokens(string template, IReadOnlyDictionary<string, string>? parameters)
    {
        if (parameters is null || parameters.Count == 0)
        {
            return template.Replace("{asset_name}", "Asset", StringComparison.OrdinalIgnoreCase);
        }

        string output = template;
        foreach (var kvp in parameters)
        {
            output = output.Replace($"{{{kvp.Key}}}", kvp.Value, StringComparison.OrdinalIgnoreCase);
        }

        return output;
    }
}
