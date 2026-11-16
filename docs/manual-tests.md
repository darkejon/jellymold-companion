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
| 1 | Open the settings page | "Recording." and the counts; the completion line says 90% (music and audiobook settings are not shown since 2026-11-14) |
| 2 | In the web client, play a film for two minutes, pause for a minute, play one more, stop | One entry under way, about three minutes of actual time; the pause not counted |
| 3 | Play an episode, skip to near the end, let it play past 90%, stop | The episode watched, credited its full running time |
| 4 | Tick a film you have not played as watched | It counts as watched, with no time added |
| 5 | Untick it again straight away | It no longer counts |
| 6 | In JellyMold, connect to this server, play an episode, then force-quit the app mid-play. Wait ten minutes | The session ends as *idle*, at the position you were at — not the minutes later Jellyfin would guess |
| 7 | Play a few songs, skipping one after ten seconds | The skipped one is not a listen; the others are |
| 7 | Settings page → untick yourself under *Who to record*, save, play something, tick yourself again | Nothing recorded while unticked |
| 8 | Settings page → **Back up now** | A backup listed with today's date |
| 9 | Settings page → **Export as CSV** for yourself | A file that opens in Numbers, one row per viewing |
| 10 | Move a film you have played out of its library folder (to another folder — **not** Jellyfin's Delete, which deletes the file), then scan the library. Move it back afterwards | Its history stays, marked as no longer in the library; after moving it back and scanning, the film carries on from its history |

Report anything that reads wrongly on the settings page as well as anything that counts wrongly.

## Phase 2 — 2026-10-30

Imports. On the local server, where `jon`'s baseline has already been taken (16 watched items).

| # | Do this | Expect |
|---|---|---|
| 1 | Settings page → *Past history* → *Imports so far* | Jellyfin's watch state imported for `claude` and `jon` |
| 2 | Export your Letterboxd diary (letterboxd.com → Settings → Data → Export), choose it under *Import for: jon*, *From: Letterboxd*, **Show what it would add** | A count of what it would add, what is not in this library, and a sample of rows with what will happen to each. Nothing added yet |
| 3 | **Import these** | The list shows it imported; the figures at the top of the page go up |
| 4 | Do step 2 again with the same file | *Already imported* for every row; nothing to add |
| 5 | **Undo** it in the list, and confirm | Its entries stop counting; the figures go back; the list says *undone* (Undo did nothing before 2026-11-14) |
| 6 | If you have a Trakt export, the same with *From: Trakt export* | As above; films and episodes matched by their ids |
| 7 | Read the page's wording throughout the section | Plain, and accurate to what happened |

Playback Reporting has been checked on the Docker 10.10.7 server with a real database. It is not on the local server.

## Phase 3 — 2026-10-30

On the local server unless it says otherwise. Most of Phase 3 is for the app to use; these are what a person can see.

| # | Do this | Expect |
|---|---|---|
| 1 | Settings page → *Helping Jellyfin* | Two switches, both off (on by default since 2026-11-14). The first says it is not needed on this server (Jellyfin 12 fixed it) |
| 2 | Switch on *Prepare subtitles ahead of time*, save; Dashboard → Scheduled Tasks → *JellyMold Companion* → **Prepare subtitles** → run | It finishes; a film with embedded subtitles added in the last week shows its subtitles at once when first chosen |
| 3 | In the web client, open an episode JellyMold has learned an intro for (once the app posts them) | A **Skip Intro** button in the web client too |
| 4 | Read the two switches' descriptions | Plain, and accurate about what each changes |

The resume-point fix needs Jellyfin 10.10, so it is checked on JRDisk only with your say-so, or on the Docker server.

## Black bars — 2026-11-07

On the local server. The plugin measures; the app reads what it measured once its own session has built that.

| # | Do this | Expect |
|---|---|---|
| 1 | Settings page → *Helping Jellyfin* | A third switch, *Measure black bars*, on, with how many files have been measured and how many have bars |
| 2 | Dashboard → Scheduled Tasks → *JellyMold Companion* → **Measure black bars** → run | It finishes; the count on the settings page goes up |
| 3 | Once the app reads it: play a widescreen Blu-ray remux for the first time on a device (*Companion (2025)* on JRDisk) | It opens with no black bars and no resize |
| 4 | The same with a film that opens out for IMAX scenes, if you have one | The IMAX scenes fill the screen; nothing is cut off |
| 5 | A 4K HDR film, and one in Dolby Vision if you have one | Cropped like the others. If not, the log line *measured …* says what was found |
| 6 | Read the switch's description | Plain, and accurate about what it does and costs |

Rows 3 to 5 need JRDisk's films, so they wait for your say-so.

## Stats on the web — 2026-11-14

On the local server, in a browser. The checks have covered signing in and out, the picker, the menu link and
both layouts on the 10.10 test server; these need a person and a real library.

| # | Do this | Expect |
|---|---|---|
| 1 | Settings page → *Stats on the web* → open the address | The page, signed in as you with no form: the *Watched* card, streaks, films, series, recently watched and each year, with your real figures |
| 2 | Compare it with the app's stats screen for this server | The same figures where the app shows only this server; where the app combines servers, the page has less |
| 3 | *More* under Films, and under Series; try each order | Every film or series, in that order, headed with how many |
| 4 | Click a film, a series and a recently watched row | Each opens its page in Jellyfin's web client |
| 5 | As an admin, choose someone else from the menu at the top | Their stats, headed *Stats for* their name |
| 6 | In a private window, open the address | Its own sign-in form. Sign in with an ordinary account: their stats, no menu, and *Sign out* |
| 7 | Open the page on a phone, in light and dark | It fits the width, in the app's colours |
| 8 | Dashboard menu → *Plugins* → *JellyMold Stats* | The stats page inside the dashboard, signed in, in the dashboard's theme, with one scroll bar (the dashboard's). *More*, then back, returns to where you were; a row opens the item in the dashboard |
| 9 | In a private window, the stats page's sign-in form | It says plainly to use the Jellyfin username and password |
| 10 | The banner at the top of the page, wide and on a phone | The app's icon, its line and *Get the app*, which opens the App Store. On a phone the button sits under the text. The cross hides it, and it stays hidden after a reload |
| 11 | As an admin, the menu of whose stats | Every account on the server, you first; someone who has watched nothing here shows *Nothing watched here yet* |
| 12 | After installing or updating, open Jellyfin in a browser as an ordinary account (a hard reload the first time) | *JellyMold Stats* in the side menu, opening their stats in a new tab, with nothing turned on. Settings page → *Remove the link from everyone's menu* takes it away at the next reload |
