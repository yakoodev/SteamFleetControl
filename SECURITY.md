# Security Policy

## Supported Versions
Security fixes are prioritized for the latest `main` branch and the latest release tag.

| Version | Supported |
| --- | --- |
| latest (`main`) | Yes |
| older branches | Best effort |

## Reporting a Vulnerability
Please do **not** open public issues for vulnerabilities.

Use one of the following:
- GitHub private advisory form: <https://github.com/yakoodev/SteamFleetControl/security/advisories/new>
- If advisories are unavailable, open a private maintainer contact request and include `SECURITY` in the title.

## What to Include
- Affected component/endpoint.
- Reproduction steps or proof of concept.
- Impact assessment (data exposure, account takeover, RCE, etc.).
- Suggested mitigation (if available).

## Response Targets
- Initial triage: within 72 hours.
- Status update: within 7 days.
- Fix timeline: depends on severity and complexity.

## Security Best Practices for Contributors
- Never commit real secrets, Steam credentials, session payloads, or mail codes.
- Sanitize logs and screenshots.
- Prefer secure defaults and explicit error handling.
