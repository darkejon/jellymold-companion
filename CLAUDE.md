# JellyMold Companion

A Jellyfin server plugin: a permanent, per-person record of everything watched and listened to on the
server, plus services that make the JellyMold iPhone/iPad app faster and richer. GPL-3.0, to be published as
a public repository, `darkejon/jellymold-companion`.

**Read before planning any work:**

- `docs/brief.md` — the brief, written in the app's own sessions (copied from the app repository)
- `docs/design.md` — the design. §19 holds the decisions taken and anything still open; §14 what differs on
  Jellyfin 12.
- `docs/api.md` — the API contract the app integration is built from (CC0)
- `docs/manual-tests.md` — what needs a person; add to it when something lands that the checks cannot judge

The maintainer is a UX designer, not a developer: they read code, run builds and make product and design
calls. Decisions with real trade-offs are presented, not quietly made. Write plainly. Small commits, one per
logical step; commit only when asked.

---

## Hard rules

1. **Never install on, or test against, JRDisk without asking.** It is the maintainer's real server
   (Synology, `192.168.4.126:8096`, Jellyfin 10.10.7) and holds their real library and watch data.
2. **Never `sudo`.** The .NET 10 SDK lives in `~/.dotnet` (builds both targets). Put it on the path:
   `export PATH="$HOME/.dotnet:$PATH"`.
3. **The JellyMold app repository (`../JellyMold`) is read-only from here.** Integrating the plugin into the
   app is done in the app's own sessions.
4. **Never print a secret.** Sign in through `op run --env-file=.env.op -- …` and keep tokens out of command
   output. `.env.op` holds 1Password references only, and is gitignored.
5. **Pin Jellyfin packages exactly**, and never ship them: the zip holds the plugin's two assemblies and
   `meta.json`, nothing else. A second copy of a Jellyfin or SQLite library in the plugin folder is a known way
   to break a server.
6. **History is never deleted by the plugin.** Migrations only add (design §10.2); a test fails the build on
   anything destructive. Add a migration as `Storage/Migrations/000N_name.sql` and bump `SchemaVersion`.
7. **No Jellyfin types outside `src/Jellyfin.Plugin.JellyMoldCompanion/Jellyfin/`.** The core library is shared by
   both builds and tested without a server.

---

## Layout

| | |
|---|---|
| `src/JellyMoldCompanion.Core` | Counting rules, recorder, database, queries. No Jellyfin types. `net8.0;net10.0`. |
| `src/Jellyfin.Plugin.JellyMoldCompanion` | What Jellyfin loads: the adapter and listeners (`Jellyfin/`), the API (`Api/`), the backup task, the settings page. |
| `tests/JellyMoldCompanion.Core.Tests` | xUnit, on a real SQLite file. Scenario tests read like the playback they describe (`Harness`). |
| `tools/` | Loading, test servers, live check, packaging — below. |

## The two builds

| | Jellyfin | Target | Plugin version | Tested on |
|---|---|---|---|---|
| **10.10** | 10.10.7 | `net8.0`, `JELLYFIN_10_10` | `10.<release>` | Docker, `localhost:8097` |
| **12** | 12.1+ | `net10.0`, `JELLYFIN_12` | `12.<release>` | The local server, `localhost:8096` |

The release (`0.1.0`) is `ReleaseVersion` in `Directory.Build.props`. A plugin version starts with the Jellyfin
major so a 12 server never installs the 10.10 build (design §13a).

## Building and checking

```bash
dotnet test tests/JellyMoldCompanion.Core.Tests        # the rules, ~1 s
dotnet build -c Release                                # both builds
tools/load-local.sh                                    # install the 12 build into the local server
tools/docker-10.10.sh load                             # install the 10.10 build into the Docker server
JELLYFIN_TOKEN=… tools/live-check.py                   # play a film and an episode, check what was recorded (~2 min)
tools/package.sh "What changed"                        # both zips + manifest.json
```

**Jellyfin restarts in-process and does not reload a plugin's code.** A new build needs the server process
restarted: `load-local.sh` quits and reopens Jellyfin.app, and `docker-10.10.sh load` restarts the container.
`POST /System/Restart` alone leaves the old code running.

### The local test server (Jellyfin 12.1)

- `http://localhost:8096`, server name *Mac-mini*. The native **Jellyfin.app**, not Docker.
- Data: `~/Library/Application Support/jellyfin` — plugins in `plugins/`, logs in `log/`, the history in
  `data/jellymold/history.db`.
- Accounts: `claude` (admin — `MAC_MINI_JELLYFIN_USER` / `MAC_MINI_JELLYFIN_PASSWORD` in `.env.op`) and `jon`
  (admin). No ordinary account.
- Libraries: *Movies*, *TV Shows*, *Minecraft videos* — the maintainer's own files. Never delete from them.

### The Docker test server (Jellyfin 10.10.7)

- `tools/docker-10.10.sh up`, then `tools/setup-test-server.py setup` once. `http://localhost:8097`, bound to
  localhost only, config in `.docker/` (gitignored).
- Accounts `admin` and `viewer` (ordinary), passwords generated into `.docker/jellyfin-10.10/test-users.json`.
  `tools/setup-test-server.py token viewer` prints a token for scripts to capture.
- Library: `test-media/` (gitignored), generated by `tools/make-test-media.sh` — films, a series, an album, a book
  in one file and a book in chapter files. Jellyfin's metadata providers match some of them to real titles; harmless.
- OrbStack runs Docker here: `open -a OrbStack` if `docker` cannot connect.
