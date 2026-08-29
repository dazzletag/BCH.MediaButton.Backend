# BCH.MediaButton.Backend

Backend for the Media Button system used in care homes. It serves Raspberry Pi devices and the staff web UI via a .NET 8 Web API deployed to Azure App Service. Priorities: clarity, stability, Pi-friendly APIs, simple auth, and minimal over-engineering.

## Structure

- `MediaButton.Backend.sln` - solution.
- `src/MediaButton.Backend.Api` - ASP.NET Core 8 Web API (minimal API).
- `web` - Vite + React SPA for staff/relatives to manage media and playlists.

## Running locally

Requirements: .NET 8 SDK.

```bash
dotnet restore
dotnet run --project src/MediaButton.Backend.Api
```

Swagger UI is enabled in Development. Health/info endpoints:
- `GET /health`
- `GET /api/info`

## Frontend (SPA)

```bash
cd web
cp .env.example .env.local   # fill API base + auth IDs
npm install
npm run dev
```

Auth: MSAL against the home tenant; scope defaults to `api://<API_CLIENT_ID>/access_as_user`. Pages cover media upload (SAS), playlist creation, and device assignment.

## Deploying

Target: Azure App Service (`mediabutton` in resource group `BCHSystems`).

```powershell
./scripts/deploy-api.ps1
```

That builds the portal bundle, publishes the API, zips it, deploys it, and
then checks the **live** site serves the bundle it just built.

### The trap this avoids

The API serves the portal from its own `wwwroot`, which `dotnet publish`
fills from `web/dist`. That directory is gitignored, so it is whatever the
publishing machine last built - it is not tracked, and a stale copy looks
identical to a fresh one.

Publishing from a checkout with an old `web/dist` therefore *silently
reverts the live portal*. That is exactly what happened on 2026-08-28: a
stale bundle removed the Reporting tab from production. Comparing
`web/dist` to the committed `publish/api/wwwroot` does **not** catch it -
that is the repo's copy of the bundle, not the deployed one. Only a
comparison against the live site does, which is what the script's final
step performs.

`dotnet publish` now refuses to run when `web/dist` is missing or older
than `web/src`. For an API-only change where you intend to leave the
deployed bundle untouched:

```powershell
./scripts/deploy-api.ps1 -SkipSpaBuild -Force
```

Building the bundle requires `web/.env.local` (copy `web/.env.example`) -
MSAL auth config is embedded at build time, and without it sign-in fails
with `AADSTS900144`.

`publish/api/` is committed as the record of what is deployed, since
`web/dist` itself is not tracked.

## Next steps

- Configure Azure SQL connection string (`ConnectionStrings:Default`), Storage (`Storage:*`), and Azure AD B2C (`AzureAdB2C:*`) in App Settings/Key Vault.
- Replace placeholder device keys in `appsettings.json` (or better: move to App Settings/Key Vault).
- Add CI (GitHub Actions) for build/test and deployment to App Service.
