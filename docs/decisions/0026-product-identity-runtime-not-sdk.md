# ADR-0026: Product identity — "Asterisk Runtime for .NET" (not "SDK")

- **Status:** Proposed
- **Date:** 2026-04-20
- **Deciders:** Harold Reina
- **Related:**
  - Product Strategy Document v2: `Asterisk.Platform/docs/specs/2026-04-19-product-strategy-v2.md` §4
  - ADR-0023 (PublicApi tracker adoption): `0023-publicapi-tracker-adoption.md`
  - Roadmap: `docs/roadmap.md`

## Context

El repositorio `Asterisk.Sdk` se describe externamente como **"The modern .NET SDK for Asterisk PBX"** (`README.md:3`) y metadata `PackageTags=sdk`, `<Product>Asterisk.Sdk</Product>`. Sin embargo, el código shipped es categóricamente distinto de un "SDK":

- **13 `IHostedService`/`BackgroundService`** activos — un SDK típico no tiene hosted services.
- **2 aggregate roots** con lifecycle (`AsteriskServer`, `CallSessionManager`).
- **In-memory pub/sub broker** (`RxPushEventBus` + `Channel<T>`) — un SDK no embebe broker.
- **Persistence backends** (Redis + Postgres via Sessions stores).
- **SSE HTTP endpoints** (`Push.AspNetCore`).
- **NATS bidirectional bridge** (shipped v1.13).
- **Voice AI pipeline** (30% del repo — 7 de 24 paquetes).

**Comparables SDK técnicos:** AsterNET (159⭐ dormant), Sufficit.Asterisk (library modular), Twilio/Vonage .NET (REST clients) — todos pasivos. El nombre "SDK" fuerza comparación apples-to-oranges donde `Asterisk.Sdk` parece overweight.

**Comparable correcto:** `asterisk-java` (que se describe honestamente como "library") + frameworks .NET como ASP.NET Core. Microsoft Agent Framework 1.0 (abril 2026) legitimó "framework" en .NET.

Sin rebrand, la narrativa pública permanece en tensión con el código real.

## Decision

**Rebrandear narrativa pública de "Asterisk.Sdk" a "Asterisk Runtime for .NET"** manteniendo los **package IDs estables** (`Asterisk.Sdk.*` no cambian) por continuidad SEO + PublicAPI baseline + PackageValidation.

**Cambios concretos (ver checklist de 10 puntos en PSD §4.4):**
1. README line 3 + hero section.
2. CLAUDE.md §Project Overview.
3. `Directory.Build.props` — `<Product>` + `<PackageTags>`.
4. Cada `src/*/Asterisk.Sdk.*.csproj` — `<Description>` individual (24 files).
5. `docs/README-commercial.md` + `docs/README-technical.md`.
6. `Examples/**/README.md` (22 apps).
7. NuGet.org descriptions (automatic via csproj re-pack).
8. GitHub repo topics (quitar `sdk`, agregar `framework`/`runtime`/`native-aot-framework`).
9. Documentation site (si/cuando exista).
10. Cross-repo alignment: Pro README + Platform README referencian el nuevo framing.

**Ejecución:** single PR cross-repo en Mes 5 de roadmap (v2.0.0-preview1 — Septiembre 2026). Todos los cambios coordinados.

## Consequences

**Positivas:**
- Narrativa honesta: lo que se vende matches con lo que se envía.
- Posicionamiento en categoría vacía — no hay competidor directo en .NET runtime para Asterisk.
- Continuidad técnica: package IDs estables, consumers no se rompen.
- Upsell ladder clean: "Runtime (MIT) → Enterprise Runtime (Pro) → Contact Center (Platform SaaS)".

**Negativas:**
- Ventana de transición (v1.x → v2.0) puede confundir durante 2-3 releases.
- SEO: buscadores "Asterisk SDK .NET" pueden perder ranking temporal — mitigado por keywords dual (README menciona ambos términos en transición).
- GitHub stars de comunidad SDK-oriented pueden asumir cambio drástico.

## Alternatives considered

- **Status quo (mantener "SDK"):** rechazado — tensión narrativa-código no resuelve; compite en commodity slot con AsterNET/Sufficit.
- **"Asterisk.NET Framework":** rechazado — conflicto con .NET Framework legacy (el runtime pre-.NET Core). Confusión SEO garantizada.
- **Rename package IDs (`Asterisk.Runtime.*`):** rechazado — breaking catastrófico para consumers existentes; SEO/stars/PublicApi baseline pierdidos.
- **No cambiar nada + emitir ADR de intent:** rechazado — decisiones dependientes (stewardship pledge, tier identity) requieren baseline narrativo explícito.

## References

- PSD §4 (Identity + naming per tier) — Asterisk.Platform/docs/specs/2026-04-19-product-strategy-v2.md
- Competitive landscape research (2026-04-19) — conversación fuente
- Microsoft Agent Framework 1.0 announcement (abril 2026)
