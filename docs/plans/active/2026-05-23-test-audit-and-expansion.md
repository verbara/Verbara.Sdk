# Auditoría profunda + expansión de tests funcionales e integration

## Execution status (live)

- ✅ **Fase 0 — Foundation hardening** — shipped 2026-05-23 in PR #27 (`2f6bab13`). functional-tests required on `merge_group`, Asterisk 22+23 matrix wired, `tools/audit-test-asserts.sh` enforces assertion presence.
- ✅ **Fase 1 — Claim guards (all 4 in one PR)** — shipped 2026-05-23 in PR #28 (`b8758d57`). OTEL counts pinned (9/15/11/60), `Tests/Verbara.Sdk.DocSnippets.Tests/` compiles 6 doc snippets via Roslyn (26 marked `<!-- skip-doc-snippet -->`), AOT canary + actual publish+run. **Two corrections (verified 2026-08-02) — see re-scope note below:** the AOT canary shipped at `tools/AotCanary/` (22 project references, well past the "≥3 packages" target) rather than at the planned `Tests/Verbara.Sdk.AotConsumer.SmokeTest/`; and `baseline.json` was **never** checked in — `perf-regression.yml` is weekly and observational **by design** (its own header records the gate as a follow-up), so the throughput claim has no guard. Turn-detection accuracy fixtures deferred (no audio data) — still true.
- 🚧 **Fase 2 — Coverage breadth** — PR 1/10 shipped 2026-05-23 in PR #29 (`02d0e88c`): Data.Npgsql +7 tests (transaction, ordering, cancellation, DBNull, Guid/byte[]/DateTimeOffset roundtrip) + new `Tests/Verbara.Sdk.Push.Webhooks.IntegrationTests/` with `Microsoft.AspNetCore.TestHost.TestServer` fixture (6 end-to-end). **The "PRs 2–10" figure was wrong** — see the re-scope note below; three of the four listed items turned out to be already shipped.
- ⏸ **Fase 3 — Longevity** — not started. Now `longevity-soak-and-chaos` (openspec) + Sdk/ADR-0043.
- ⏸ **Fase 4 — Live cloud / mutation / multi-platform** — deferred per plan, optional. Unchanged.

<!-- doctor:stale-exempt: narrative-only since the 2026-08-02 re-scope; the remainder is tracked as
     the three openspec changes listed below, which are the backlog. This file carries the 5-phase
     rationale and the shipped record — it is not a work queue and will not close on its own. -->

> **Re-scope (2026-08-02) — the park is lifted and the remainder now lives in OpenSpec.** The
> 2026-07-13 disposition below parked this plan until `ci-pipeline-slimming` (Sdk/ADR-0038)
> archived. It archived on 2026-07-15, so the remainder was re-audited against the tree and split
> into three OpenSpec changes. **Open changes are the backlog from here — this file is narrative
> only and adds no work of its own.**
>
> | Workstream | OpenSpec change | ADR |
> |---|---|---|
> | Unguarded public claims (throughput gate + `baseline.json`, turn-detection accuracy) | `enforce-unguarded-public-claims` | Sdk/ADR-0042 |
> | Per-provider wire-format fidelity (WireMock substrate) | `wiremock-http-provider-substrate` | Sdk/ADR-0041 |
> | Fase 3 longevity (soak, extended chaos, extended hot paths) | `longevity-soak-and-chaos` | Sdk/ADR-0043 |
>
> **The remainder was much smaller than this file claimed.** Verified already shipped, and therefore
> dropped from scope: **AudioSocket** round-trip (`Integration/AudioSocketRoundTripTests.cs` — echo +
> concurrent sessions) and **OpenAiRealtime** (`Bridge/` 6 tests against a fake server, plus
> `FunctionCalling/` dispatch incl. throw and unknown-function paths). **Cluster.Postgres** is
> largely done too — 10 tests already carrying `[Trait("Category","Integration")]`, concurrent
> contention included (`ShouldElectExactlyOneWinner_WhenManyOwnersContendConcurrently`); only two
> edge cases remain (lock release on connection drop, advisory-lock cleanup after a Postgres
> restart). Scoping "PRs 2–10" off this file without re-reading the tree would have re-done work.
>
> **ADR number correction:** this plan reserved `0038` for the WireMock ADR. `ci-pipeline-slimming`
> took 0038 in July. The WireMock ADR is **Sdk/ADR-0041**.
>
> **Defects surfaced by the re-scope audit** (each now owned by one of the three changes):
> every benchmark step in `perf-regression.yml` ends in `|| true`, so the workflow reports green
> even when a benchmark fails to run at all; `README.md:105` publishes a throughput figure for
> `ChannelManager.GetById`, **a method that does not exist** (the real API is `GetByUniqueId` /
> `GetByName`, which is what `ChannelManagerBenchmark` actually measures); `docker/functional/sipp-scenarios/`
> holds only a `.gitkeep`, so the soak's "100 calls/min steady-state" driver does not exist;
> `ToxiproxyFactAttribute` is applied to zero tests (its probe hard-codes a URL and ignores
> `TOXIPROXY_API_URL`); several chaos assertions accept 4 of the 6 `AmiConnectionState` values and
> are close to vacuous; and the shipped turn-detection model resource is `smart-turn-v3.2-cpu.onnx`
> while the package `<Description>` says v3 and `README.md` links the v3 model card under a v3.2
> label.
>
> **Docs bug found while reproducing this repo's own instructions:** `CLAUDE.md` documents the unit
> filter as `Category!=Functional&Category!=Integration`, but `ci.yml` uses four exclusions —
> `Category!=Functional&Category!=Integration&Category!=Realtime&Category!=Spike`. Running the
> documented command locally pulls the Docker-dependent `Realtime` tests into the unit lane; that
> is what produced 12 Testcontainers failures on 2026-08-02 (the same 6 tests pass 6/6 in isolation).

> **Disposition (2026-07-13 stale-plans review) — superseded by the re-scope note above, kept for
> history:** the shipped halves (Fase 0, Fase 1 ×3 guards,
> Fase 2 PR 1/10) are recorded above. The remainder (perf regression gate + `baseline.json` —
> which, per repo verification, was NOT checked in despite the Fase 1 line below; turn-detection
> fixtures; Fase 2 PRs 2–10; Fase 3 longevity) is NOT resumed from this plan: it will be
> re-scoped as an OpenSpec change AFTER `ci-pipeline-slimming` (Sdk/ADR-0038) archives, since
> that change renegotiates the CI-cost premises Fase 0 set (functional matrix on both events).
> This plan stays in `active/` deliberately — the inline `doctor:stale-exempt` marker above
> records the parked state (`/xr:doctor` reports it as INFO with that reason instead of a
> stale-plan WARN) until the re-scope happens. Also reconcile the Lmnt flake noted below before
> any Fase 2 resumption.

**Closure memo for the 5-PR day:** [[project_2026_05_23_test_audit_phases_0_1_2]] in memory.

**Discovered flakes — RESOLVED 2026-08-02, no longer block resumption.**
`LmntSpeechSynthesizerWsTests.SynthesizeAsync_ShouldSendTextMessage_WithCorrectText` flaked in the
first merge_group run of PR #29, and `DeepgramSpeechSynthesizerTests.SynthesizeAsync_ShouldSendRequestToCorrectPath`
flaked under full-solution load. Both were recorded as fake-server synchronization races — an
assertion reaching the capture state before the server wrote it. **That diagnosis was wrong.** The
real cause was an address-family ambiguity: `localhost` resolves `::1` first, `WebSocketTestServer`
binds IPv4 only and so does not own `::1` on its port, and an `HttpListener` on
`http://localhost:{port}/` therefore binds the same port number without conflict — so the client
reached the wrong server. Fixed by dialling the `127.0.0.1` literal at every fake-server seam, with
a Governance guard preventing reintroduction (Sdk/ADR-0044, openspec change
`deflake-loopback-address-ambiguity`). The lesson generalizes: a test that only fails under parallel
load may be a **resource-identity collision**, not a timing race — check identity before widening a
timeout.

**New convention shipped alongside:** ADR-0037 (cross-repo ADR reference). All future "ADR-0022 Phase D" style references must use `Platform/ADR-NNNN` or `Pro/ADR-NNNN` prefix to disambiguate from this repo's local 0022 (Activity cancellation).

---

## Context

Tras la auditoría de Phase 1 sobre los 32 test projects (~2,924 unit + 195 functional + 70 integration), surgen problemas estructurales que **no se arreglan añadiendo más tests del mismo tipo:**

1. **El gate de funcional no aplica al merge.** La suite Functional+Integration corre en cada PR (verificado verde hoy en runs `26330363340`, `26330192143`, `26329351571`) pero el `functional-tests` job es `if: github.event_name == 'pull_request'` y NO está en `required_status_checks`. Una regresión que solo aparezca en `merge_group` puede landear.
2. **Asterisk 22+23 matrix existe en infra pero CI lo ignora.** `docker/docker-compose.test-23.yml` está checked-in desde 2026-04-20 — ningún workflow lo usa. CI prueba solo Asterisk 22.
3. **Claims del README no enforced por tests.** "Native AOT ready", "1.53M events/sec", "60 const strings pinned", "docs examples runnable", "94.3% turn-detection accuracy" son afirmaciones del frontend del producto. La auditoría docs del PR #26 mostró que la doc deriva sin sistema de detección. Mismo riesgo aplica a claims técnicos — y ya en este chat tropezamos con "12 meters" vs "15 meters".
4. **Coverage por package desigual:** 8 paquetes VoiceAi (STT 7 providers + TTS 6 providers + OpenAiRealtime + TurnDetection + AudioSocket) tienen **0 integration tests** — todo HttpClient mockeado. Push.Webhooks sin fixture HTTP. Cluster.Postgres (shipped v2.2.1) con 13 tests, ninguno multi-conexión. Data.Npgsql con 9 tests cubriendo surface incompleta.
5. **Longevidad ZERO.** No hay soak tests, AOT-publish smoke (el workflow `aot-validate.yml` valida trim warnings pero no publica + ejecuta), ni perf regression gate. Para un SDK telefónico 24/7, FD leaks y memory creep son catastróficos en producción y CI no los detecta.

Este plan ataca lo estructural primero (gates + claim guards + matrix), luego rellena breadth con metodología correcta, y al final añade longevity. Sin atajos: cada fase paga su prerequisito.

## Deep analysis — menú completo evaluado

24 vectores evaluados antes de elegir la senda. Lo descartado (con razón):

- **Live cloud APIs (STT/TTS reales):** Costo recurrente $$$, flake del proveedor, secreto-management pesado. ROI relativo bajo cuando los unit tests + WireMock-con-recordings dan 90% del valor sin costos. → Difiere a Fase 4.
- **Mutation testing (Stryker.NET):** Stryker ya es dev-dep, pero el mutation score sin cobertura sólida es ruido. Su valor llega cuando Fases 0-3 ya cierran los huecos obvios. → Fase 4.
- **Multi-platform CI (Windows + macOS):** El 95% de telephony deployments son Linux. ubuntu-latest cubre eso. Es desvío de foco hasta que llegue una demanda externa concreta. → No-scope.
- **Property-based testing (FsCheck):** Útil para los parsers pero menor leverage que los gaps de breadth. → No-scope esta vuelta.
- **Contract tests (Pact-style cross-version):** El matrix Asterisk 22+23 da el 80% del valor sin la complejidad de Pact. → Reemplazado por Fase 0 matrix.
- **Fuzz testing AFL-style:** Útil para seguridad pero ortogonal al producto. → No-scope.

El menú original de 4 opciones era reduccionista — la senda correcta es **secuencial multi-fase**, no "pick one".

## Recommended approach

### Fase 0 — Foundation hardening (~2-3 días)

Sin atajos: cerrar el gate y activar el matrix antes de añadir nada.

1. **`functional-tests` job → required.** Mover el job fuera del `if: pull_request` para que también corra en `merge_group`. Añadir a branch protection (`required_status_checks.contexts`). Trade-off explícito: ~19 min más por entry de queue — aceptable porque la queue ya tiene `min_entries_to_merge_wait_minutes: 5`, así que el bottleneck no se mueve mucho.
2. **Matrix Asterisk 22 + 23 en el job functional-tests.** `strategy.matrix.asterisk: [22, 23]`. Por defecto el compose corre 22; añadir env `ASTERISK_VERSION` que conmuta entre `docker-compose.test.yml` y `docker-compose.test-23.yml`. Las fixtures ya leen las env vars (`ASTERISK_AMI_PORT`, etc.) — solo hace falta wiring de CI.
3. **Audit semántico de los 195 funcionales:** crear un script `tools/audit-test-asserts.sh` que para cada `[Fact]` en FunctionalTests verifique presencia de ≥1 `.Should()` o `Assert.` Y ≥1 `await` (no es smoke-no-await). Generar reporte; arreglar los que aparezcan vacíos.

**Cierre de fase:** branch protection exige funcional+integration sobre Asterisk 22 Y 23, ambos verdes en al menos 5 PRs consecutivos.

### Fase 1 — Claim guards (~1 semana)

Cada claim del README → test que lo enforce. Si el claim no se puede testear, sale del README.

| Claim | Guard a crear |
|---|---|
| "Native AOT ready" en `README.md` | Extender `.github/workflows/aot-validate.yml`: `dotnet publish -c Release -r linux-x64 -p:PublishAot=true` un proyecto sandbox `Tests/Verbara.Sdk.AotConsumer.SmokeTest/` que use ≥3 paquetes (Hosting, Ami, VoiceAi); ejecutar binario; assertar exit 0 + log con cierta cadena |
| "1.53M events/sec AMI parse + dispatch" en README | Workflow `perf-regression.yml`: corre los 5 benchmarks listados en README contra baseline JSON checked in (`Tests/Verbara.Sdk.Benchmarks/baseline.json`); PR falla si regresión > 10% en ratio mean. Update baseline manual via labeled PR |
| "60 const strings + 9 ActivitySources + 15 Meters + 11 HealthChecks" | Test nuevo en `Verbara.Sdk.OpenTelemetry.Tests/`: `VerbaraSemanticConventions.GetAll().Count.Should().Be(60)` + asserts por categoría; `VerbaraTelemetry.ActivitySourceNames.Should().HaveCount(9)`; idem MeterNames(15) y HealthCheck registry(11) |
| "Docs examples runnable" | Nuevo proyecto `Tests/Verbara.Sdk.DocSnippets.Tests/`: hook MSBuild que extrae bloques \`\`\`csharp de `README.md`, `docs/README-technical.md`, `docs/guides/*.md`; genera `*.g.cs` envuelto en `class Snippet_Filename_LineNN { void M() { … } }`; falla la build si no compila. Esto habría atrapado todo el drift `AddAsterisk` → `AddVerbara` que arreglamos hoy |
| "94.3% English turn-detection accuracy" | Carpeta `Tests/fixtures/audio/turn-boundaries/` con ≥20 muestras WAV etiquetadas (10 turn-end positivas + 10 turn-mid negativas); test en `VoiceAi.TurnDetection.Tests` ejecuta el ONNX y assertea precision ≥ 0.85 / recall ≥ 0.85 |

**Cierre:** todo claim verificable está enforced; PR que rompe un claim falla CI antes del merge.

### Fase 2 — Coverage breadth con metodología correcta (~2 semanas)

Metodología fija: **WireMock.NET para HTTP providers**, **TestServer para webhooks**, **Testcontainers para state stores**. **No** cloud real (esa es Fase 4).

| Package | Gap actual | Solución |
|---|---|---|
| `VoiceAi.Stt` (7 providers) | Solo HttpClient mocked en unit | WireMock.NET fixture **por provider** con recordings reales (capturas .json del response real del API checked in en `Tests/.../recordings/`); cada test arma WireMock con la captura, ejecuta el provider, asserts wire format + transcript final + métricas `stt.recognition.*_ms` |
| `VoiceAi.Tts` (6 providers) | Idem | Idem — WireMock per provider con captura de stream binario |
| `VoiceAi.OpenAiRealtime` | Unit only | Aprovechar `WebSocketTestServer` ya existente (visto en memoria SDK Release Status); test de conversation roundtrip con scripted assistant responses + assert function-call dispatch |
| `VoiceAi.AudioSocket` | Unit only | Functional test: real AudioSocket connection desde un test client → handler → assert audio bytes round-trip via Testcontainers o socket loopback |
| `VoiceAi.TurnDetection` | Solo unit contra ONNX | Cubierto por Fase 1 (audio fixtures) — pero además: test contra audio sintético (noise, silence, mixed) para edge cases |
| `Push.Webhooks` | Unit only (HMAC + circuit breaker aislados) | Nueva fixture con `Microsoft.AspNetCore.TestHost.TestServer` que monta un endpoint subscriber real; tests asseran HMAC roundtrip + retry behavior + circuit breaker bajo respuestas lentas (Toxiproxy ya en stack) |
| `Cluster.Postgres` | 13 tests, single-connection | Tests de contención multi-connection: 2+ `NpgsqlConnection` concurrentes peleando por el mismo lock; assert exclusividad; lock release on connection drop (drop deliberado); lock renewal; advisory lock cleanup tras restart Postgres (kill+restart Testcontainer) |
| `Data.Npgsql` | 9 tests, surface incompleta | Cobertura completa por método de `NpgsqlExecutor`: happy path + cancellation + DBNull binding + transaction rollback + parameterized binding edge cases + nullable returns + `ExecuteScalarAsync<T>` para T=`long?`, `string?`, `Guid` |

**Cierre:** matriz de coverage (a generar en `docs/research/2026-XX-XX-test-coverage-matrix.md`) muestra Functional o Integration ≥ 1 por package shippable. Ningún paquete crítico solo unit-mocked.

### Fase 3 — Longevidad y chaos avanzado (~1-2 semanas)

Lo que separa un SDK "passes CI" de uno "production-grade 24/7":

1. **Soak test (24h)** — workflow `soak.yml` cron `0 2 * * 0` (domingo 02:00 UTC weekly):
   - Levanta stack completo (Asterisk 22 + 23 paralelos)
   - SIPp drives 100 calls/min steady-state
   - Cada 5 min: `dotnet-counters` snapshot + `lsof -p $pid | wc -l`
   - Después de 24h: assert heap stable post-1h (delta < 50 MB), FD count stable (delta < 10), 0 unhandled exceptions
   - Si falla: GitHub Issue auto-creado con el snapshot trail
2. **Chaos suite extendida** (aprovecha Toxiproxy ya en stack):
   - RST midstream durante AMI command response
   - Half-open socket (server cerró silenciosamente, cliente no sabe)
   - `asterisk -rx "core reload"` mientras hay 50 channels activos
   - Postgres failover mid-Sessions transaction (Testcontainer kill + restart)
   - NATS broker disconnect mid-publish
3. **Perf regression CI** extendido — más hot paths: ARI deserialize Channel, Session correlate by LinkedId, ChannelManager.GetById, observer dispatch

**Cierre:** 4 soak weekly consecutivos verdes antes de declarar fase cerrada.

### Fase 4 (opcional, deferred) — Live cloud + mutation + multi-platform

Solo después de Fases 0-3 estables: live cloud smoke (gated por secret presence), Stryker.NET CI con diff por PR, Windows+macOS matrix.

## Por qué este orden (rationale sin atajos)

- **Fase 0 primero:** añadir tests con un gate roto = falso positivo de calidad. Y matrix 22+23 sin cost es dinero gratis (la infra ya está, solo wiring).
- **Fase 1 antes que Fase 2:** los claims son la promesa pública del producto; el costo de un claim falso (cliente confía, falla en prod) es mayor que el costo de un gap interno conocido.
- **Fase 2 antes que Fase 3:** soak con coverage agujereado no detecta el bug del provider que no estás probando.
- **Fase 4 al final:** alto costo, ROI marginal vs. las primeras 3 fases.

## Critical files / paths

**Existentes a reusar / extender:**
- `Tests/Verbara.Sdk.FunctionalTests/Infrastructure/Fixtures/` — `AsteriskContainerFixture`, `ToxiproxyFixture`, `RealtimeFixture`, `FunctionalTestBase`
- `Tests/Verbara.Sdk.TestInfrastructure/` — base classes compartidas
- `Tests/Verbara.Sdk.Benchmarks/` — base de perf regression
- `docker/docker-compose.test.yml` + `docker-compose.test-23.yml` — multi-version ya armado (Fase 0)
- `.github/workflows/ci.yml` — extender el `functional-tests` job con matrix + remover `if: pull_request` (Fase 0)
- `.github/workflows/aot-validate.yml` — extender con publish + run smoke (Fase 1)

**Nuevos:**
- `tools/audit-test-asserts.sh` (Fase 0)
- `Tests/Verbara.Sdk.AotConsumer.SmokeTest/` (Fase 1)
- `Tests/Verbara.Sdk.DocSnippets.Tests/` (Fase 1)
- `Tests/Verbara.Sdk.Benchmarks/baseline.json` (Fase 1)
- `Tests/fixtures/audio/turn-boundaries/*.wav` (Fase 1)
- `Tests/Verbara.Sdk.VoiceAi.Stt.Tests/WireMock/recordings/` (Fase 2)
- `Tests/Verbara.Sdk.VoiceAi.Tts.Tests/WireMock/recordings/` (Fase 2)
- `Tests/Verbara.Sdk.Push.Webhooks.IntegrationTests/` (Fase 2)
- `Tests/Verbara.Sdk.Cluster.Postgres.IntegrationTests/` (Fase 2) — separar el archivo Integration del Unit
- `.github/workflows/perf-regression.yml` (Fase 1)
- `.github/workflows/soak.yml` (Fase 3)
- `docs/decisions/0038-wiremock-as-http-provider-test-substrate.md` (Fase 2 — ADR introduciendo WireMock.NET como dep convention)
- `docs/research/YYYY-MM-DD-test-coverage-matrix.md` (Fase 2)

## Verification

Por fase:
- **Fase 0:** `gh pr checks <PR#>` muestra `Functional Tests (asterisk-22)` + `Functional Tests (asterisk-23)` como required y green; branch protection lista los dos.
- **Fase 1:** `git revert` de un claim guard sin tocar el README hace CI rojo (test manual). Build falla si los conteos de OTEL conventions desalinean con README.
- **Fase 2:** matriz de coverage publicada; cada paquete shippable con celda Functional o Integration ≥ 1.
- **Fase 3:** 4 soak weekly verdes consecutivos; ninguna issue auto-creada por leak.
- **Fase 4:** opcional, no se valida en esta planificación.

## Out of scope

- Property-based testing (FsCheck)
- Fuzz testing (AFL-style)
- Contract testing (Pact)
- Multi-platform CI (Windows + macOS)
- Live cloud APIs como required (queda como Fase 4 opcional)
- Migración a xunit v4 — sigue gated por `project_xunit_migration_tracking`
