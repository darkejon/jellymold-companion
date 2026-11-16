/*
  The JellyMold stats page (design §16 idea 12): the app's stats screen on the web, drawn from the plugin's API by the
  viewer's own sign-in. A person sees their own history; an admin can pick anyone the plugin has recorded. The API
  enforces that, not this page.

  Signing in: the page is served from Jellyfin's own address, so where Jellyfin's web client is signed in to this server
  in the same browser, the page uses that sign-in and asks nothing. Otherwise it has its own form, which sends the
  password only to this server, and keeps the sign-in as Jellyfin's web client keeps its own.

  No quizzes: they are not finished in the app yet (2026-11-14).
*/
'use strict';

(() => {
  // — Where things are —

  // The page is at <root>/JellyMold/Stats, whatever <root> a base URL or a reverse proxy gives it.
  const ROOT = location.pathname.replace(/\/JellyMold\/Stats\/?$/i, '');
  const at = path => `${ROOT}/${path}`;

  const OWN_SIGN_IN = 'jellymold-stats-sign-in';
  const DEVICE = 'jellymold-stats-device';
  const PERSON = 'jellymold-stats-person';
  const LIST_SHOWN = 5;   // the app's: five of each, and More for the rest

  // Inside Jellyfin's dashboard (its JellyMold Stats item, Configuration/statsMenu.html): drawn in the dashboard's theme,
  // as tall as it is so the dashboard does the scrolling, and opening Jellyfin's pages in the dashboard, not the frame.
  const QUERY = new URLSearchParams(location.search);
  const EMBEDDED = window.parent !== window && QUERY.get('embedded') === 'dashboard';
  const scroller = EMBEDDED ? window.parent : window;
  if (EMBEDDED && ['dark', 'light'].includes(QUERY.get('theme'))) document.documentElement.dataset.theme = QUERY.get('theme');
  if (EMBEDDED) {
    document.documentElement.classList.add('embedded');
    const tell = () => window.parent.postMessage({ jellymoldStats: { height: document.body.offsetHeight } }, location.origin);
    new ResizeObserver(tell).observe(document.body);
  }

  /** Scrolls to y. Inside the dashboard the frame takes its new height first, or the dashboard could not scroll that far. */
  function scrollPage(y) {
    if (EMBEDDED && window.frameElement) window.frameElement.style.height = `${document.body.offsetHeight}px`;
    scroller.scrollTo(0, y);
  }

  const state = {
    server: null,     // System/Info/Public
    token: null,
    own: false,       // signed in on this page, rather than through Jellyfin's web client
    me: null,         // Users/Me
    info: null,       // JellyMold/v1/Info
    people: [],       // for an admin: everyone the plugin has recorded
    person: null,     // whose stats are shown
    summary: null,
    lists: new Map(), // the full film and series lists, as fetched
    homeScroll: 0,
    zone: (() => { try { return Intl.DateTimeFormat().resolvedOptions().timeZone; } catch { return ''; } })(),
  };

  const main = document.getElementById('main');

  // — Storage, which a private window may refuse —

  function read(store, key) {
    try { return store.getItem(key); } catch { return null; }
  }
  function readJson(store, key) {
    try { return JSON.parse(read(store, key) || 'null'); } catch { return null; }
  }
  function write(store, key, value) {
    try { store.setItem(key, typeof value === 'string' ? value : JSON.stringify(value)); } catch { /* kept for this visit only */ }
  }
  function forget(store, key) {
    try { store.removeItem(key); } catch { /* nothing kept */ }
  }

  // — JellyMold itself: the banner at the top, until it is hidden in this browser —

  const PROMO_HIDDEN = 'jellymold-stats-promo-hidden';
  const promo = document.getElementById('promo');
  promo.hidden = read(localStorage, PROMO_HIDDEN) === '1';
  document.getElementById('promo-hide').addEventListener('click', () => {
    promo.hidden = true;
    write(localStorage, PROMO_HIDDEN, '1');
    document.getElementById('main').focus({ preventScroll: true });
  });

  // — Asking the server —

  function deviceId() {
    let id = read(localStorage, DEVICE);
    if (!id) {
      const bytes = new Uint8Array(16);
      crypto.getRandomValues(bytes);
      id = Array.from(bytes, b => b.toString(16).padStart(2, '0')).join('');
      write(localStorage, DEVICE, id);
    }
    return id;
  }

  /*
    This page's own sign-in names itself, so it shows as "JellyMold Companion" in Jellyfin's list of devices. With
    Jellyfin's web client's sign-in it sends the token alone: naming itself would rename that device on the server.
  */
  function authorization(token, own) {
    if (token && !own) return `MediaBrowser Token="${token}"`;
    const parts = ['Client="JellyMold Companion"', 'Device="Web browser"', `DeviceId="${deviceId()}"`, 'Version="1"'];
    if (token) parts.push(`Token="${token}"`);
    return `MediaBrowser ${parts.join(', ')}`;
  }

  class Refusal extends Error {
    constructor(status, detail) {
      super(detail || `The server answered ${status}.`);
      this.status = status;
    }
  }

  async function ask(path, { method = 'GET', body, anonymous = false, token = state.token, own = state.own } = {}) {
    const headers = { Accept: 'application/json' };
    if (!anonymous) headers.Authorization = authorization(token, own);
    if (body !== undefined) headers['Content-Type'] = 'application/json';
    const response = await fetch(at(path), {
      method,
      headers,
      body: body === undefined ? undefined : JSON.stringify(body),
      cache: 'no-store',
      credentials: 'omit',
    });
    if (!response.ok) {
      let detail = null;
      try { detail = (await response.json()).Detail; } catch { /* not JSON */ }
      throw new Refusal(response.status, detail);
    }
    return response.status === 204 ? null : response.json();
  }

  const id = value => String(value || '').replace(/-/g, '').toLowerCase();
  const self = () => state.person === id(state.me.Id);
  const forPerson = () => (self() ? '' : `&userId=${state.person}`);

  // — Words and numbers, as the app writes them —

  const number = new Intl.NumberFormat();
  const relativeFormat = new Intl.RelativeTimeFormat(undefined, { numeric: 'auto' });

  /** As a headline figure: "412h", "2h 4m", "44m" — hours alone once there are ten of them. */
  function figure(seconds) {
    if (seconds >= 36000) return `${Math.round(seconds / 3600)}h`;
    return written(Math.round(seconds / 60));
  }

  /** As a row gives it: "150h 23m", "2h 25m", "44m" — the minutes kept however many hours. */
  function hoursAndMinutes(seconds) {
    return written(Math.max(1, Math.round(seconds / 60)));
  }

  function written(minutes) {
    const hours = Math.floor(minutes / 60);
    const rest = minutes % 60;
    if (hours === 0) return `${rest}m`;
    return rest === 0 ? `${hours}h` : `${hours}h ${rest}m`;
  }

  function startOfDay(date) {
    return new Date(date.getFullYear(), date.getMonth(), date.getDate());
  }

  /** "yesterday", "3 days ago", "last week", "2 months ago". */
  function relative(when) {
    const date = new Date(when);
    const now = new Date();
    const days = Math.round((startOfDay(date) - startOfDay(now)) / 86400000);
    if (days === 0) {
      const minutes = Math.round((date - now) / 60000);
      if (Math.abs(minutes) < 1) return relativeFormat.format(0, 'second');
      if (Math.abs(minutes) < 60) return relativeFormat.format(minutes, 'minute');
      return relativeFormat.format(Math.round(minutes / 60), 'hour');
    }
    if (Math.abs(days) < 7) return relativeFormat.format(days, 'day');
    if (Math.abs(days) < 30) return relativeFormat.format(Math.round(days / 7), 'week');
    if (Math.abs(days) < 365) return relativeFormat.format(Math.round(days / 30.44), 'month');
    return relativeFormat.format(Math.round(days / 365.25), 'year');
  }

  const capital = text => text.charAt(0).toLocaleUpperCase() + text.slice(1);

  /** "30 Oct", the year added where it is not this one. */
  function shortDate(date) {
    const options = { day: 'numeric', month: 'short' };
    if (date.getFullYear() !== new Date().getFullYear()) options.year = 'numeric';
    return date.toLocaleDateString(undefined, options);
  }

  /** A date with no time, "2026-03-02", as that day here. */
  function day(text) {
    const [y, m, d] = String(text).split('-').map(Number);
    return new Date(y, m - 1, d);
  }

  const days = count => (count === 1 ? '1 day' : `${number.format(count)} days`);
  const plural = (count, one, many) => (count === 1 ? `1 ${one}` : `${number.format(count)} ${many}`);

  /** "S2 E3 · The Title", or as much of it as the record has. */
  function episodeLine(item) {
    let numbers = '';
    if (item.SeasonNumber != null) numbers += `S${item.SeasonNumber}`;
    if (item.EpisodeNumber != null) numbers += `${numbers ? ' ' : ''}E${item.EpisodeNumber}`;
    return [numbers, item.Name].filter(Boolean).join(' · ');
  }

  // — Building the page —

  function h(tag, props, ...children) {
    const node = document.createElement(tag);
    for (const [key, value] of Object.entries(props || {})) {
      if (value == null || value === false) continue;
      if (key === 'class') node.className = value;
      else if (key.startsWith('on')) node.addEventListener(key.slice(2), value);
      else node.setAttribute(key, value === true ? '' : value);
    }
    for (const child of children.flat(Infinity)) {
      if (child == null || child === false) continue;
      node.append(child instanceof Node ? child : String(child));
    }
    return node;
  }

  const ICONS = {
    flame: '<path d="M12 3c.6 3.2 4.8 5.3 4.8 10a4.8 4.8 0 0 1-9.6 0c0-2.3 1.1-3.8 2.3-4.9.3 1.7 1.2 2.7 2.1 3C11.1 8.7 11.3 5.6 12 3z"/>',
    trophy: '<path d="M8 4h8v5.5a4 4 0 0 1-8 0z"/><path d="M8 6H5.2c0 2.3 1.3 3.8 3.1 4.1M16 6h2.8c0 2.3-1.3 3.8-3.1 4.1M12 13.5V17M9 20h6M10 17h4v3h-4z"/>',
    calendar: '<rect x="4" y="5.5" width="16" height="14.5" rx="2"/><path d="M4 10h16M8.5 3.5v4M15.5 3.5v4"/>',
    chevron: '<path d="M1.5 1.5L6.5 7l-5 5.5"/>',
    back: '<path d="M10 2L2.5 10l7.5 8"/>',
    tick: '<circle cx="12" cy="12" r="10" fill="currentColor" stroke="none"/><path d="M7.5 12.3l3 3 6-6.3" stroke="#fff" stroke-width="2.2"/>',
  };
  const VIEWBOXES = { chevron: '0 0 8 14', back: '0 0 12 20' };

  function icon(name, className) {
    const svg = document.createElementNS('http://www.w3.org/2000/svg', 'svg');
    svg.setAttribute('viewBox', VIEWBOXES[name] || '0 0 24 24');
    svg.setAttribute('fill', 'none');
    svg.setAttribute('stroke', 'currentColor');
    svg.setAttribute('stroke-width', '1.8');
    svg.setAttribute('stroke-linecap', 'round');
    svg.setAttribute('stroke-linejoin', 'round');
    svg.setAttribute('aria-hidden', 'true');
    if (className) svg.setAttribute('class', className);
    svg.innerHTML = ICONS[name];   // the page's own drawings, never data
    return svg;
  }

  /** Jellyfin's own page for an item, in its web client — what a row opens, as the app's rows open the item. */
  function itemPage(itemId) {
    return itemId && state.server ? at(`web/#/details?id=${id(itemId)}&serverId=${encodeURIComponent(state.server.Id)}`) : null;
  }

  function row(href, children) {
    return h('li', null, href ? h('a', { class: 'row', href, target: EMBEDDED ? '_top' : null }, children) : h('div', { class: 'row' }, children));
  }

  function line(first, last, className) {
    return h('div', { class: className ? `line ${className}` : 'line' }, h('span', null, first), last != null && last !== '' ? h('span', null, last) : null);
  }

  function bar(fraction, done) {
    const fill = h('div', { class: done ? 'bar-fill done' : 'bar-fill' });
    fill.style.width = `${Math.round(Math.min(Math.max(fraction, 0), 1) * 1000) / 10}%`;
    return h('div', { class: 'bar-track', 'aria-hidden': 'true' }, fill);
  }

  function stat(symbol, title, detail, value) {
    return h('li', { class: 'with-icon' }, h('div', { class: 'stat' },
      h('span', { class: 'stat-icon' }, icon(symbol)),
      h('span', { class: 'stat-text' }, title, detail ? h('span', { class: 'stat-detail' }, detail) : null),
      h('span', { class: 'stat-value' }, value)));
  }

  function section(className, title, items, foot) {
    return h('section', { class: `section ${className}`, 'aria-label': title },
      h('h2', null, title),
      h('ul', { class: 'list' }, items),
      foot ? h('p', { class: 'section-foot' }, foot) : null);
  }

  /** Three figures across, each centred in its own third: the card's, or a section's time at the head of its list. */
  function figures(list, compact) {
    return h('div', { class: compact ? 'figures compact' : 'figures' }, list.map(f => h('div', { class: 'figure' },
      f.above ? h('span', { class: 'figure-above' }, f.above) : null,
      h('span', { class: 'figure-value' }, f.value),
      h('span', { class: 'figure-label' }, f.below))));
  }

  // — Rows —

  /** A film watched or under way: title and year, a bar for how far into one under way, when, and how long it runs. */
  function filmRow(film) {
    const item = film.Item;
    const runtime = film.RuntimeSeconds;
    const position = film.Finished ? null : film.PositionSeconds;
    let said = null;
    if (!item.InLibrary) said = 'No longer in your library';
    else if (film.LastWatched && film.Viewings > 1) said = `Watched ${film.Viewings === 2 ? 'twice' : `${number.format(film.Viewings)} times`}, last ${relative(film.LastWatched)}`;
    else if (film.LastWatched) said = `Last watched ${relative(film.LastWatched)}`;

    let length = null;
    if (position != null && runtime) length = `${hoursAndMinutes(position)} of ${hoursAndMinutes(runtime)}`;
    else if (position != null) length = hoursAndMinutes(position);
    else if (runtime) length = hoursAndMinutes(runtime);

    return row(item.InLibrary ? itemPage(item.Id) : null, [
      line(item.Name, item.ProductionYear != null ? String(item.ProductionYear) : null),
      position != null && runtime ? bar(position / runtime) : null,
      said || length ? line(said || '', length, 'sub') : null,
    ]);
  }

  /** A series started: episodes watched out of its total, a bar for how far, and when it was last watched. */
  function seriesRow(series) {
    const total = series.EpisodesInLibrary;
    const watched = series.EpisodesWatched;
    const count = total != null ? `${number.format(watched)} of ${number.format(total)}` : plural(watched, 'episode', 'episodes');
    const right = h('span', { class: 'count' }, count);
    const top = h('div', { class: 'line' }, h('span', null, series.Name),
      series.Finished ? icon('tick', 'tick') : null,
      series.Finished ? h('span', { class: 'visually-hidden' }, 'Finished') : null,
      right);

    let said = '';
    if (!series.InLibrary) said = 'No longer in your library';
    else if (series.LastWatched) said = `Last watched ${relative(series.LastWatched)}`;

    return row(series.InLibrary ? itemPage(series.SeriesId) : null, [
      top,
      total > 0 ? bar(watched / total, series.Finished) : null,
      said || series.CreditedSeconds > 0 ? line(said, series.CreditedSeconds > 0 ? hoursAndMinutes(series.CreditedSeconds) : null, 'sub') : null,
    ]);
  }

  /** Something watched lately: a film, or an episode under its series. */
  function recentRow(entry) {
    const item = entry.Item;
    const episode = item.Type === 'Episode';
    const sub = episode ? episodeLine(item) : (item.InLibrary ? null : 'No longer in your library');
    return row(item.InLibrary ? itemPage(episode ? item.SeriesId : item.Id) : null, [
      line(episode ? (item.SeriesName || item.Name) : item.Name, h('span', { class: 'when' }, capital(relative(entry.LastActivityAt))), null),
      sub ? h('div', { class: 'line sub' }, h('span', null, sub)) : null,
    ]);
  }

  function moreRow(href) {
    return h('li', null, h('a', { class: 'row more', href }, h('span', null, 'More'), icon('chevron')));
  }

  // — Views —

  function show(...nodes) {
    main.replaceChildren(...nodes);
  }

  function heading(title, below) {
    return h('div', { class: 'title' }, h('h1', null, title), below ? h('p', null, below) : null);
  }

  function personName() {
    if (self()) return state.me.Name;
    const person = state.people.find(p => id(p.UserId) === state.person);
    return person ? person.Name : 'someone';
  }

  function pageTitle() {
    return self() ? 'Your stats' : `Stats for ${personName()}`;
  }

  function note(lines, action) {
    return h('div', { class: 'card note', role: 'status' }, lines.map(text => h('p', null, text)), action || null);
  }

  function failure(error, retry) {
    const words = error instanceof Refusal
      ? [error.message]
      : ['The server can’t be reached right now.'];
    return note(words, retry ? h('button', { type: 'button', class: 'primary', onclick: retry }, 'Try again') : null);
  }

  function placeholder() {
    return h('section', { class: 'hero placeholder', 'aria-busy': 'true' },
      h('h2', null, 'Watched'),
      h('div', { class: 'rule' }),
      figures([{ value: '–', below: 'Films' }, { value: '–', below: 'Episodes' }, { value: '–', below: 'Series finished' }]));
  }

  async function home() {
    document.title = 'JellyMold stats';
    const top = heading(pageTitle(), state.server ? `On ${state.server.ServerName}` : null);
    if (!state.summary) {
      show(top, placeholder());
      try {
        state.summary = await ask(`JellyMold/v1/Summary?recent=${LIST_SHOWN}&tz=${encodeURIComponent(state.zone)}${forPerson()}`);
      } catch (error) {
        if (error instanceof Refusal && error.status === 401) return signedOut();
        show(top, failure(error, () => home()));
        return;
      }
    }
    if (route() !== 'home') return;   // moved on while it loaded
    show(top, ...watched(state.summary));
    scrollPage(state.homeScroll);
  }

  /** The watching half of the app's page: the card, then each section the history has something for. */
  function watched(summary) {
    const parts = [];
    if (self() && state.info && state.info.You && state.info.You.Recorded === false) {
      parts.push(note([
        'Your history isn’t being recorded on this server.',
        state.info.You.IsAdmin
          ? 'Turn it on in JellyMold Companion’s settings, under Who to record.'
          : 'An administrator can turn it on in JellyMold Companion’s settings.',
      ]));
    }

    const films = summary.Films;
    const episodes = summary.Episodes;
    const series = summary.Series;
    const anything = films.Watched + films.UnderWay + episodes.Watched + series.Started > 0;

    // The card: films and episodes watched to the end, series finished; beneath, the time.
    const card = [h('h2', null, 'Watched'), h('div', { class: 'rule' })];
    if (!anything) {
      card.push(h('p', { class: 'hero-message' }, self()
        ? 'Watch something, and your stats will show up here.'
        : 'Nothing watched here yet.'));
    } else {
      card.push(figures([
        { value: number.format(films.Watched), below: 'Films' },
        { value: number.format(episodes.Watched), below: 'Episodes' },
        { value: number.format(series.Finished), below: 'Series finished' },
      ]));
      const total = films.CreditedSeconds + episodes.CreditedSeconds;
      const spent = films.ActualSeconds + episodes.ActualSeconds;
      if (total > 0 || spent > 0) {
        card.push(h('div', { class: 'rule' }));
        if (total > 0) card.push(h('p', { class: 'total' }, 'Total watch time: ', h('strong', null, figure(total))));
        if (spent > 0) {
          const since = recordingSince();
          card.push(h('p', { class: 'spent' },
            since ? `Time spent watching since ${shortDate(since)}: ` : 'Time spent watching: ',
            h('strong', null, figure(spent))));
        }
      }
    }
    parts.push(h('section', { class: 'hero', 'aria-label': 'Watched' }, card));
    if (!anything) return parts;

    const left = [];
    const right = [];

    // Days in a row with something watched, now and at best.
    const streaks = summary.Streaks;
    if (streaks && streaks.LongestDays > 0) {
      const from = streaks.LongestFrom ? day(streaks.LongestFrom) : null;
      const to = streaks.LongestTo ? day(streaks.LongestTo) : null;
      const dates = from && to && from.getTime() !== to.getTime() ? `${shortDate(from)} to ${shortDate(to)}` : null;
      right.push(section('s-streaks', 'Watching streaks', [
        stat('flame', 'Current streak', null, days(streaks.CurrentDays)),
        stat('trophy', 'Longest streak', dates, days(streaks.LongestDays)),
      ]));
    }

    // Films: the time, the latest five, and More.
    if (films.Recent.length) {
      const items = [];
      if (films.CreditedSeconds > 0 && films.AverageRuntimeSeconds && films.Longest && films.Longest.RuntimeSeconds) {
        items.push(h('li', null, figures([
          { above: 'Total', value: figure(films.CreditedSeconds), below: 'Watch time' },
          { above: 'Average', value: figure(films.AverageRuntimeSeconds), below: 'Film duration' },
          { above: 'Longest', value: figure(films.Longest.RuntimeSeconds), below: films.Longest.Item.Name },
        ], true)));
      }
      items.push(...films.Recent.map(filmRow));
      if (films.Watched + films.UnderWay > LIST_SHOWN) items.push(moreRow('#films'));
      left.push(section('s-films', 'Films', items));
    }

    // Series: the time, the latest five, and More.
    if (series.Recent.length) {
      const items = [];
      if (episodes.CreditedSeconds > 0 && episodes.AverageRuntimeSeconds && series.MostWatched) {
        items.push(h('li', null, figures([
          { above: 'Total', value: figure(episodes.CreditedSeconds), below: 'Watch time' },
          { above: 'Average', value: figure(episodes.AverageRuntimeSeconds), below: 'Episode duration' },
          { above: 'Most watched', value: figure(series.MostWatched.CreditedSeconds), below: series.MostWatched.Name },
        ], true)));
      }
      items.push(...series.Recent.map(seriesRow));
      if (series.Started > LIST_SHOWN) items.push(moreRow('#series'));
      left.push(section('s-series', 'Series', items));
    }

    // The last ten things watched, one row each.
    const seen = new Set();
    const recent = (summary.Recent || []).filter(entry => {
      const key = id(entry.Item.Id);
      if (seen.has(key)) return false;
      seen.add(key);
      return true;
    });
    if (recent.length) right.push(section('s-recent', 'Recently watched', recent.map(recentRow)));

    // Each year's watching, the latest first. One year is no comparison, so the section waits for a second.
    const years = (summary.ByYear || []).filter(y => y.Viewings > 0).sort((a, b) => b.Year - a.Year);
    if (years.length > 1) {
      right.push(section('s-years', 'By year', years.map(y => stat('calendar', String(y.Year),
        y.Viewings === 1 ? '1 film or episode watched' : `${number.format(y.Viewings)} films and episodes watched`,
        figure(y.CreditedSeconds)))));
    }

    parts.push(h('div', { class: 'columns' }, h('div', { class: 'column' }, left), h('div', { class: 'column' }, right)));
    return parts;
  }

  function recordingSince() {
    let since = null;
    if (self()) since = state.info && state.info.You && state.info.You.RecordingSince;
    else {
      const person = state.people.find(p => id(p.UserId) === state.person);
      since = person && person.RecordingSince;
    }
    return since ? new Date(since) : null;
  }

  const ORDERS = {
    films: [
      ['recent', 'Most recent', n => `${plural(n, 'film', 'films')}, most recently watched first`],
      ['title', 'A to Z', n => `${plural(n, 'film', 'films')}, A to Z`],
    ],
    series: [
      ['recent', 'Most recent', n => `${number.format(n)} series, most recently watched first`],
      ['mostWatched', 'Most episodes watched', n => `${number.format(n)} series, most episodes watched first`],
      ['mostComplete', 'Most complete', n => `${number.format(n)} series, finished first, then by how far through`],
      ['title', 'A to Z', n => `${number.format(n)} series, A to Z`],
    ],
  };
  const chosenOrder = { films: 'recent', series: 'recent' };

  /** Every film, or every series, on a page of its own, in the order chosen. */
  async function everything(kind) {
    const title = kind === 'films' ? 'Films' : 'Series';
    document.title = `${title} · JellyMold stats`;
    const back = h('a', { class: 'back', href: '#' }, icon('back'), self() ? 'Your stats' : `Stats for ${personName()}`);
    const top = heading(title, self() ? null : personName());
    show(back, top, h('p', { class: 'loading' }, 'Loading…'));
    scrollPage(0);

    const order = chosenOrder[kind];
    let rows;
    try {
      rows = await allRows(kind, order);
    } catch (error) {
      if (error instanceof Refusal && error.status === 401) return signedOut();
      show(back, top, failure(error, () => everything(kind)));
      return;
    }
    if (route() !== kind || chosenOrder[kind] !== order) return;

    const choices = ORDERS[kind];
    const describe = choices.find(c => c[0] === order)[2];
    const select = h('select', {
      onchange: event => { chosenOrder[kind] = event.target.value; everything(kind); },
    }, choices.map(([value, label]) => h('option', { value, selected: value === order }, label)));

    show(back, top, h('section', { class: `section` },
      h('div', { class: 'list-head' },
        h('h2', null, describe(rows.length)),
        h('label', { class: 'order' }, h('span', null, 'Order'), select)),
      h('ul', { class: 'list' }, rows.map(kind === 'films' ? filmRow : seriesRow))));
  }

  async function allRows(kind, order) {
    const key = `${state.person}|${kind}|${order}`;
    if (state.lists.has(key)) return state.lists.get(key);
    const endpoint = kind === 'films' ? 'Films' : 'Series';
    const rows = [];
    for (;;) {
      const page = await ask(`JellyMold/v1/${endpoint}?order=${order}&startIndex=${rows.length}&limit=500${forPerson()}`);
      rows.push(...page.Items);
      if (!page.Items.length || rows.length >= page.TotalRecordCount) break;
    }
    state.lists.set(key, rows);
    return rows;
  }

  // — Routing: the page, and a list a level deeper —

  function route() {
    const hash = location.hash.replace(/^#/, '');
    return hash === 'films' || hash === 'series' ? hash : 'home';
  }

  let lastRoute = 'home';
  function go() {
    if (!state.me) return;
    const next = route();
    if (lastRoute === 'home' && next !== 'home') state.homeScroll = scroller.scrollY;
    lastRoute = next;
    if (next === 'home') home();
    else everything(next);
  }

  window.addEventListener('hashchange', () => {
    go();
    main.focus({ preventScroll: true });
  });

  // — Signing in —

  /** Jellyfin's web client's sign-in to this server, in this browser, if it has one. Never another server's. */
  function jellyfinSignIn(serverId) {
    const credentials = readJson(localStorage, 'jellyfin_credentials');
    const servers = (credentials && Array.isArray(credentials.Servers) ? credentials.Servers : [])
      .filter(s => s && s.AccessToken && s.UserId && id(s.Id) === id(serverId));
    servers.sort((a, b) => (b.DateLastAccessed || 0) - (a.DateLastAccessed || 0));
    return servers.length ? servers[0].AccessToken : null;
  }

  /** Whether a token is a sign-in this server still honours; anything other than a refusal is an error. */
  async function tryToken(token, own) {
    try {
      state.me = await ask('Users/Me', { token, own });
      state.token = token;
      state.own = own;
      return true;
    } catch (error) {
      if (error instanceof Refusal && (error.status === 401 || error.status === 403)) return false;
      throw error;
    }
  }

  async function start() {
    try {
      state.server = await ask('System/Info/Public', { anonymous: true });
    } catch (error) {
      show(failure(error, start));
      return;
    }
    document.getElementById('server-name').textContent = state.server.ServerName || 'this server';
    document.getElementById('foot').hidden = false;

    try {
      // This page's own sign-in first: someone signed in here on purpose. Then Jellyfin's web client's.
      const own = readJson(localStorage, OWN_SIGN_IN);
      if (own && own.Token && id(own.ServerId) === id(state.server.Id)) {
        if (await tryToken(own.Token, true)) return enter();
        forget(localStorage, OWN_SIGN_IN);
      }
      const borrowed = jellyfinSignIn(state.server.Id);
      if (borrowed && await tryToken(borrowed, false)) return enter();
    } catch (error) {
      show(failure(error, start));
      return;
    }
    signIn();
  }

  function signIn() {
    state.me = null;
    state.token = null;
    document.getElementById('bar-end').hidden = true;
    document.title = 'Sign in · JellyMold stats';
    const view = document.getElementById('sign-in-template').content.cloneNode(true);
    view.querySelector('[data-server]').textContent = state.server.ServerName || 'this server';
    const form = view.querySelector('form');
    const error = view.querySelector('.form-error');
    const button = view.querySelector('button');
    form.addEventListener('submit', async event => {
      event.preventDefault();
      error.hidden = true;
      const username = form.elements.username.value.trim();
      if (!username) {
        error.textContent = 'Enter your username.';
        error.hidden = false;
        form.elements.username.focus();
        return;
      }
      button.disabled = true;
      try {
        const result = await ask('Users/AuthenticateByName', {
          method: 'POST',
          body: { Username: username, Pw: form.elements.password.value },
          token: null,
          own: true,
        });
        write(localStorage, OWN_SIGN_IN, { Token: result.AccessToken, ServerId: result.ServerId || state.server.Id });
        state.token = result.AccessToken;
        state.own = true;
        state.me = result.User;
        enter();
      } catch (failed) {
        error.textContent = failed instanceof Refusal && failed.status === 401
          ? 'That username and password didn’t work.'
          : 'The server can’t be reached right now. Try again in a moment.';
        error.hidden = false;
        button.disabled = false;
      }
    });
    show(view);
    form.elements.username.focus();
  }

  /** A sign-in the server stopped honouring part-way: back to the form. */
  function signedOut() {
    if (state.own) forget(localStorage, OWN_SIGN_IN);
    reset();
    signIn();
  }

  function reset() {
    state.summary = null;
    state.lists.clear();
    state.people = [];
    state.info = null;
  }

  async function signOut() {
    if (state.own) {
      try { await ask('Sessions/Logout', { method: 'POST' }); } catch { /* signed out here either way */ }
      forget(localStorage, OWN_SIGN_IN);
    }
    forget(sessionStorage, PERSON);
    reset();
    history.replaceState(null, '', location.pathname + location.search);
    signIn();
  }

  document.getElementById('sign-out').addEventListener('click', signOut);

  async function enter() {
    reset();
    state.person = id(state.me.Id);
    try {
      state.info = await ask('JellyMold/v1/Info');
    } catch (error) {
      if (error instanceof Refusal && error.status === 401) return signedOut();
      show(failure(error, enter));
      return;
    }

    // An admin can read anyone's: every account on the server (Jellyfin's own list, with its current names), and
    // anyone the plugin recorded whose account has since been deleted. Everyone else reads only their own.
    if (state.info.You && state.info.You.IsAdmin) {
      const [status, users] = await Promise.all([
        ask('JellyMold/v1/Admin/Status').catch(() => null),
        ask('Users').catch(() => null),
      ]);
      const people = new Map();
      for (const p of (status && status.People) || []) people.set(id(p.UserId), p);
      for (const u of users || []) people.set(id(u.Id), { ...people.get(id(u.Id)), UserId: u.Id, Name: u.Name, DeletedAt: null });
      state.people = [...people.values()];
      const chosen = read(sessionStorage, PERSON);
      if (chosen && state.people.some(p => id(p.UserId) === chosen)) state.person = chosen;
    }

    drawBar();
    lastRoute = route();
    go();
  }

  function drawBar() {
    document.getElementById('bar-end').hidden = false;
    const who = document.getElementById('who');
    who.textContent = state.me.Name;
    who.title = state.own ? `Signed in as ${state.me.Name}` : `Signed in as ${state.me.Name}, through Jellyfin`;
    document.getElementById('sign-out').hidden = !state.own;

    const picker = document.getElementById('picker');
    const select = document.getElementById('person');
    const people = state.people.slice();
    if (!people.some(p => id(p.UserId) === id(state.me.Id))) people.push({ UserId: state.me.Id, Name: state.me.Name });
    people.sort((a, b) => {
      if (id(a.UserId) === id(state.me.Id)) return -1;
      if (id(b.UserId) === id(state.me.Id)) return 1;
      return String(a.Name).localeCompare(String(b.Name), undefined, { sensitivity: 'base' });
    });
    picker.hidden = !(state.info.You && state.info.You.IsAdmin) || people.length < 2;
    select.replaceChildren(...people.map(p => h('option', { value: id(p.UserId), selected: id(p.UserId) === state.person },
      id(p.UserId) === id(state.me.Id) ? `${p.Name} (you)` : p.DeletedAt ? `${p.Name} (account deleted)` : p.Name)));
  }

  document.getElementById('person').addEventListener('change', event => {
    state.person = event.target.value;
    write(sessionStorage, PERSON, state.person);
    state.summary = null;
    state.homeScroll = 0;
    if (route() === 'home') home();
    else location.hash = '';
  });

  start();
})();
