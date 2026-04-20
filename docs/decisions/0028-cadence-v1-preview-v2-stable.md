# ADR-0028: Cadence commitment — v1.x preview series, v2.0 stable Q4 2026

- **Status:** Accepted (2026-04-20 — executed as part of SDK v1.15.0 roadmap R1-F)
- **Date:** 2026-04-20
- **Deciders:** Harold Reina
- **Related:**
  - PSD v2: `Asterisk.Platform/docs/specs/2026-04-19-product-strategy-v2.md` §5.1, §9
  - ADR-0026 (product identity)
  - ADR-0023 (PublicApi tracker adoption)
  - ADR-0027 (stewardship pledge) — cadence commitment is the operational companion to the stewardship pledge
  - ADR-0029 (resilience primitives MIT) — first concrete Commercial→MIT movement under this cadence

## Context

Cadence actual del repo (verificado 2026-04-20):
- **11 minors en 6 meses** (v1.5.3 → v1.13.0 entre oct 2025 - abr 2026).
- **Extrapolado: 22 minors/año.**
- 30 commits en 2 días (v1.12.0 → v1.13.0, 2026-04-18 → 2026-04-20).
- `CompatibilitySuppressions.xml` en core + Ari → binary breaks aceptados bajo `PackageValidationBaselineVersion=1.5.3`.

Comparación con industry benchmarks:

| Categoría | Cadencia típica | Actual SDK |
|---|---|---|
| **SDKs** (AWS/Azure/Twilio/Stripe) | 2-4 minor/yr + LTS + major 3-5 yrs | **5-10× excesivo** |
| **Frameworks** (ASP.NET Core/EF Core/NestJS) | 4-12 minor/yr + major anual | **~2× excesivo** |
| **Runtime in active dev (0.x preview)** | Sin expectativa fija | Encaja, pero v1.x naming engaña |

**Mismatch documentado:** SDK ships SDK-grade hygiene (`PackageValidation` + `PublicApiAnalyzers` + `BannedSymbols`) mientras corre a startup-preview velocity. Esta contradicción erosiona trust de consumers enterprise (esperan cadence SDK-like) y simultáneamente invalida la disciplina de hygiene (si todo es preview, por qué el lock-down?).

Research OSS maintainer: 60% considera abandonar proyectos; 44% cita burnout. 30-commits-in-2-days es señal temprana.

## Decision

**Declarar públicamente: v1.x series = preview. v2.0.0 será primer stable release (Q4 2026, noviembre).**

**Cadence commitments post-v2.0:**

1. **Major annual** sincronizado con .NET release cycle (noviembre): v2.0 (Nov 2026), v3.0 (Nov 2027), v4.0 (Nov 2028).
2. **Minor cap: 8-12/año** (framework range — no 22). Minor puede carry breaking changes + migration guide obligatoria.
3. **Patch cadence:** mensual o por needs, no-breaking por definición.
4. **LTS line:** v2.0 soporte garantizado 12 meses post-v3.0 → hasta Nov 2028.

**v1.x preview window:**

- Hasta v2.0 release (Nov 2026), v1.x queda marcado como "preview series" en README.
- Releases v1.14+ pueden seguir con cadence actual (features que lleguen antes de v2.0 estabilización).
- `CompatibilitySuppressions.xml` permitido en v1.x; cleaned antes de v2.0 GA.
- Consumers enterprise deben preferir esperar a v2.0 o aceptar preview semantics.

**Compat matrix published en cada release:**

- Tabla Sdk_version × Pro_version × Platform_version compatibility.
- LTS series tienen skip-ship windows explícitas.

## Consequences

**Positivas:**
- Narrativa honesta (code + naming alineados): "framework in active development, v2.0 será stable".
- Reduce cognitive load en consumers — saben explícitamente que v1.x es preview.
- Reduce pressure burnout en maintainer — cadence post-v2.0 es 2× más lenta.
- LTS declared permite enterprise adoption con skip-ship confidence.
- Annual major sincroniza con .NET cycle — consumers pueden planear upgrade windows.

**Negativas:**
- Perception cost: algunos prospects pueden desestimar "v1.x preview" como inmaduro.
- v1.x features shipped ahora pueden tener que ser re-diseñadas para v2.0 stability.
- Commitment a 12-meses LTS window requiere ownership recursos (backport patches a v2.0 LTS mientras v3.0 evoluciona).

**Mitigación:**
- README clarifica: "preview series = actively maintained, feature-complete, but API surface still evolving toward v2.0 stability. Production use is supported with the understanding that breaking changes may occur in v1.x minors."
- v2.0 LTS scope es minimal: bug fixes + security patches. No new features → limita ongoing effort.

## Alternatives considered

- **Continuar cadence actual sin declararlo:** rechazado — narrativa opaca genera distrust + consumers confused + burnout cumulative.
- **Demote a 0.x inmediatamente:** rechazado — breaking para consumers existentes que consumen v1.x stable-labeled. SEO/version history perdido.
- **v2.0 Q3 2026 (septiembre):** considerado pero rechazado — 5 meses scope (4 primitives migrations + rebrand + benchmark suite + migration guide cross-repo) es ajustado. Q4 da 2 meses slack.
- **Annual major sin LTS:** rechazado — enterprise buyers requieren LTS para commitment.
- **LTS 18 o 24 meses:** rechazado — overreach para team size actual.

## References

- PSD §5.1 cadence decision
- SDK cadence sustainability research (2026-04-19) — conversación fuente
- .NET release cadence (annual November majors, LTS even-numbered versions)
- byteiota / dosu.dev OSS maintainer burnout data
