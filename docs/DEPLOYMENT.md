# Deployment Guide

## Requirements
- Docker + Docker Compose
- Open ports:
- `8080` for web
- `5432` for PostgreSQL (optional external access)
- `6379` for Redis (optional external access)

## Environment Variables
Use `.env` (see `.env.example`):
- `ADMIN_EMAIL`
- `ADMIN_PASSWORD`
- `SECRETS_MASTER_KEY_B64`
- `WORKER_COUNT`

## Run
```bash
docker compose up --build -d
```

## Verify
- UI: <http://localhost:8080>
- Swagger: <http://localhost:8080/swagger>
- Hangfire: <http://localhost:8080/hangfire>
- Health: <http://localhost:8080/health>

## Stop
```bash
docker compose down
```

## Full reset (drop DB/cache volumes)
```bash
docker compose down -v
docker compose up --build -d
```

## Local Build/Test (without Docker)
```bash
dotnet build SteamFleet.slnx
dotnet test SteamFleet.slnx
```

## Production Notes
- Replace default admin credentials before public exposure.
- Generate a strong `SECRETS_MASTER_KEY_B64` and store it in a secure secret manager.
- Put reverse proxy/TLS in front of the service.
- Restrict network access to PostgreSQL and Redis.
- Enable centralized log collection and backups.
