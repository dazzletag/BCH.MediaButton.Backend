# Media Button Web

Single-page web experience for Bristol Care Homes staff and relatives to manage media and playlists for Media Button devices. Built with Vite + React + TypeScript, MSAL auth against the home tenant, and direct-to-blob uploads via the backend SAS endpoints.

## Quick start

```bash
cd web
cp .env.example .env.local  # fill values
npm install
npm run dev
```

## Environment variables

- `VITE_API_BASE_URL` — Backend base URL (e.g., `https://your-api.azurewebsites.net`).
- `VITE_AUTH_CLIENT_ID` — App registration client ID for `MediaButtonManagementAPI`.
- `VITE_AUTH_TENANT_ID` — Your home tenant ID.
- `VITE_AUTH_SCOPE` — API scope (defaults to `api://<client-id>/access_as_user` if omitted).

## Auth flow

MSAL React with `PublicClientApplication` + redirect login. Tokens are requested with the API scope and injected into API calls via `useApiClient`.

## API integration

- `GET /api/admin/media` — list media.
- `POST /api/admin/media/upload-url` + SAS PUT — upload assets.
- `POST /api/admin/media` — register uploaded asset.
- `GET /api/admin/playlists` — list playlists.
- `POST /api/admin/playlists` — create playlist.
- `PUT /api/admin/devices/{deviceId}/playlist` — assign playlist to a device.

## Design notes

Light, airy palette with Bristol Care Homes-inspired blues and a warm accent, layered glass cards, and Manrope + Playfair Display typography. Motion is kept subtle to stay Pi-friendly.
