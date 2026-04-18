# Contributing to SteamFleetControl

Thanks for helping build SteamFleetControl.

## Before You Start
- Read [README.en.md](README.en.md) or [README.ru.md](README.ru.md).
- Check existing issues to avoid duplicates.
- For security-sensitive topics, use [SECURITY.md](SECURITY.md) instead of a public issue.

## Development Setup
```bash
docker compose up --build -d
dotnet build SteamFleet.slnx
dotnet test SteamFleet.slnx
```

## Branching and Commits
- Create a feature branch from `main`.
- Keep PRs focused and small when possible.
- Prefer clear commit messages.

Recommended commit style:
- `feat: add typed job form for ...`
- `fix: handle steam invite token fallback`
- `docs: update deployment guide`

## Pull Request Checklist
- [ ] Code builds successfully.
- [ ] Tests are added/updated where relevant.
- [ ] No real credentials, tokens, session payloads, or personal data are committed.
- [ ] Docs are updated for behavior/API changes.
- [ ] PR description explains what changed and why.

## Coding Guidelines
- Keep behavior explicit and observable (good errors, audit events, logs without secret leakage).
- For risky flows (Steam auth/session/password), prefer recoverable error states over crashes.
- Add tests for parser and protocol edge cases.

## Documentation Guidelines
- Update `README.ru.md` and `README.en.md` for user-visible changes.
- Keep API/ops details in `docs/` files, not only in PR text.

## Need Help?
- Open a discussion/issue with context, expected behavior, and logs (sanitized).
