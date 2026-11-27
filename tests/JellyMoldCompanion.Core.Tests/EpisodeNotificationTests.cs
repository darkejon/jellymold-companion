using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using JellyMoldCompanion.Notifications;

namespace JellyMoldCompanion.Tests;

/// <summary>Design §16 idea 16: telling JellyMold apps about new episodes of the shows somebody asked to hear about.</summary>
public class EpisodeNotificationTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 27, 20, 0, 0, TimeSpan.Zero);
    private static readonly Guid Series = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid Other = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private const string ServerId = "0123456789abcdef0123456789abcdef";

    private static DateTimeOffset At(double seconds) => Start.AddSeconds(seconds);

    // — Batching —

    [Fact]
    public void AnEpisodeGoesOnceItsSeriesHasBeenQuietForNinetySeconds()
    {
        var batches = new EpisodeBatches();
        batches.Add(Series, Guid.NewGuid(), At(0));
        Assert.Empty(batches.Ready(At(89)));
        Assert.Single(batches.Ready(At(90)));
    }

    [Fact]
    public void ASeasonArrivingOverAScanIsOneBatch()
    {
        var batches = new EpisodeBatches();
        for (var i = 0; i < 8; i++)
        {
            batches.Add(Series, Guid.NewGuid(), At(i * 20));
            Assert.Empty(batches.Ready(At(i * 20 + 10)));
        }

        var batch = Assert.Single(batches.Ready(At(140 + 90)));
        Assert.Equal(8, batch.Episodes.Count);
    }

    [Fact]
    public void ALongScanStillSendsTenMinutesAfterTheFirstEpisode()
    {
        var batches = new EpisodeBatches();
        for (var t = 0; t < 600; t += 60)
        {
            batches.Add(Series, Guid.NewGuid(), At(t));
        }

        Assert.Empty(batches.Ready(At(599)));
        Assert.True(Assert.Single(batches.Ready(At(600))).PastLongest(At(600)));
    }

    [Fact]
    public void EachSeriesIsABatchOfItsOwn()
    {
        var batches = new EpisodeBatches();
        batches.Add(Series, Guid.NewGuid(), At(0));
        batches.Add(Other, Guid.NewGuid(), At(60));
        Assert.Equal([Series], batches.Ready(At(100)).Select(b => b.Series));
        Assert.Equal([Series, Other], batches.Ready(At(150)).Select(b => b.Series));
    }

    [Fact]
    public void EpisodesArrivingWhileABatchIsSentWaitForTheNext()
    {
        var batches = new EpisodeBatches();
        var first = Guid.NewGuid();
        batches.Add(Series, first, At(0));
        var ready = Assert.Single(batches.Ready(At(90)));
        var late = Guid.NewGuid();
        batches.Add(Series, late, At(91));
        batches.Done(Series, ready.Episodes.Keys);

        Assert.Equal(1, batches.Count);
        Assert.Empty(batches.Ready(At(150)));
        Assert.Equal([late], Assert.Single(batches.Ready(At(181))).Episodes.Keys);
    }

    [Fact]
    public void AnEpisodeSeenAgainKeepsItsFirstArrival()
    {
        var batches = new EpisodeBatches();
        var episode = Guid.NewGuid();
        batches.Add(Series, episode, At(0));
        batches.Add(Series, episode, At(80));
        Assert.Single(batches.Ready(At(90)));
    }

    // — What goes —

    private static SeriesFacts Show(string? tmdb = "580", string? tvdb = "72073") => new(Series, "Star Trek: Deep Space Nine", tmdb, tvdb);

    private static EpisodeFacts Episode(int? season, int? number, string name = "Cardassians") => new(Guid.NewGuid(), season, number, name);

    [Fact]
    public void ASeriesWithEitherIdGoesAtOnce()
    {
        Assert.Equal(BatchVerdict.Send, EpisodeNotice.Decide(Show(), [Episode(2, 5)], pastLongest: false));
        Assert.Equal(BatchVerdict.Send, EpisodeNotice.Decide(Show(tvdb: null), [Episode(2, 5)], pastLongest: false));
        Assert.Equal(BatchVerdict.Send, EpisodeNotice.Decide(Show(tmdb: null), [Episode(2, 5)], pastLongest: false));
    }

    [Fact]
    public void ASeriesWithNeitherIdIsHeldForItsMetadata()
    {
        // A new series' metadata may not have arrived yet.
        Assert.Equal(BatchVerdict.Hold, EpisodeNotice.Decide(Show(tmdb: null, tvdb: "tt-not-an-id"), [Episode(1, 1)], pastLongest: false));
        Assert.Equal(BatchVerdict.Hold, EpisodeNotice.Decide(Show(tmdb: null, tvdb: null), [Episode(1, 1)], pastLongest: false));

        // Ten minutes on, it still has none: nothing goes.
        Assert.Equal(BatchVerdict.Drop, EpisodeNotice.Decide(Show(tmdb: null, tvdb: null), [Episode(1, 1)], pastLongest: true));
    }

    [Fact]
    public void GoneEpisodesOrAGoneSeriesSendNothing()
    {
        Assert.Equal(BatchVerdict.Drop, EpisodeNotice.Decide(Show(), [], pastLongest: false));
        Assert.Equal(BatchVerdict.Drop, EpisodeNotice.Decide(null, [Episode(1, 1)], pastLongest: false));
    }

    [Theory]
    [InlineData("580", "580")]
    [InlineData(" 580 ", "580")]
    [InlineData("0", null)]
    [InlineData("000", null)]
    [InlineData("tt0106145", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    [InlineData("1234567890123", null)]
    public void IdsAreDigits(string? given, string? taken)
    {
        Assert.Equal(taken, EpisodeNotice.Id(given));
    }

    [Fact]
    public void OnlyShowsSomebodyWantsAreSent()
    {
        var wanted = new WantedShows(new HashSet<string> { "580" }, new HashSet<string> { "81189" });
        Assert.True(wanted.Wants(Show()));
        Assert.True(wanted.Wants(Show(tmdb: "999", tvdb: "81189")));
        Assert.False(wanted.Wants(Show(tmdb: "999", tvdb: "123")));
        Assert.False(wanted.Wants(Show(tmdb: null, tvdb: null)));
        Assert.False(WantedShows.None.Wants(Show()));
    }

    [Fact]
    public void TheBodyIsTheShowAndItsEpisodesInOrder()
    {
        var body = JsonNode.Parse(EpisodeNotice.Body(Show(), [Episode(2, 6, "Melora"), Episode(2, 5), Episode(null, null, "Extra")]))!;
        Assert.Equal("EPISODES_ADDED", (string?)body["type"]);
        Assert.Equal("Star Trek: Deep Space Nine", (string?)body["series"]!["name"]);
        Assert.Equal("580", (string?)body["series"]!["tmdbId"]);
        Assert.Equal("72073", (string?)body["series"]!["tvdbId"]);
        Assert.Equal(Series.ToString("N"), (string?)body["series"]!["jellyfinId"]);

        var episodes = body["episodes"]!.AsArray();
        Assert.Equal(["Cardassians", "Melora", "Extra"], episodes.Select(e => (string?)e!["name"]));
        Assert.Equal(2, (int?)episodes[0]!["season"]);
        Assert.Equal(5, (int?)episodes[0]!["episode"]);
        Assert.Equal(32, ((string?)episodes[0]!["jellyfinId"])!.Length);

        // An unknown number is left out, not sent as nothing.
        Assert.Null(episodes[2]!["season"]);
        Assert.False(episodes[2]!.AsObject().ContainsKey("season"));
    }

    [Fact]
    public void AShowWithoutATvdbIdSendsNone()
    {
        var series = JsonNode.Parse(EpisodeNotice.Body(Show(tvdb: null), [Episode(1, 1)]))!["series"]!.AsObject();
        Assert.False(series.ContainsKey("tvdbId"));
    }

    [Fact]
    public void ABatchOfMoreThanTwoHundredKeepsItsEarliestTwoHundred()
    {
        // A whole long-running series added at once: the relay reads 200 episodes of a request.
        var episodes = Enumerable.Range(0, 400).Select(n => Episode(n / 100 + 1, n % 100 + 1, $"Episode title {n}")).Reverse().ToList();
        var body = EpisodeNotice.Body(Show(), episodes);
        Assert.True(Encoding.UTF8.GetByteCount(body) <= EpisodeNotice.MaxBodyBytes);

        var sent = JsonNode.Parse(body)!["episodes"]!.AsArray();
        Assert.Equal(200, sent.Count);
        Assert.Equal((1, 1), ((int)sent[0]!["season"]!, (int)sent[0]!["episode"]!));
        Assert.Equal((2, 100), ((int)sent[^1]!["season"]!, (int)sent[^1]!["episode"]!));
    }

    [Fact]
    public void TitlesTooLongForTheRelayLeaveOutTheLatestEpisodes()
    {
        var episodes = Enumerable.Range(1, 200).Select(n => Episode(1, n, new string('x', 400))).ToList();
        var body = EpisodeNotice.Body(Show(), episodes);
        Assert.True(Encoding.UTF8.GetByteCount(body) <= EpisodeNotice.MaxBodyBytes);

        var sent = JsonNode.Parse(body)!["episodes"]!.AsArray();
        Assert.InRange(sent.Count, 100, 199);
        Assert.Equal(1, (int?)sent[0]!["episode"]);
    }

    // — The relay —

    private const string Secret = "5ec7e75ec7e75ec7e75ec7e75ec7e75ec7e75ec7e75ec7e75ec7e75ec7e7aaaa";
    private const string NewSecret = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

    private sealed class MemoryStore : IRelayInstanceStore
    {
        public RelayInstance? Instance { get; set; }

        public RelayInstance? Load() => Instance;

        public void Save(RelayInstance instance) => Instance = instance;
    }

    /// <summary>A relay with one instance, which forgets it on demand, as a reset relay does.</summary>
    private sealed class FakeRelay : HttpMessageHandler
    {
        public List<(HttpMethod Method, string Path, string? Auth, string? Body)> Requests { get; } = [];

        public HashSet<string> Known { get; } = [];

        public string WantedJson { get; set; } = """{"tmdb":["580"],"tvdb":[]}""";

        public string? CreateError { get; set; }

        /// <summary>Refuses every secret, even one it has just handed out, as a relay that keeps resetting would.</summary>
        public bool RefuseAll { get; set; }

        private int _made;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            var auth = request.Headers.Authorization?.Parameter;
            var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add((request.Method, path, auth, body));

            if (path.EndsWith("/instances", StringComparison.Ordinal))
            {
                if (CreateError is not null)
                {
                    return Json($$"""{"error":"{{CreateError}}"}""");
                }

                var secret = _made++ == 0 ? Secret : NewSecret;
                Known.Add(secret);
                return Json($$"""{"instanceId":"{{new string((char)('a' + _made), 32)}}","secret":"{{secret}}"}""");
            }

            if (RefuseAll || auth is null || !Known.Contains(auth))
            {
                return new HttpResponseMessage(HttpStatusCode.Unauthorized) { Content = new StringContent("Unauthorised") };
            }

            return path.EndsWith("/shows", StringComparison.Ordinal) ? Json(WantedJson) : Json("""{"sent":2,"failed":0}""");
        }

        private static HttpResponseMessage Json(string json) =>
            new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    }

    private static (RelaySession Session, FakeRelay Relay, MemoryStore Store, Func<DateTimeOffset, DateTimeOffset> SetClock) Relay()
    {
        var relay = new FakeRelay();
        var store = new MemoryStore();
        var now = Start;
        var client = new RelayClient(new HttpClient(relay), new Uri("https://relay.example"));
        return (new RelaySession(client, ServerId, store, () => now), relay, store, t => now = t);
    }

    [Fact]
    public async Task TheFirstAskMakesAnInstanceAndKeepsIt()
    {
        var (session, relay, store, _) = Relay();
        var wanted = await session.WantedAsync(CancellationToken.None);

        Assert.Equal(["580"], wanted.Tmdb);
        Assert.Equal(Secret, store.Instance!.Secret);
        Assert.Equal((HttpMethod.Post, $"/v1/servers/{ServerId}/instances"), (relay.Requests[0].Method, relay.Requests[0].Path));
        Assert.Null(relay.Requests[0].Auth);
        Assert.Equal($"/v1/hook/{ServerId}/{store.Instance.InstanceId}/shows", relay.Requests[1].Path);
        Assert.Equal(Secret, relay.Requests[1].Auth);
    }

    [Fact]
    public async Task AKeptInstanceIsUsedAgain()
    {
        var (session, relay, store, _) = Relay();
        store.Instance = new RelayInstance(new string('c', 32), Secret);
        relay.Known.Add(Secret);

        await session.SendAsync("{}", CancellationToken.None);
        Assert.DoesNotContain(relay.Requests, r => r.Path.EndsWith("/instances", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TheWantedShowsAreAskedForAtMostEveryFiveMinutes()
    {
        var (session, relay, _, setClock) = Relay();
        await session.WantedAsync(CancellationToken.None);
        setClock(Start.AddMinutes(4));
        await session.WantedAsync(CancellationToken.None);
        Assert.Single(relay.Requests, r => r.Path.EndsWith("/shows", StringComparison.Ordinal));

        setClock(Start.AddMinutes(5));
        await session.WantedAsync(CancellationToken.None);
        Assert.Equal(2, relay.Requests.Count(r => r.Path.EndsWith("/shows", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task AResetRelayGetsANewInstanceAndOneRetry()
    {
        var (session, relay, store, _) = Relay();
        await session.WantedAsync(CancellationToken.None);
        relay.Known.Clear();

        var (sent, failed) = await session.SendAsync("""{"type":"EPISODES_ADDED"}""", CancellationToken.None);

        Assert.Equal((2, 0), (sent, failed));
        Assert.Equal(NewSecret, store.Instance!.Secret);
        Assert.Equal(2, relay.Requests.Count(r => r.Path.EndsWith("/instances", StringComparison.Ordinal)));
        Assert.Equal("""{"type":"EPISODES_ADDED"}""", relay.Requests[^1].Body);
        Assert.Equal(NewSecret, relay.Requests[^1].Auth);
    }

    [Fact]
    public async Task ARelayThatKeepsRefusingIsNotGivenInstanceAfterInstance()
    {
        var (session, relay, _, setClock) = Relay();
        await session.WantedAsync(CancellationToken.None);
        relay.Known.Clear();
        relay.Requests.Clear();

        // Remade once and refused again: the one retry fails, and no further instance is made for half an hour.
        relay.RefuseAll = true;
        await Assert.ThrowsAnyAsync<RelayException>(() => session.SendAsync("{}", CancellationToken.None));
        setClock(Start.AddMinutes(10));
        await Assert.ThrowsAnyAsync<RelayException>(() => session.SendAsync("{}", CancellationToken.None));
        Assert.Equal(1, relay.Requests.Count(r => r.Path.EndsWith("/instances", StringComparison.Ordinal)));

        // Half an hour on, it tries again.
        setClock(Start.AddMinutes(31));
        await Assert.ThrowsAnyAsync<RelayException>(() => session.SendAsync("{}", CancellationToken.None));
        Assert.Equal(2, relay.Requests.Count(r => r.Path.EndsWith("/instances", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task ARelayThatWillNotMakeAnInstanceSaysWhy()
    {
        var (session, relay, store, _) = Relay();
        relay.CreateError = "Too many webhooks for this server";
        var error = await Assert.ThrowsAnyAsync<RelayException>(() => session.WantedAsync(CancellationToken.None));
        Assert.Contains("Too many webhooks", error.Message);
        Assert.Null(store.Instance);
    }

    [Fact]
    public async Task TheSecretIsInNoErrorAndNoAddress()
    {
        var (session, relay, store, _) = Relay();
        await session.WantedAsync(CancellationToken.None);
        relay.Known.Clear();
        var refused = await Record.ExceptionAsync(() => new RelayClient(new HttpClient(relay), new Uri("https://relay.example"))
            .SendAsync(ServerId, store.Instance!, "{}", CancellationToken.None));

        Assert.IsType<RelayUnauthorizedException>(refused);
        Assert.DoesNotContain(Secret, refused!.ToString());
        Assert.DoesNotContain(Secret, store.Instance!.ToString());
        Assert.All(relay.Requests, r => Assert.DoesNotContain(Secret, r.Path));
    }

    [Fact]
    public async Task WantedShowsKeepOnlyIds()
    {
        var (session, relay, _, _) = Relay();
        relay.WantedJson = """{"tmdb":["580", 1396, "tt0106145", "", null],"tvdb":["72073","0"]}""";
        var wanted = await session.WantedAsync(CancellationToken.None);
        Assert.Equal(["1396", "580"], wanted.Tmdb.Order());
        Assert.Equal(["72073"], wanted.Tvdb);
    }

    [Fact]
    public void TheInstanceFileIsTheServerAccountsAlone()
    {
        var folder = Directory.CreateTempSubdirectory("jmc-relay-");
        try
        {
            var path = Path.Combine(folder.FullName, "relay.json");
            var file = new RelayInstanceFile(path);
            Assert.Null(file.Load());

            file.Save(new RelayInstance(new string('d', 32), Secret));
            Assert.Equal(Secret, new RelayInstanceFile(path).Load()!.Secret);
            if (!OperatingSystem.IsWindows())
            {
                Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
            }

            File.WriteAllText(path, "not json");
            Assert.Null(file.Load());
        }
        finally
        {
            folder.Delete(recursive: true);
        }
    }

    [Theory]
    [InlineData(ServerId, true)]
    [InlineData("abc", false)]
    [InlineData("../../v1/hook", false)]
    [InlineData(null, false)]
    public void TheServerIdIsCheckedBeforeItIsUsedInAnAddress(string? serverId, bool ok)
    {
        Assert.Equal(ok, RelayClient.IsServerId(serverId));
    }
}
