# Architecture Overview

## High-Level Design
SteamFleetControl is a modular monolith on .NET 8 with separate runtime processes:
- `SteamFleet.Web` (UI + API)
- `SteamFleet.Worker` (background jobs)
- `PostgreSQL` (primary data store)
- `Redis` (cache/coordination/rate-limit support)

## Project Layers
- `SteamFleet.Contracts` - shared DTOs/contracts.
- `SteamFleet.Domain` - domain entities and core enums.
- `SteamFleet.Application` - application-level orchestration.
- `SteamFleet.Persistence` - EF Core, identity, data services.
- `SteamFleet.Integrations.Steam` - live Steam gateway and protocol/web flows.
- `SteamFleet.Web` - Razor UI and REST API.
- `SteamFleet.Worker` - Hangfire processing and job execution.
- `tests/*` - unit and integration suites.

## Main Functional Modules
- Auth & RBAC (Identity + roles).
- Accounts (CRUD/import/export, tags/folders, status).
- Steam Session/Auth lifecycle.
- Jobs (batch operations, retry/backoff/cancel/reporting).
- Secrets management (AES-GCM, master key from env).
- Audit event stream.

## Data Model (Core)
- `steam_accounts`
- `steam_account_secrets`
- `steam_account_tags`, `steam_account_tag_links`
- `steam_account_games`
- `folders`
- `jobs`, `job_items`, `job_sensitive_reports`
- `audit_events`
- ASP.NET Identity tables

## Security Model
- RBAC boundaries per endpoint and action type.
- CSRF protection for UI forms.
- Rate limiting for login and sensitive operations.
- Secret material never stored in plaintext.
- Audit events for critical operations.

## Background Processing
Hangfire jobs process account operations in batches and store item-level results:
- dry-run support
- retry/backoff
- cancellation
- one-time sensitive report for password-change batch

## Observability
- Structured logging (Serilog).
- Correlation identifiers for request/job traceability.
- Optional OpenTelemetry export.
