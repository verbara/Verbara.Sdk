# ADR-0027: Stewardship pledge — "Primitives stay MIT. Forever."

- **Status:** Proposed
- **Date:** 2026-04-20
- **Deciders:** Harold Reina
- **Related:**
  - PSD v2: `Asterisk.Platform/docs/specs/2026-04-19-product-strategy-v2.md` §3.3, §6.3
  - ADR-0026 (product identity)

## Context

El modelo open-core (Asterisk.Sdk MIT + Asterisk.Sdk.Pro commercial + Asterisk.Platform) depende de **trust** entre el repo open-source y sus consumers/contributors. Research de open-core companies (GitLab/HashiCorp/Elastic/MongoDB/Grafana/Redis/Confluent) muestra un patrón claro: **movimientos OSS→Commercial generan backlash + forks que capturan cloud providers**.

Evidencia concreta:
- **HashiCorp BSL (agosto 2023):** re-licensed Terraform/Vault/Consul/Nomad desde MPL-2.0 a BSL. Fork OpenTofu en 6 meses capturó AWS/Microsoft/Oracle.
- **Redis SSPL (marzo 2024):** re-licensed Redis desde BSD-3. Fork Valkey en 3 semanas capturó Linux Foundation + cloud providers. Redis reversed course (AGPLv3, mayo 2025).
- **Elastic SSPL (2021):** developer backlash + OSI pressure. Elastic reversed (AGPLv3 adicional, septiembre 2024).
- **MongoDB SSPL (2018):** AWS lanzó DocumentDB; relationship permanent damage.

El pattern de **open-core bait-and-switch** es el anti-pattern más costoso en la industria. Pre-emptive pledge es el single most trust-building move (evidencia Open Core Ventures "Open Charter").

Actualmente el repo carece de un compromiso explícito. Un ADR formal lo hace binding.

## Decision

**Publicar stewardship pledge público en README + ADR + stewardship page:**

> **Asterisk Runtime for .NET stewardship commitment:**
>
> 1. **Primitives stay MIT. Forever.** Features once published in `Asterisk.Sdk.*` MIT package **will never** move to a commercial tier. The API surface is permanent.
>
> 2. **Commercial → Open is permitted.** Features starting in `Asterisk.Sdk.Pro.*` commercial MAY be moved to MIT. Commercial layer may shrink; it will never eat into open core.
>
> 3. **Exceptions require explicit major version + 6-month deprecation notice.** In the unlikely event of reorganization, a major version bump is required, with migration path documented and 6-month deprecation window for consumers.
>
> 4. **No retroactive license changes.** Packages published under MIT will remain MIT-licensed in their existing versions. Future versions may change license only per rule #3.
>
> 5. **Fork-friendly.** The MIT license permits forks. We commit not to make fork-hostile moves (licensing tricks, trademark tricks, CLA changes that retroactively affect contributors).

**Publication channels:**
- README.md sección "Stewardship".
- `docs/stewardship.md` (nueva página dedicada).
- Cross-reference desde `Asterisk.Sdk.Pro/README.md` y `Asterisk.Platform/README.md`.
- Communicated en release notes de v2.0.0.

**Enforcement:**
- PR review check: cualquier movement de código MIT → commercial requires link a este ADR + mayor version bump.
- CI analyzer (futuro): static check que falle si types públicos MIT son removidos en un minor.

## Consequences

**Positivas:**
- Trust pre-emptive vs bait-and-switch anti-pattern.
- Atrae contributors (OSS community más willing to contribute cuando stewardship es explícito).
- Enterprise sales: clientes evalúan open-core risk; pledge reduce perceived lock-in risk.
- Evita forks reactivos (OpenTofu/Valkey-style) — si los primitives ya están MIT permanentemente, no hay razón para fork defensivo.
- Disciplina interna: forces thoughtful tier placement antes de ship (no "pongo en commercial para ver si genera revenue").

**Negativas:**
- Reduce future revenue flexibility — features que generen monetization inesperada en MIT no pueden migrarse atrás.
- Rigidez: si un primitive MIT resulta ser core a revenue Pro, el pledge prohíbe moverlo.
- Permite free-riding: cloud providers pueden empaquetar el MIT runtime + vender managed service sin retornar.

**Mitigación de negativos:**
- Careful tier placement upfront (regla de 5 gates de §3.1 PSD) reduce riesgo de misallocation.
- Commercial tier (Pro) tiene moat en multi-tenancy + compliance + licensing — no en primitives. Cloud providers pueden fork MIT, pero rebuildear el Pro tier es significativo.
- Platform tier (SaaS) es la defensa final para revenue.

## Alternatives considered

- **No pledge explícito:** rechazado — research muestra que ausencia de pledge genera uncertainty + increases fork risk.
- **Source Available (BSL/SSPL) desde v2.0:** rechazado — fast path a backlash evidenciado por HashiCorp/Redis/Elastic. Breaks existing MIT history.
- **Pledge limitado (solo "current features stay MIT, new features TBD"):** rechazado — menos trust-building; ambiguity en new features es exactamente lo que genera anxiety.
- **CLA changes que permitan re-licensing:** rechazado — anti-pattern documentado en OCV research.

## References

- Open Core Ventures: "Preventing the bait and switch by open core software companies" (2022)
- Open Core Ventures: "Open Charter gives open source users predictability" (2024)
- HashiCorp BSL retrospective (multiple sources)
- Redis SSPL U-turn analysis (Kuray Karaaslan, 2025)
- PSD §6.3 stewardship pledge rationale
