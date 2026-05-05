# Guides

Practical how-to guides for working with Verbara Sdk.

| Guide | Description |
|-------|-------------|
| [asterisk-version-compatibility.md](asterisk-version-compatibility.md) | AMI event coverage matrix across Asterisk 18-23, listing typed classes and fallback behavior per version. |
| [asterisk-version-matrix.md](asterisk-version-matrix.md) | Supported Asterisk versions (22 LTS primary, 23 Standard secondary), Docker test infrastructure, and known-divergent behavior. |
| [high-load-tuning.md](high-load-tuning.md) | Configuration guidance for high-load scenarios (1K-100K+ agents), including EventPump sizing and buffer capacity recommendations. |
| [log-analysis-prompt.md](log-analysis-prompt.md) | Ready-to-use LLM prompt for analyzing Verbara.Sdk structured log files with extract, classify, and diagnose phases. |
| [log-analysis-reference.md](log-analysis-reference.md) | Tag catalog for SDK structured logs -- lists every `[TAG]`, its domain, source classes, and emitted events. |
| [manual-asterisk-realtime-setup.md](manual-asterisk-realtime-setup.md) | How to run Asterisk in Realtime mode with PostgreSQL so external tooling can manage PJSIP endpoints, queues, and voicemail via SQL. |
| [session-store-backends.md](session-store-backends.md) | Choosing and configuring a session store backend (InMemory, Redis, or Postgres) for `Verbara.Sdk.Sessions`. |
| [troubleshooting.md](troubleshooting.md) | Common issues and fixes for AMI connections, ARI, AGI, and other SDK components. |
