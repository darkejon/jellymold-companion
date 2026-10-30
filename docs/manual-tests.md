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

## Phase 2 — 2026-10-30

Imports. On the local server, where `jon`'s baseline has already been taken (16 watched items).

| # | Do this | Expect |
|---|---|---|
| 1 | Settings page → *Past history* → *Imports so far* | Jellyfin's watch state imported for `claude` and `jon` |
| 2 | Export your Letterboxd diary (letterboxd.com → Settings → Data → Export), choose it under *Import for: jon*, *From: Letterboxd*, **Show what it would add** | A count of what it would add, what is not in this library, and a sample of rows with what will happen to each. Nothing added yet |
| 3 | **Import these** | The list shows it imported; the figures at the top of the page go up |
| 4 | Do step 2 again with the same file | *Already imported* for every row; nothing to add |
| 5 | **Undo** it in the list | Its entries stop counting; the figures go back |
| 6 | If you have a Trakt export, the same with *From: Trakt export* | As above; films and episodes matched by their ids |
| 7 | Read the page's wording throughout the section | Plain, and accurate to what happened |

Playback Reporting has been checked on the Docker 10.10.7 server with a real database. It is not on the local server.

## Phase 3 — 2026-10-30

On the local server unless it says otherwise. Most of Phase 3 is for the app to use; these are what a person can see.

| # | Do this | Expect |
|---|---|---|
| 1 | Settings page → *Helping Jellyfin* | Two switches, both off. The first says it is not needed on this server (Jellyfin 12 fixed it) |
| 2 | Switch on *Prepare subtitles ahead of time*, save; Dashboard → Scheduled Tasks → *JellyMold Companion* → **Prepare subtitles** → run | It finishes; a film with embedded subtitles added in the last week shows its subtitles at once when first chosen |
| 3 | In the web client, open an episode JellyMold has learned an intro for (once the app posts them) | A **Skip Intro** button in the web client too |
| 4 | Read the two switches' descriptions | Plain, and accurate about what each changes |

The resume-point fix needs Jellyfin 10.10, so it is checked on JRDisk only with your say-so, or on the Docker server.
