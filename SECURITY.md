# Security Policy

## Reporting a Vulnerability

If you discover a security vulnerability in Asterisk.Sdk, please report it responsibly.

### How to Report

Use GitHub's private vulnerability reporting:

1. Go to the [Security tab](../../security) of this repository
2. Click "Report a vulnerability"
3. Provide a detailed description of the vulnerability

### Response Timeline

- **Acknowledgment:** Within 48 hours
- **Initial assessment:** Within 5 business days
- **Fix timeline:** Depends on severity (critical: ASAP, high: 2 weeks, medium: next release)

### Coordinated Disclosure

- Do not publicly disclose the vulnerability until a fix is available
- We will credit reporters in the CHANGELOG (unless anonymity is requested)

### Scope

This policy covers all packages in the Asterisk.Sdk family:
- Asterisk.Sdk.* (MIT licensed, nuget.org)
- Security issues in dependencies should be reported to the respective maintainers

### Not in Scope

- Asterisk PBX itself (report to [Asterisk Security](https://www.asterisk.org/security))
- Configuration issues in user deployments
