# linkarr

linkarr ties together **visularr** (the media server, a Jellyfin fork) and **modularr** (the Sonarr/Radarr/Prowlarr/qBittorrent replacement), so neither manages users on its own.

- **Users and rights, in one place.** linkarr is the source of truth. Each user gets rights (`admin`, `watch`, `libraries`, `request`, `manageDownloads`, `modularrAccess`) and linkarr creates, updates, disables and deletes the matching accounts in both apps:
  - visularr: the Jellyfin user and its policy (`IsAdministrator`, `IsDisabled`, `EnableMediaPlayback`, `EnableAllFolders`/`EnabledFolders`) and its password.
  - modularr: an `Admin` (manage downloads) or `User` account through modularr's provisioning API.
  - Passwords are sent when they are chosen (create, reset, change) and never stored, only hashed for linkarr's own sign-in. If an app is down the user shows **out of sync**; other fields retry on their own every 10 minutes, a password has to be entered again.
  - Users created directly in an app show up as **unmanaged**; adopt them or delete them.
- **Catalog.** Every 5 minutes, on demand, and when modularr's webhook fires, linkarr merges modularr's movies, series and queue with visularr's items. Each title is `available`, `downloading`, `notIndexed` (modularr has the file and visularr doesn't show it: one click sends a targeted refresh, or turn on the auto-fix), `untracked` (in visularr only: one click monitors it in modularr), `missing` or `unmonitored`.
- **Requests.** Users with the `request` right search through linkarr (the modularr key stays on the server) and request a title; linkarr adds it to modularr, monitored, and starts a search. Requests can need an admin's approval. Their status follows the catalog: requested → downloading → available.
- **Dashboard** with the catalog counts, active downloads, out-of-sync users and links into both apps.

## Run the trio

The `docker-compose.yml` at the root runs modularr, visularr and linkarr on one network. They reach each other by service name; modularr and visularr see the media folder at `/media` (read-only for visularr; linkarr does not mount it).

linkarr is the only app with a first-run setup: modularr and visularr start with no accounts, and their API keys come from `.env`.

```sh
# The images modularr:dev and visularr:dev are built from their own repositories.
# 1. Generate the two API keys (see .env.example for the optional MEDIA_DIR, public URLs and TMDB key).
printf 'MODULARR_API_KEY=%s\nVISULARR_API_KEY=%s\n' "$(openssl rand -hex 32)" "$(openssl rand -hex 32)" >> .env
# 2. Start the trio (compose refuses to start while a key is missing).
docker compose up -d --build
```

3. Open linkarr at http://localhost:8070 (by IP or `localhost`: the first account can only be created that way, or from a host listed in `AllowedHosts`) and **create the admin account**. Compose publishes linkarr on `127.0.0.1` only, so nobody else on the network can claim that account first; to open it to the LAN, set `LINKARR_SETUP_TOKEN` in `.env` (setup then asks for it) and change the port to `"8070:8070"`, or put a reverse proxy in front. It signs in to linkarr, visularr and modularr: linkarr creates it as admin in both apps with the same password, then deletes visularr's hidden `visularr-bootstrap` admin. If an app is still starting, linkarr keeps retrying for 2 minutes (the password stays in memory only); after that the account shows *password needed*, and changing the password on the Account page sends it.
4. **Create the users** in linkarr (Users > New user), choosing for each whether it gets a visularr and a modularr account.

The connections (`MODULARR_URL`, `MODULARR_API_KEY`, `MODULARR_PUBLIC_URL`, `VISULARR_URL`, `VISULARR_API_KEY`, `VISULARR_PUBLIC_URL`, `LINKARR_PUBLIC_URL`) are set by compose: linkarr writes them into its settings at every start (keys encrypted like any other) and its Settings page shows them read-only as *Set by the deployment*. Without these variables (e.g. `dotnet run`), they are entered in Settings instead.

| App      | URL                     |
|----------|-------------------------|
| modularr | http://localhost:8080   |
| visularr | http://localhost:8096   |
| linkarr  | http://localhost:8070   |

modularr also publishes 6881 (tcp and udp) for torrents. Behind a reverse proxy, bind modularr and visularr to `127.0.0.1` too, and set `LINKARR_TRUSTED_PROXIES` to the proxy's address so linkarr reads the client address from `X-Forwarded-For` (only the proxy: anything in that range can choose the address linkarr sees). An `https` `LINKARR_PUBLIC_URL` makes the session cookie `Secure`.

## What happens on its own

- At start, and after every Settings save, linkarr tells both apps they are managed (they then hide their own user management and link to linkarr) and syncs the users.
- modularr's events need no setup: the managed push hands modularr linkarr's URL and webhook token, and modularr posts its events to linkarr (`Linkarr__InternalUrl` inside compose). Imports then update the catalog (15 s after the last event) and, with the auto-fix on, visularr scans the imported folder at once.
- The apps link to each other through linkarr: `/go/watch?tmdb=|tvdb=` (visularr item), `/go/manage?tmdb=|tvdb=` (modularr title), `/go/app/modularr|visularr`. They redirect only to the configured public URLs, and only for a signed-in linkarr user to the title linkarr resolved (otherwise to the app's own lookup by id, so the links reveal nothing about the catalog).
- Users created directly in an app show as unmanaged (visularr's `visularr-bootstrap` never does): adopt them or delete them. linkarr never links an app user by name: a linkarr user whose name is already taken in an app shows a sync error until an admin adopts or deletes that app user.

## Security

- linkarr's own sign-in mirrors modularr: cookie sessions backed by session rows and a security stamp (password, admin or disabled change signs out), 10 sign-in attempts a minute per address (per /64 for IPv6), a same-origin check on every write, and admin-only writes enforced on the server.
- **API keys are encrypted at rest with ASP.NET Data Protection.** The key ring lives in `/config/keys`, created with mode `0700`; the API never returns a key, only whether one is set. A copy of `linkarr.db` alone does not reveal them.
- Passwords and keys are never logged. They only travel to the configured app URLs; the outbound client has a 10 s timeout, follows no redirect and caps responses at 32 MB.
- The webhook token travels in the `X-Linkarr-Token` header (the `?token=` query is still accepted for an older modularr, for one release) and is compared in constant time; Settings > *Rotate webhook token* replaces it and pushes it to modularr. An event's folder is only scanned when it is a catalog title's folder.
- Errors an app returns (with an excerpt of its answer) are shown to admins only; members get a generic message.
- Requests: non-admins may request `Requests per day` titles a day (Settings, 10 by default) and cannot re-request a title denied to them until an admin re-opens (approves) or deletes that request.
- Responses carry a Content-Security-Policy (`default-src 'self'`, inline scripts and styles for the static export, `https:` images for posters), `nosniff`, `Referrer-Policy: same-origin` and `frame-ancestors 'none'`.

## Development

```sh
cd backend && dotnet build && dotnet test
cd frontend && npm ci && npx tsc --noEmit && npx eslint src && npm run build
```

`dotnet run --project backend/src/Linkarr.Api --urls http://localhost:8070` runs the API with its config in `backend/src/Linkarr.Api/config`; `npm run dev` in `frontend` proxies `/api` to it.

The code is explicitly typed: no `var` in C# (`IDE0008` and `csharp_style_var_*` are build errors via `.editorconfig`), no `var` or `any` and explicit return types in TypeScript (ESLint). Warnings are errors.
