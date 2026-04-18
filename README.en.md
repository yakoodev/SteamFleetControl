# SteamFleetControl

[Русский](README.ru.md) | [English](README.en.md)

## Общая сводка / Quick Summary
- RU: **SteamFleetControl** — self-hosted платформа для управления Steam-аккаунтами (web + worker + PostgreSQL + Redis) с упором на массовые операции и безопасность.
- EN: **SteamFleetControl** is a self-hosted Steam account platform (web + worker + PostgreSQL + Redis) focused on secure batch operations.

## About
SteamFleetControl is a .NET 8 modular monolith for operating Steam accounts at scale:
- account inventory and organization;
- live Steam authentication and session maintenance;
- batch job execution with per-account results;
- audit trail for operational and security actions;
- Docker-friendly split into web API/UI and background worker.

## Core Features
- ASP.NET Identity + RBAC (`SuperAdmin`, `Admin`, `Operator`, `Auditor`).
- Encrypted secret storage (AES-GCM, master key outside DB).
- Steam integration for auth/session/profile/privacy/avatar/password/deauthorize/friends/games.
- Account import/export, tags, folders, and family-group model.
- Hangfire-powered batch jobs (retry/backoff, dry-run, cancel, reports).
- Business-level audit events and security observability.

## Quick Start
```bash
docker compose up --build -d
```

After startup:
- UI: <http://localhost:8080>
- Swagger: <http://localhost:8080/swagger>
- Hangfire: <http://localhost:8080/hangfire>
- Health: <http://localhost:8080/health>

Default local admin:
- Email: `admin@local`
- Password: `Admin1234`

## Environment Variables
See [.env.example](.env.example):
- `ADMIN_EMAIL`
- `ADMIN_PASSWORD`
- `SECRETS_MASTER_KEY_B64`
- `WORKER_COUNT`

## Local Development
```bash
dotnet build SteamFleet.slnx
dotnet test SteamFleet.slnx
```

## Documentation
- Architecture: [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md)
- Deployment: [docs/DEPLOYMENT.md](docs/DEPLOYMENT.md)
- API overview: [docs/API.md](docs/API.md)
- Contribution guide: [CONTRIBUTING.md](CONTRIBUTING.md)
- Security policy: [SECURITY.md](SECURITY.md)
- Community conduct: [CODE_OF_CONDUCT.md](CODE_OF_CONDUCT.md)

## Contributing
1. Read [CONTRIBUTING.md](CONTRIBUTING.md).
2. Open an issue for bugs or ideas.
3. Submit a PR using the provided template.

## License
Distributed under the [MIT License](LICENSE).
