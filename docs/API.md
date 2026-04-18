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
- `POST /api/accounts/{id}/authenticate/qr/start`
- `GET /api/accounts/{id}/authenticate/qr/{flowId}`
- `POST /api/accounts/{id}/authenticate/qr/{flowId}/cancel`
- `POST /api/accounts/{id}/validate-session`
- `POST /api/accounts/{id}/refresh-session`

Security/family/games/friends:
- `POST /api/accounts/{id}/password/change`
- `POST /api/accounts/{id}/sessions/deauthorize`
- `POST /api/accounts/{id}/games/refresh`
- `GET /api/accounts/{id}/games?scope=owned|family|all&q=&page=&pageSize=`
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

## Notes
- Check Swagger for the latest DTO schema and examples.
- Sensitive endpoints require proper role and are rate-limited.
