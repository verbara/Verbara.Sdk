namespace Verbara.Sdk.VoiceAi.Tts.Deepgram;

/// <summary>
/// Catalog of Deepgram Aura voice model identifiers.
/// All <c>aura-2-*</c> entries use the Aura 2 generation (2025-2026).
/// Legacy <c>aura-*</c> entries (no <c>-2-</c> segment) are Aura 1; kept for backwards-compat.
/// </summary>
/// <remarks>
/// <para>
/// Ids were first transcribed from Deepgram's docs on 2026-05-03 and re-checked against the live
/// <c>GET /v1/models</c> catalog and <c>POST /v1/speak</c> on 2026-08-18. Every constant below
/// synthesises. Unlike the Speechmatics preview, an unknown model here fails loudly — a bad id
/// returns <c>400</c> with <c>"No such model/version combination found."</c> — so this catalog is
/// a convenience, not a guard.
/// </para>
/// <para>
/// This is a curated subset: the live catalog carries 102 TTS models across seven languages. For
/// the full roster see https://developers.deepgram.com/docs/tts-models.
/// </para>
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

    /// <summary>Aura 2 — Apollo (English, male).</summary>
    public const string Apollo = "aura-2-apollo-en";

    /// <summary>Aura 2 — Luna (English, female).</summary>
    public const string Luna = "aura-2-luna-en";

    /// <summary>Aura 2 — Arcas (English, male).</summary>
    public const string Arcas = "aura-2-arcas-en";

    // ── Aura 2 — Spanish (es) — 17 live; the accents a Spanish-language deployment reaches for ──

    /// <summary>Aura 2 — Sirio (Spanish, male, Mexican accent).</summary>
    public const string Sirio = "aura-2-sirio-es";

    /// <summary>Aura 2 — Celeste (Spanish, female, Colombian accent).</summary>
    public const string Celeste = "aura-2-celeste-es";

    /// <summary>Aura 2 — Gloria (Spanish, female, Colombian accent).</summary>
    public const string Gloria = "aura-2-gloria-es";

    /// <summary>Aura 2 — Javier (Spanish, male, Latin American accent).</summary>
    public const string Javier = "aura-2-javier-es";

    /// <summary>Aura 2 — Selena (Spanish, female, Latin American accent).</summary>
    public const string Selena = "aura-2-selena-es";

    /// <summary>Aura 2 — Diana (Spanish, female, Peninsular accent).</summary>
    public const string Diana = "aura-2-diana-es";

    /// <summary>Aura 2 — Nestor (Spanish, male, Peninsular accent).</summary>
    public const string Nestor = "aura-2-nestor-es";

    // ── Aura 2 — Multilingual (2026 expansion) ────────────────────────────────
    // The ids below closed a TODO left open on 2026-05-03, when Deepgram had announced Dutch,
    // French, German, Italian and Japanese support but had not published the canonical id strings.
    // They were read from the live GET /v1/models catalog on 2026-08-18 and each one synthesises.
    // One masculine and one feminine voice per language, chosen from the vendor's own metadata
    // tags; the live counts are de=7, fr=2, it=9, ja=5, nl=9, so this is a subset by design.

    /// <summary>Aura 2 — Elara (German, female).</summary>
    public const string Elara = "aura-2-elara-de";

    /// <summary>Aura 2 — Fabian (German, male).</summary>
    public const string Fabian = "aura-2-fabian-de";

    /// <summary>Aura 2 — Agathe (French, female). One of only two French voices live.</summary>
    public const string Agathe = "aura-2-agathe-fr";

    /// <summary>Aura 2 — Hector (French, male). One of only two French voices live.</summary>
    public const string Hector = "aura-2-hector-fr";

    /// <summary>Aura 2 — Cinzia (Italian, female).</summary>
    public const string Cinzia = "aura-2-cinzia-it";

    /// <summary>Aura 2 — Elio (Italian, male).</summary>
    public const string Elio = "aura-2-elio-it";

    /// <summary>Aura 2 — Izanami (Japanese, female).</summary>
    public const string Izanami = "aura-2-izanami-ja";

    /// <summary>Aura 2 — Fujin (Japanese, male).</summary>
    public const string Fujin = "aura-2-fujin-ja";

    /// <summary>Aura 2 — Daphne (Dutch, female).</summary>
    public const string Daphne = "aura-2-daphne-nl";

    /// <summary>Aura 2 — Sander (Dutch, male).</summary>
    public const string Sander = "aura-2-sander-nl";

    // ── Aura 1 — Legacy (kept for migration path) ─────────────────────────────

    /// <summary>Aura 1 — Asteria (English, female). Legacy default; prefer <see cref="Thalia"/> for new integrations.</summary>
    public const string Asteria = "aura-asteria-en";

    /// <summary>Aura 1 — Orion (English, male). Legacy voice.</summary>
    public const string Orion = "aura-orion-en";

    /// <summary>Aura 1 — Stella (English, female). Legacy voice.</summary>
    public const string Stella = "aura-stella-en";

    /// <summary>
    /// Aura 1 — Helios (English, male). Legacy voice.
    /// </summary>
    /// <remarks>
    /// Helios exists only in Aura 1. Until 2.4.1 this catalog exposed it as
    /// <c>"aura-2-helios-en"</c>, an id the API rejects with <c>400 "No such model/version
    /// combination found."</c> — there is no Aura 2 Helios. The constant now carries the id that
    /// synthesises.
    /// </remarks>
    public const string HeliosLegacy = "aura-helios-en";
}
