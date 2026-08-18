namespace Verbara.Sdk.VoiceAi.Tts.Lmnt;

/// <summary>
/// Catalog of LMNT voice identifiers curated for low-latency telephony use cases.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is a curated subset, not the roster.</b> LMNT publishes 44 system voices; the six below
/// are the ones picked for low-latency telephony. Setting <c>LmntTtsOptions.Voice</c> to any other
/// id from the vendor's list is supported and expected — these constants exist to spell the common
/// ones correctly, not to bound the choice.
/// </para>
/// <para>
/// Ids were first transcribed from LMNT's docs on 2026-05-03 and re-checked against the live
/// <c>GET /v1/ai/voice/list?owner=system</c> catalog on 2026-08-18; all six are still present. An
/// unknown voice fails loudly here — the API answers <c>400</c> with
/// <c>{"error":"Invalid voice: ..."}</c> — so this catalog is a convenience, not a guard.
/// </para>
/// <para>
/// For the full roster see https://docs.lmnt.com/reference/list-voices.
/// </para>
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

    // The other 38 system voices are enumerable via
    // GET https://api.lmnt.com/v1/ai/voice/list?owner=system and are usable without appearing here.
    // VoiceCatalogConformanceTests checks the six above against that endpoint.
}
