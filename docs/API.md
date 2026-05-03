# API Overview

Base URL (local): `http://localhost:8080`

## Auth API
- `POST /api/auth/login`
- `POST /api/auth/logout`
- `POST /api/auth/refresh`
- `POST /api/auth/2fa/enable`

## Accounts API
- `GET /api/accounts`
- `POST /api/accounts`
- `GET /api/accounts/{id}`
- `PUT /api/accounts/{id}`
- `DELETE /api/accounts/{id}`
- `POST /api/accounts/import`
- `GET /api/accounts/export`

Steam/session actions:
- `POST /api/accounts/{id}/authenticate`
- `POST /api/accounts/qr/start`
- `GET /api/accounts/qr/{flowId}`
- `POST /api/accounts/qr/{flowId}/cancel`
- `POST /api/accounts/{id}/authenticate/qr/start`
- `GET /api/accounts/{id}/authenticate/qr/{flowId}`
- `POST /api/accounts/{id}/authenticate/qr/{flowId}/cancel`
- `POST /api/accounts/{id}/validate-session`
- `POST /api/accounts/{id}/refresh-session`

Security/family/games/friends:
- `POST /api/accounts/{id}/password/change`
- `POST /api/accounts/{id}/sessions/deauthorize`
- `GET /api/accounts/{id}/guard/confirmations`
- `POST /api/accounts/{id}/guard/confirmations/{confirmationId}/{confirmationKey}/accept`
- `POST /api/accounts/{id}/guard/confirmations/{confirmationId}/{confirmationKey}/deny`
- `POST /api/accounts/{id}/guard/confirmations/accept-batch`
- `POST /api/accounts/{id}/guard/link/start`
- `POST /api/accounts/{id}/guard/link/phone`
- `POST /api/accounts/{id}/guard/link/finalize`
- `POST /api/accounts/{id}/guard/remove`
- `POST /api/accounts/{id}/games/refresh`
- `GET /api/accounts/{id}/games?scope=owned|family|all&q=&page=&pageSize=`
- `POST /api/accounts/{id}/family/sync`
- `GET /api/accounts/{id}/family`
- `POST /api/accounts/{id}/family/assign-parent`
- `POST /api/accounts/{id}/family/remove-parent`
- `POST /api/accounts/{id}/friends/invite-link/sync`
- `GET /api/accounts/{id}/friends/invite-link`
- `POST /api/accounts/{id}/friends/accept-invite`
- `POST /api/accounts/{id}/friends/refresh`
- `GET /api/accounts/{id}/friends`

## Jobs API
- `POST /api/jobs/profile-update`
- `POST /api/jobs/privacy-update`
- `POST /api/jobs/avatar-update`
- `POST /api/jobs/tags-assign`
- `POST /api/jobs/group-move`
- `POST /api/jobs/add-note`
- `POST /api/jobs/session-validate`
- `POST /api/jobs/session-refresh`
- `POST /api/jobs/password-change`
- `POST /api/jobs/sessions-deauthorize`
- `POST /api/jobs/friends-add-by-invite`
- `POST /api/jobs/friends-connect-family-main`
- `GET /api/jobs/{id}`
- `GET /api/jobs/{id}/items`
- `POST /api/jobs/{id}/cancel`
- `GET /api/jobs/{id}/sensitive-report`

## Audit API
- `GET /api/audit-events`
- `GET /api/audit-events/{id}`

## Internal Worker Adapter API (DDCRM integration runtime)
- `GET /internal/v2/worker/health`
- `GET /internal/v2/worker/capabilities`
- `GET /internal/v2/worker/account`
- `POST /internal/v2/worker/actions/{action}`

Поддерживаемые action:
- `ext.integration.steam.read`
- `ext.integration.steam.jobs`
- `ext.account.proxy-credentials.apply` (no-op, для bootstrap совместимости)

Контракт `POST /internal/v2/worker/actions/{action}`:
- тело: `{ "payload": { ... } }` (`payload` обязателен, JSON object);
- успешный ответ: `{ "requestId": "...", "result": { ... } }`;
- ошибки валидации action/payload: `400` c `error=validation_failed`.

`ext.integration.steam.read` (`payload.operation`):
- `accounts.list` — параметры: `query?`, `status?`, `page?`, `pageSize?`;
- `accounts.get` — параметры: `accountId` (GUID);
- `jobs.list` — параметры: `take?`;
- `jobs.details` — параметры: `jobId` (GUID).

`ext.integration.steam.jobs` (`payload.operation`):
- `accounts.create` — параметры: `account` (`AccountUpsertRequest`);
- `accounts.update` — параметры: `accountId` (GUID), `account` (`AccountUpsertRequest`);
- `accounts.archive` — параметры: `accountId` (GUID);
- `jobs.create` — параметры: `job` (`JobCreateRequest`);
- `jobs.cancel` — параметры: `jobId` (GUID).

Пример `accounts.create`:
```json
{
  "payload": {
    "operation": "accounts.create",
    "account": {
      "loginName": "steam_login",
      "displayName": "Steam Account",
      "status": "Active"
    }
  }
}
```

Требования:
- обязательный `X-Service-Token` (`WORKER_API_SERVICE_AUTH_ACCEPTED_TOKENS`);
- legacy DDCRM service-token API `/internal/v1/ddcrm/*` деактивирован (410 Gone);
- runtime изоляция задаётся через env scope (`DDCRM_WORKER_ACCOUNT_ID`, `DDCRM_WORKER_PROJECT_ID`, `STEAM_ACCOUNTS_STORAGE_NAMESPACE`).

## Notes
- Check Swagger for the latest DTO schema and examples.
- Sensitive endpoints require proper role and are rate-limited.
- Steam Family is synced from Steam (`/family/sync`) and is the source of truth for family grouping.
- Legacy routes `family/assign-parent` and `family/remove-parent` are reserved and currently return controlled `409` (manual writes disabled).
- QR onboarding lifecycle:
  - `POST /api/accounts/qr/start` returns `{ flowId, challengeUrl, qrImageDataUrl, expiresAt, pollingIntervalSeconds }`.
  - `GET /api/accounts/qr/{flowId}` returns pending/completed/conflict/failed status.
  - duplicate account returns `409 Conflict` with `reasonCode=DuplicateAccount` and `existingAccount`.

## Risk-Aware Behavior
- `AccountDto` includes risk profile fields:
  - `riskLevel`, `authFailStreak`, `lastRiskReasonCode`, `lastRiskAt`, `autoRetryAfter`
- Controlled Steam operation errors are returned with:
  - `errorMessage`
  - `reasonCode`
  - `retryable`
- New/extended reason codes used by hardening:
  - `CooldownActive`
  - `InvalidCredentials`
  - `AccessDenied`
  - `AuthThrottled`
  - `SessionReplaced`
