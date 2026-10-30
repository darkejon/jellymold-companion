# JellyMold Companion

A Jellyfin server plugin that keeps a permanent record of everything each person watches and listens to on your
server — whichever app they use — and lets each person read their own history from an app.

- **Every playback, per person**, admin or not: films, episodes, other videos, music and audiobooks.
- **Actual time, not clock time.** Pauses, seeks, buffering and an app that vanished mid-episode add nothing.
- **Watched at 90%** (your server's own resume setting), and watching again counts again.
- **Kept for good.** History survives the media being deleted from your library, plugin updates (the database is
  only ever added to), and a damaged disk (nightly backups). The plugin never deletes history; you can export it.
- **Private by default.** Each person sees only their own history; admins can see everyone's.

It was built for [JellyMold](https://jellymold.app), an iPhone and iPad player for Jellyfin, and its API is open to
any app.

## Install

Works with **Jellyfin 10.10.7** and **Jellyfin 12.1 or later**. The right build installs itself.

1. In Jellyfin, open **Dashboard → Plugins**, go to **Repositories**, and add:
   - Name: `JellyMold Companion`
   - URL: `https://raw.githubusercontent.com/darkejon/jellymold-companion/main/manifest.json`
2. Open the **Catalog**, find **JellyMold Companion** under *General*, and install it.
3. **Restart Jellyfin.**

Recording starts straight away for everyone. Settings are under **Dashboard → Plugins → JellyMold Companion**:
what to record, who to record, the shortest play that counts, how many backups to keep, and export.

### Where the history is kept

`<Jellyfin data folder>/data/jellymold/history.db`, with nightly copies in `backups/` beside it (a Jellyfin
scheduled task, *Back up watch history*, at 03:00). A backup of your Jellyfin data folder includes it. Uninstalling
the plugin leaves it in place, so reinstalling carries on where it left off.

## For app developers

The API is under `/JellyMold/v1` and uses a person's ordinary Jellyfin sign-in. See [docs/api.md](docs/api.md).
`GET /JellyMold/v1/Info` answers 401 without a sign-in when the plugin is installed and 404 when it is not.

## For contributors

- [docs/design.md](docs/design.md) — the design, and every decision taken (§19)
- [CLAUDE.md](CLAUDE.md) — how the project is run: the test servers, the rules, the tools
- `tools/` — `load-local.sh` (install into a local Jellyfin.app), `docker-10.10.sh` and `setup-test-server.py` (a
  Jellyfin 10.10.7 test server), `live-check.py` (an end-to-end check against a real server), `package.sh` (release)

```bash
~/.dotnet/dotnet test tests/JellyMoldCompanion.Core.Tests
```

## Licence

GPL-3.0, like the Jellyfin libraries it is built against. The API description in [docs/api.md](docs/api.md) is
also released under CC0, so an app can be written from it under any licence.
