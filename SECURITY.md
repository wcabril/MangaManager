# Security Policy

## Supported Versions

Only the latest release is actively maintained.

| Version | Supported |
|---|---|
| Latest (v1.8.x) | ✅ |
| Older versions | ❌ |

## Reporting a Vulnerability

If you discover a security vulnerability, **please do not open a public issue**.

Instead, report it privately:

- **LinkedIn:** [linkedin.com/in/wcabril](https://linkedin.com/in/wcabril)

Please include:
- A description of the vulnerability
- Steps to reproduce it
- Potential impact

You can expect a response within **7 days**. If confirmed, a fix will be released as soon as possible.

## Scope

This is a local desktop application that:
- Reads/writes files on your local machine
- Makes outbound HTTP requests to AniList, MangaDex and MangaUpdates APIs (read-only, no authentication)
- Runs `kcc_c2e` as a local subprocess

No user data is collected or transmitted beyond those public API calls.
