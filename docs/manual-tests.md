# Manual tests

What the automated checks cannot judge: a person, a real app, and time passing. Run on the **local test server**
(Jellyfin 12.1 on this Mac, `http://localhost:8096`), where Phase 1 is already installed. The settings page is at
**Dashboard → Plugins → JellyMold Companion**; its first line says what has been recorded.

To read what was recorded, as any signed-in user, in a browser on the server's address:
`/JellyMold/v1/History` and `/JellyMold/v1/Summary?tz=Europe/London` (the web client's sign-in carries over only when
you are signed in on the same browser; the settings page's figures are the quickest check).

## Phase 1 — 2026-10-30

| # | Do this | Expect |
|---|---|---|
| 1 | Open the settings page | "Recording." and the counts; the completion line says 90% and 5 minutes for audiobooks |
| 2 | In the web client, play a film for two minutes, pause for a minute, play one more, stop | One entry under way, about three minutes of actual time; the pause not counted |
| 3 | Play an episode, skip to near the end, let it play past 90%, stop | The episode watched, credited its full running time |
| 4 | Tick a film you have not played as watched | It counts as watched, with no time added |
| 5 | Untick it again straight away | It no longer counts |
| 6 | In JellyMold, connect to this server, play an episode, then force-quit the app mid-play. Wait ten minutes | The session ends as *idle*, at the position you were at — not the minutes later Jellyfin would guess |
| 7 | Play a few songs, skipping one after ten seconds | The skipped one is not a listen; the others are |
| 8 | Settings page → untick yourself under *Who to record*, save, play something, tick yourself again | Nothing recorded while unticked |
| 9 | Settings page → **Back up now** | A backup listed with today's date |
| 10 | Settings page → **Export as CSV** for yourself | A file that opens in Numbers, one row per viewing |
| 11 | Move a film you have played out of its library folder (to another folder — **not** Jellyfin's Delete, which deletes the file), then scan the library. Move it back afterwards | Its history stays, marked as no longer in the library; after moving it back and scanning, the film carries on from its history |

Report anything that reads wrongly on the settings page as well as anything that counts wrongly.
