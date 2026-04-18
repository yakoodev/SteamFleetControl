# SteamFleetControl

[Русский](README.ru.md) | [English](README.en.md)

## Общая сводка / Quick Summary
- RU: **SteamFleetControl** — self-hosted платформа для управления Steam-аккаунтами (web + worker + PostgreSQL + Redis) с упором на массовые операции и безопасность.
- EN: **SteamFleetControl** is a self-hosted Steam account platform (web + worker + PostgreSQL + Redis) focused on secure batch operations.

## О проекте
SteamFleetControl — это модульный монолит на .NET 8 для командной работы со Steam-аккаунтами:
- хранение и организация аккаунтов;
- живая авторизация и обслуживание сессий Steam;
- пакетные задачи (jobs) с пер-аккаунтными результатами;
- аудит действий и системных событий;
- web UI + API + worker в Docker.

## Ключевые возможности
- ASP.NET Identity + RBAC (`SuperAdmin`, `Admin`, `Operator`, `Auditor`).
- Защищённое хранение секретов (AES-GCM, мастер-ключ вне БД).
- Интеграция Steam: auth/session/profile/privacy/avatar/password/deauthorize/friends/games.
- Импорт/экспорт аккаунтов, теги, папки, семейные группы.
- Batch jobs через Hangfire (retry/backoff, dry-run, cancel, отчёты).
- Audit trail по бизнес-событиям и операциям безопасности.

## Быстрый старт
```bash
docker compose up --build -d
```

После запуска:
- UI: <http://localhost:8080>
- Swagger: <http://localhost:8080/swagger>
- Hangfire: <http://localhost:8080/hangfire>
- Health: <http://localhost:8080/health>

Дефолтный админ (локально):
- Email: `admin@local`
- Password: `Admin1234`

## Переменные окружения
Смотри [.env.example](.env.example):
- `ADMIN_EMAIL`
- `ADMIN_PASSWORD`
- `SECRETS_MASTER_KEY_B64`
- `WORKER_COUNT`

## Локальная разработка
```bash
dotnet build SteamFleet.slnx
dotnet test SteamFleet.slnx
```

## Документация
- Архитектура: [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md)
- Развёртывание: [docs/DEPLOYMENT.md](docs/DEPLOYMENT.md)
- API обзор: [docs/API.md](docs/API.md)
- Гайд для контрибьюторов: [CONTRIBUTING.md](CONTRIBUTING.md)
- Безопасность: [SECURITY.md](SECURITY.md)
- Правила сообщества: [CODE_OF_CONDUCT.md](CODE_OF_CONDUCT.md)

## Участие сообщества
Если хотите участвовать в развитии проекта:
1. Прочитайте [CONTRIBUTING.md](CONTRIBUTING.md).
2. Создайте issue с проблемой или предложением.
3. Отправьте PR по шаблону.

## Лицензия
Проект распространяется под [MIT License](LICENSE).
