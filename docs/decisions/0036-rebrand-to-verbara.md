# ADR-0036: Rebrand to Verbara

- **Status:** Accepted
- **Date:** 2026-05-03
- **Deciders:** Verbara maintainer (Harol A. Reina H.)
- **Related:** [ADR-0002](0002-open-core-mit-plus-pro.md) (open-core MIT + Pro), [ADR-0027](0027-stewardship-pledge-mit-commercial.md) (stewardship pledge), [Platform ADR-0016](https://github.com/Harol-Reina/Asterisk.Platform/blob/main/docs/decisions/0016-license-and-rebrand-to-verbara.md), [Web ADR-0006](https://github.com/Harol-Reina/Asterisk.Platform.Web/blob/main/docs/decisions/0006-license-and-commercial-tier-strategy.md)

## Context

The umbrella product family that includes this SDK has been published under the "Asterisk." prefix (`Asterisk.Sdk`, `Asterisk.Sdk.Pro`, `Asterisk.Platform`, `Asterisk.Platform.Web`). Trademark verification revealed that **"Asterisk" is a registered trademark of Sangoma Technologies / Digium** (Sangoma acquired Digium in 2018), referring to the Asterisk PBX project. Continuing to use "Asterisk" as a brand prefix for our umbrella product is a trademark-infringement risk.

Precedent: the **FreePBX** project was forced to rename in v2.0 because of this trademark. Continuing creates a 3-12 month timeline to forced rename under brand pressure — the worst possible scenario, after brand equity has accrued.

This SDK's relationship to Asterisk PBX is fundamentally different from a brand-conflict standpoint:

- The SDK **targets** Asterisk PBX (Sangoma's product) as a runtime dependency. References like "the AMI client" or "the ARI WebSocket" refer to Asterisk PBX protocols and remain accurate forever.
- The SDK as a **product** (the NuGet packages we publish, the API surface we maintain) needs an independent brand to avoid trademark conflict.

The decision applies the rebrand selectively: the **product brand** changes; **technical references** to the Asterisk PBX protocols stay (because they describe what the SDK targets).

## Decision

### Rebrand: Asterisk.Sdk → Verbara Sdk

The product brand is **Verbara** (verbara.io). New public artifacts (LICENSE notices, NOTICE files, README headers, package descriptions) refer to **Verbara Sdk**. The MIT license is unchanged; only the brand changes.

**Selective scope:**

- **Brand renames:** Repository name, NuGet package IDs, .NET namespaces, README title, marketing copy.
- **Brand stays:** Technical references to the Asterisk PBX product (the `Asterisk.PBX` runtime, AMI/ARI/AGI protocol names, Sangoma's product names). These are correct and remain.

### Migration timing

This ADR establishes the **policy**. Technical migration (repo rename, NuGet package re-publishing, namespace migration) is executed in a coordinated cross-repo rebrand track post-Track 1A:

| Current | Future | Notes |
|---|---|---|
| `Asterisk.Sdk` (repo) | `verbara-sdk` (repo) | GitHub redirect 301 maintained |
| `Asterisk.Sdk` (NuGet) | `Verbara.Sdk` (NuGet) | Dual-publish for 12 months as deprecation path |
| `Asterisk.Sdk.Hosting` (NuGet) | `Verbara.Sdk.Hosting` (NuGet) | Dual-publish |
| `namespace Asterisk.Sdk` (.NET) | `namespace Verbara.Sdk` (.NET) | Coordinated build-time alias for ~6 months |
| `https://github.com/Harol-Reina/Asterisk.Sdk` | `https://github.com/verbara/verbara-sdk` | Redirect maintained by GitHub |

### Identity infrastructure (cross-repo, established 2026-05-03)

- **Domain:** `verbara.io` (registered)
- **Email aliases (Cloudflare Email Routing, free):** `legal@verbara.io`, `security@verbara.io`, `licensing@verbara.io`, `hello@verbara.io`
- **GitHub organization:** `github.com/verbara`
- **Brand tagline:** *"Open-core honest contact-center stack — MIT SDK, commercial Pro overlays."*

### License unchanged

This SDK remains under **MIT License**. The rebrand does not change licensing terms. The license matrix across the umbrella ecosystem is:

| Repo | License | Status |
|---|---|---|
| Verbara Sdk (this repo) | MIT | Unchanged |
| Verbara Sdk Pro | Commercial | Unchanged |
| Verbara Platform | Apache 2.0 | NEW (was unspecified, see Platform ADR-0016) |
| Verbara Web | Apache 2.0 | NEW (was unspecified, see Web ADR-0006) |

## Consequences

**Positive:**
- Trademark exposure eliminated before the SDK reaches enough mind-share that a Sangoma cease-and-desist would be commercially damaging.
- Distinct brand from Sangoma's Asterisk PBX clarifies positioning — "Verbara is the .NET SDK; Asterisk PBX is the runtime we target."
- Coherent umbrella branding across SDK / Pro / Platform / Web.
- MIT license unchanged → existing users see no licensing impact during migration.

**Negative:**
- NuGet download counters / GitHub stars on the current `Asterisk.Sdk` packages will not transfer automatically to `Verbara.Sdk`. Mitigation: dual-publish for 12 months, prominent migration notice, redirect from old packages to new.
- Documentation / blog posts / Stack Overflow answers referring to "Asterisk.Sdk" will become slightly stale. Mitigation: redirects + deprecation notes in old packages.
- Brief period of brand confusion during the transition (~2-4 weeks public-facing; longer for SEO).

**Trade-off:**
- Trades short-term mind-share friction (rebrand cost) for long-term brand independence and trademark safety. Given the SDK is pre-revenue and the umbrella product is rebranding holistically, the cost is minimal compared to a forced rename mid-launch.

## Alternatives considered

- **Keep `Asterisk.Sdk` and accept trademark risk.** Rejected — see FreePBX precedent. Sangoma has demonstrated willingness to enforce; the cost of forced rename mid-launch is much higher than rebranding pre-launch.

- **Keep `Asterisk.Sdk` and seek explicit trademark license from Sangoma.** Rejected — uncertain outcome (Sangoma may decline, condition the license, or charge a fee), introduces ongoing dependency on a competitor for our brand permission.

- **Rebrand only the umbrella product (Platform / Web / Pro) but keep the SDK as `Asterisk.Sdk`.** Rejected — inconsistent branding. The SDK is the most-public-facing artifact (NuGet downloads, GitHub stars). If the SDK keeps "Asterisk" branding while the rest of the family is "Verbara", the message is confusing.

- **Different name than Verbara.** Considered ~80 candidates across categories (astronomy, Latin/Greek, made-up, communication metaphors). Verbara was chosen for: (a) GitHub username + verbara.io / .dev / .app available; (b) Latin etymology (*verbum*) communicates "voice/word"; (c) Spanish/Portuguese-friendly for LATAM market; (d) USPTO basic search clean; (e) no major brand conflict in CCaaS or telecom. See [Web ADR-0006](https://github.com/Harol-Reina/Asterisk.Platform.Web/blob/main/docs/decisions/0006-license-and-commercial-tier-strategy.md) for the full naming process.

- **Rename earlier (when first published) or later (after $1M ARR).** Now is the right moment: late enough that the brand decision is informed by real market analysis, early enough that the migration cost is bounded (~3-5 weeks calendar work across 4 repos, no enterprise customer contracts to renegotiate).
