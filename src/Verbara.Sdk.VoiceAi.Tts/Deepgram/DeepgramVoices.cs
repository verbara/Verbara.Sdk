namespace Verbara.Sdk.VoiceAi.Tts.Deepgram;

/// <summary>
/// Catalog of Deepgram Aura voice model identifiers.
/// All <c>aura-2-*</c> entries use the Aura 2 generation (2025-2026).
/// Legacy <c>aura-*</c> entries (no <c>-2-</c> segment) are Aura 1; kept for backwards-compat.
/// </summary>
/// <remarks>
/// Voice ids verified from Deepgram docs as of 2026-05-03.
/// For the up-to-date catalog see https://developers.deepgram.com/docs/tts-models.
/// </remarks>
public static class DeepgramVoices
{
    // ── Aura 2 — English (en) ─────────────────────────────────────────────────

    /// <summary>Aura 2 — Thalia (English, female). Default for <see cref="DeepgramTtsOptions.Model"/>.</summary>
    public const string Thalia = "aura-2-thalia-en";

    /// <summary>Aura 2 — Andromeda (English, female).</summary>
    public const string Andromeda = "aura-2-andromeda-en";

    /// <summary>Aura 2 — Zeus (English, male).</summary>
    public const string Zeus = "aura-2-zeus-en";

    /// <summary>Aura 2 — Orpheus (English, male).</summary>
    public const string Orpheus = "aura-2-orpheus-en";

    /// <summary>Aura 2 — Helios (English, male).</summary>
    public const string Helios = "aura-2-helios-en";

    /// <summary>Aura 2 — Apollo (English, male).</summary>
    public const string Apollo = "aura-2-apollo-en";

    /// <summary>Aura 2 — Luna (English, female).</summary>
    public const string Luna = "aura-2-luna-en";

    /// <summary>Aura 2 — Arcas (English, male).</summary>
    public const string Arcas = "aura-2-arcas-en";

    // ── Aura 2 — Spanish (es) ─────────────────────────────────────────────────

    /// <summary>Aura 2 — Sirio (Spanish, male). Verified from Deepgram multilingual expansion.</summary>
    public const string Sirio = "aura-2-sirio-es";

    // ── Aura 2 — Multilingual (2026 expansion) ────────────────────────────────
    // TODO(multilingual): Deepgram announced Aura 2 Dutch, French, German, Italian, Japanese
    // support in 2026 (https://deepgram.com/learn/aura-2-now-speaks-dutch-french-german-italian-japanese).
    // Exact voice ids for these languages (nl, fr, de, it, ja) were not confirmed in public
    // documentation at implementation time (2026-05-03). Add constants here when Deepgram
    // publishes the canonical voice id strings in their models reference page
    // (https://developers.deepgram.com/docs/tts-models).

    // ── Aura 1 — Legacy (kept for migration path) ─────────────────────────────

    /// <summary>Aura 1 — Asteria (English, female). Legacy default; prefer <see cref="Thalia"/> for new integrations.</summary>
    public const string Asteria = "aura-asteria-en";

    /// <summary>Aura 1 — Orion (English, male). Legacy voice.</summary>
    public const string Orion = "aura-orion-en";

    /// <summary>Aura 1 — Stella (English, female). Legacy voice.</summary>
    public const string Stella = "aura-stella-en";
}
