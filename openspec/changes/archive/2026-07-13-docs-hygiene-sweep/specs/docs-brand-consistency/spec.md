## ADDED Requirements

### Requirement: Shipped-doc identifiers name only real public API symbols

Every code identifier in a shipped, living documentation file (package and example READMEs)
SHALL name a public API symbol that exists in the current codebase. No documentation SHALL
reference a pre-rebrand `Asterisk*` DI extension method, options type, or Live-API type
(`AddAsterisk*`, `AsteriskOptions`, `AsteriskServer`, `AsteriskServerPool`) that no longer exists
post-2.0.0. Living docs are the set the `/xr:doctor` rebrand-residue sweep scans: tracked `*.md`
excluding `docs/decisions/`, `docs/specs/`, `docs/plans/{completed,archived}/`, `docs/research/`,
`openspec/changes/archive/`, and dated `CHANGELOG` history (period-correct records stay verbatim).

#### Scenario: DI extension-method name in a README resolves to a real method

- **WHEN** a living README shows a `services.Add…()` / `builder.Services.Add…()` call
- **THEN** the method named SHALL exist as a `public static` extension in
  `src/Verbara.Sdk.Hosting/ServiceCollectionExtensions.cs` (e.g. `AddVerbara`,
  `AddVerbaraSessions`, `AddVerbaraSessionsBuilder`, `AddVerbaraMultiServer`, `AddVerbaraPush`,
  `AddVerbaraPushWebhooks`, `AddVerbaraPushAspNetCore`, `AddVerbaraResilience`)
- **AND** no `AddAsterisk*` form SHALL remain in any living doc

#### Scenario: Bare type reference in a README resolves to a real type

- **WHEN** a living README names an options or Live-API type (e.g. `VerbaraOptions`,
  `VerbaraServer`, `VerbaraServerPool`)
- **THEN** that type SHALL exist under `src/Verbara.Sdk.Hosting/` or
  `src/Verbara.Sdk.Live/Server/`
- **AND** no bare `AsteriskOptions` / `AsteriskServer` / `AsteriskServerPool` reference SHALL
  remain in any living doc

### Requirement: Fictional API snippets are rewritten to the canonical shape, never token-swapped

A documentation snippet that describes an API which never existed SHALL be rewritten to compile
against the real public surface, using an authoritative in-repo example as the reference. A
mechanical token-swap SHALL NOT be applied when no post-rebrand symbol of the same shape exists.

#### Scenario: The multi-server snippet is rewritten against the canonical example

- **WHEN** `src/Verbara.Sdk.Hosting/README.md` documents multi-server / federation registration
- **THEN** the snippet SHALL use DI-time `AddVerbaraMultiServer()` plus runtime
  `VerbaraServerPool.AddServerAsync(serverId, new AmiConnectionOptions { … })`, matching
  `Examples/MultiServerExample/`
- **AND** it SHALL NOT reference `AddAsteriskServerPool` or a non-existent `AddVerbaraServerPool`
  builder-callback overload

### Requirement: Runtime data values are exempt from the rebrand sweep

String literals that are runtime DATA — message-bus subject strings and configuration-section
keys — SHALL NOT be altered by the rebrand sweep, even when they contain the token `asterisk`,
because they are wire/binding contracts, not code identifiers.

#### Scenario: NATS subject strings are preserved

- **WHEN** `Examples/NatsBridgeExample/README.md` shows subjects like `asterisk.sdk.calls…`
- **THEN** those literals SHALL remain byte-for-byte unchanged

#### Scenario: The Asterisk config-section key is preserved

- **WHEN** `src/Verbara.Sdk.Hosting/README.md` shows an `appsettings.json` fragment with an
  `"Asterisk"` section
- **THEN** that key SHALL remain `"Asterisk"`, because
  `src/Verbara.Sdk.Hosting/ServiceCollectionExtensions.cs` binds `GetSection("Asterisk:Ami")`
  and renaming the key would break configuration binding

## Architectural Risk

**Level:** LOW. **Affected:** shipped package/example READMEs only — no `.cs`, no public API, no
package version. The only substantive risk is over-scrubbing a runtime data value (NATS subject,
`"Asterisk"` config key) or under-verifying a replacement symbol. **Mitigation:** every replacement
identifier is grep-verified against the real source before edit; the two known data-value classes
are enumerated as explicit preserve-scenarios above; the fictional snippet is rewritten from a
compiling in-repo example, not swapped.
