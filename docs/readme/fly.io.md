# Deploying Aria.Web on Fly.io

> **Fly.io is only an example hosting platform.** The concepts here — container deploy, persistent volume, secrets as environment variables, and the layered access gate — apply anywhere. Adapt the commands and configuration to whatever platform you actually use.

This guide covers deploying the `Aria.Web` Blazor Server app to Fly.io.

## What is deployed

- `Aria.Web` is the web UI.
- SQLite data is stored on a Fly.io volume mounted at `/data`.
- Aria.Bridge is **not** deployed — it runs locally on each user's machine.

## Directory context

All Fly commands must be run from `src/AriaAgent/` because that is where `fly.toml` and `Aria.Web/Dockerfile` live.

```bash
cd src/AriaAgent
```

## Initial deploy

1. Make sure you are in the right directory:
   ```bash
   cd src/AriaAgent
   ```

2. Deploy:
   ```bash
   fly deploy --app <your-app>
   ```

   Do **not** use `fly launch` unless you are creating a brand new app — it will try to re-detect the runtime and may fail.

## Access gate

Production builds run a layered access gate. The following paths are always public:

- `/health` — required by Fly.io health checks.
- `/api/bridge/*` and `/api/modelbridge` — required by the Aria.Bridge daemon.
- `/access/pathoftheworthy` — invite-code entry page.

Everything else is gated by one of these layers (first match wins):

1. **Dynamic bridge knock** — when an authenticated Aria.Bridge daemon connects, it reports its public IP every 60 seconds. Requests from that same IP are allowed for ~10 minutes.
2. **Static allow-list** — `IpRestriction__AllowedIPs` can still be set as an optional extra layer.
3. **`aria-worthy` invite-code cookie** — an admin invite code entered at `/access/pathoftheworthy`.
4. **`aria-trusted` persistent cookie** — set automatically after a browser proves it controls an enrolled bridge.

### Inviting someone with an admin code

1. Generate an invite code with an ISO-8601 UTC expiry, for example:
   ```bash
   INVITE=$(uuidgen | tr '[:lower:]' '[:upper:]')
   EXPIRY=$(date -u -v+7d +%Y-%m-%dT%H:%M:%SZ)   # macOS; use `date -u -d '+7 days'` on Linux
   echo "$INVITE:$EXPIRY"
   ```

2. Set it as a secret (comma- or semicolon-separated for multiple codes):
   ```bash
   fly secrets set GuestAccess__Codes="$INVITE:$EXPIRY"
   ```

3. Redeploy so the running machine picks it up:
   ```bash
   cd src/AriaAgent
   fly deploy --app <your-app>
   ```

4. Give **only the code part** (`$INVITE`) to your friend — **not** the expiry suffix. They open `https://<your-app>.fly.dev/access/pathoftheworthy`, enter it, and the gate opens for their session.

> After they link their local bridge, the bridge's periodic knock opens the gate for their network IP, so they don't need to re-enter the code on the same network.

### Optional: static IP allow-list

You can still pin specific IPs as an extra layer:

```bash
fly secrets set IpRestriction__AllowedIPs="YOUR.PUBLIC.IP.ADDRESS,5.6.7.8"
```

After changing the secret you must redeploy so the running machine picks it up:

```bash
fly deploy --app <your-app>
```

You can find your public IP at [https://ifconfig.me](https://ifconfig.me) or [https://ipinfo.io](https://ipinfo.io).

> **Note on detected IPs.** The gate derives the client IP from Fly's `X-Forwarded-For` header (falling back to `Fly-Client-IP`). The address shown on a 403 page is the one Fly reported; it may differ from the address reported by generic "what is my IP" sites, especially on IPv6.

### Important: `/health` is public

The `/health` endpoint is intentionally excluded from the gate so Fly.io health checks can verify the machine. It returns only `ok` and exposes no application data.

## Configuration overview

Key settings in `src/AriaAgent/fly.toml`:

```toml
[build]
  dockerfile = 'Aria.Web/Dockerfile'

[env]
  ASPNETCORE_ENVIRONMENT = 'Production'
  ASPNETCORE_URLS = 'http://+:8080'
  ConnectionStrings__Default = 'Data Source=/data/aria.db'

[[mounts]]
  source = 'aria_data'
  destination = '/data'
```

The container listens on port `8080`. Fly's service routes external ports `80`/`443` to that internal port.

## GitHub auto-deploy

If you connected Fly.io to GitHub:

1. In the app dashboard, go to **Settings → GitHub integration**.
2. Set **Root directory** to `src/AriaAgent`.
3. Pushes to the configured branch will trigger a deploy automatically.

If the root directory is wrong, Fly will report:

> Could not find a Dockerfile, nor detect a runtime or framework from source code.

## Troubleshooting

### "Could not find a Dockerfile"

You are running Fly commands from the repository root instead of `src/AriaAgent`.

**Fix:**

```bash
cd src/AriaAgent
fly deploy --app <your-app>
```

Or set the **Root directory** in the GitHub integration settings to `src/AriaAgent`.

### "The app is not listening on the expected address"

Fly's health check to `/health` failed, so it assumes nothing is listening on port 8080. Common causes:

- The access gate is blocking the health check.
- The app crashed during startup.

**Fix:**

- Make sure `/health` is public in the code (see `AccessGateMiddleware.cs`).
- Check the runtime logs:
  ```bash
  fly logs --app <your-app>
  ```
- Check machine status:
  ```bash
  fly status --app <your-app>
  ```

### "failed to get lease on VM"

Another Fly process still holds a lock on the machine.

**Fix:**

Wait a minute, then retry:

```bash
fly deploy --app <your-app>
```

If it persists, the full redeploy will clear the stale lease.

### "Deployment failed" with no details in the dashboard

The dashboard summary hides build errors.

**Fix:**

Run the deploy locally to see the full build log:

```bash
cd src/AriaAgent
fly deploy --app <your-app>
```

Or check **Logs & Errors → Build logs** in the dashboard.

### Health checks return `400 Bad Request - Invalid Hostname`

`AllowedHosts` in `appsettings.Production.json` was set to `<your-app>.fly.dev`, so Kestrel rejected health-check requests that didn't match that host. The production config no longer sets `AllowedHosts`.

### Site loads but shows 403

The access gate is working and your current IP/cookie is not recognized. The 403 page shows the **Detected IP** Fly reported for this request.

**Fix:**

1. If you have an invite code, go to `/access/pathoftheworthy` and enter it.
2. If your local bridge is linked, it will knock open the gate for your IP within ~60 seconds.
3. As a fallback, copy the **Detected IP** from the 403 page and add it to the optional static allow-list:
   ```bash
   fly secrets set IpRestriction__AllowedIPs="DETECTED.IP.ADDRESS"
   ```
4. Redeploy:
   ```bash
   fly deploy --app <your-app>
   ```

### I removed my IP whitelist / bridge soul but I can still access the site

The gate has several independent layers. Removing one does not invalidate the others:

- **Bridge knock** — if your local bridge is still running, it sends a knock every 60 seconds and renews a 10-minute access window for your current public IP. Stop the bridge locally to stop the knocks.
- **`aria-trusted` cookie** — set automatically after a browser proves it controls an enrolled bridge. It lasts 90 days.
- **`aria-worthy` invite-code cookie** — set when a valid invite code is entered at `/access/pathoftheworthy`. It lasts until the code's expiry.

**Fix:**

1. Stop your local bridge process so it stops knocking.
2. Clear browser cookies for `<your-app>.fly.dev`, or test in an incognito/private window.
3. To forcibly clear all active knocks on the server (for example, after unlinking a friend's bridge):
   ```bash
   fly ssh console --app <your-app>
   sqlite3 /data/aria.db "DELETE FROM UiAccessKnocks;"
   ```

### Site is unresponsive after deploy

1. Check status:
   ```bash
   fly status --app <your-app>
   ```
2. Check logs:
   ```bash
   fly logs --app <your-app>
   ```
3. Force a fresh deploy:
   ```bash
   cd src/AriaAgent
   fly deploy --app <your-app>
   ```

## Local Docker sanity check

You can verify the Dockerfile builds locally (the image is `linux-x64`, so it will not run natively on Apple Silicon):

```bash
cd src/AriaAgent
docker build -f Aria.Web/Dockerfile -t aria-web:test .
```

To fully run-test the image, use an amd64 Linux host or VM.
