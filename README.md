# BCH.MediaButton.Backend

Backend for the Media Button system used in care homes. It serves Raspberry Pi devices and the staff web UI via a .NET 8 Web API deployed to Azure App Service. Priorities: clarity, stability, Pi-friendly APIs, simple auth, and minimal over-engineering.

## Structure

- `MediaButton.Backend.sln` — solution.
- `src/MediaButton.Backend.Api` — ASP.NET Core 8 Web API (minimal API).

## Running locally

Requirements: .NET 8 SDK.

```bash
dotnet restore
dotnet run --project src/MediaButton.Backend.Api
```

Swagger UI is enabled in Development. Health/info endpoints:
- `GET /health`
- `GET /api/info`

## Deploying

- Target: Azure App Service.
- Publish with `dotnet publish -c Release` and deploy the output of `src/MediaButton.Backend.Api/bin/Release/net8.0/publish`.

## Next steps

- Add simple auth (e.g., API keys or bearer tokens) suitable for Pi devices.
- Define request/response contracts for Pi device interactions.
- Add CI (GitHub Actions) for build/test and basic linting.
- Document required environment variables and deployment pipeline specifics.
