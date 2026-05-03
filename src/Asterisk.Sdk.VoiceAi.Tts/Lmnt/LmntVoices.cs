namespace Asterisk.Sdk.VoiceAi.Tts.Lmnt;

/// <summary>
/// Catalog of LMNT voice identifiers curated for low-latency telephony use cases.
/// </summary>
/// <remarks>
/// Voice ids verified from LMNT docs as of 2026-05-03.
/// For the up-to-date catalog see https://docs.lmnt.com/reference/list-voices.
/// </remarks>
public static class LmntVoices
{
    // ── LMNT System Voices (low-latency, telephony-ready) ────────────────────

    /// <summary>Leah — female, English. Default voice for <c>LmntTtsOptions.Voice</c>.</summary>
    public const string Leah = "leah";

    /// <summary>Amy — female, English.</summary>
    public const string Amy = "amy";

    /// <summary>Ansel — male, English.</summary>
    public const string Ansel = "ansel";

    /// <summary>Elowen — female, English.</summary>
    public const string Elowen = "elowen";

    /// <summary>Daniel — male, English.</summary>
    public const string Daniel = "daniel";

    /// <summary>Lily — female, English.</summary>
    public const string Lily = "lily";

    // Additional system voices can be enumerated via GET https://api.lmnt.com/v1/ai/voice/list?owner=system.
    // Verify and expand this list at integration test time.
}
