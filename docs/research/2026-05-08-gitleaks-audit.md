# gitleaks audit — 2026-05-08

**Tool:** `gitleaks detect` (apt-installed, Debian package)
**Scope:** full git history, 861 commits scanned
**Result:** 1 finding, all benign (self-signed demo cert, history-only)
**Verdict:** ✅ **clean** — safe for going public

## Findings

| # | Rule | File | Line | Commit | Verdict |
|---|---|---|---|---|---|
| 1 | `private-key` | `docker/certs/asterisk.key` | 1 | `3e677974` | False positive (self-signed demo cert; removed from HEAD in `1fff8e82`) |

## Detail

**Finding 1 — `docker/certs/asterisk.key`**

- Introduced in commit `3e677974` ("feat(docker): add self-signed TLS certificate for WSS WebRTC")
- Removed in commit `1fff8e82` ("refactor: move PbxAdmin to standalone repository")
- The file was a self-signed RSA private key intended for the local Docker test environment supporting WSS WebRTC. It was never used to encrypt real production traffic and has been moved out of this repository.
- Risk if seen post-public-flip: zero — even in worst case where an attacker obtains the historical key and stands up a fake WSS endpoint with it, no production system relies on this key.

## Action plan

- **No history rewrite needed** — the finding is benign and history-only.
- **No rotation needed** — the key was never used in production.
- For future commits: avoid committing private keys, even self-signed. If a Docker test setup needs TLS, generate the certs at container build time or use `mkcert` in dev setup scripts.

## Re-scan command

```sh
gitleaks detect --source . --no-banner
```

Expected: zero new findings (only #1 in history).

## Cross-references

- Audit context: SDK auto-memory `project_2026_05_08_licensing_audit.md`
- Trigger source: Platform ADR-0018, Web ADR-0007 (trigger 1)
