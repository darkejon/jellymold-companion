using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Threading.Channels;
using JellyMoldCompanion.Picture;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using BaseItemKind = Jellyfin.Data.Enums.BaseItemKind;

namespace Jellyfin.Plugin.JellyMoldCompanion.Jellyfin;

/// <summary>One file to measure: a version of a film, an episode or a video, with its frame size and running time.</summary>
public sealed record CropTarget(Guid ItemId, Guid MediaSourceId, string Path, int Width, int Height, TimeSpan Runtime, FileStamp? File);

/// <summary>
/// Measures the black bars built into each file's picture (design §16 idea 11) with Jellyfin's own ffmpeg, one file at a
/// time, at low priority. A file is measured a few minutes after it is added, when an app asks for one not yet measured
/// (ahead of everything else), and by the nightly task for the rest of the library.
/// </summary>
public sealed class PictureCropMeasurer : IHostedService, IDisposable
{
    public static readonly BaseItemKind[] Kinds = [BaseItemKind.Movie, BaseItemKind.Episode, BaseItemKind.Video];

    /// <summary>Jellyfin reads a new file's streams and running time first.</summary>
    private static readonly TimeSpan AfterAdding = TimeSpan.FromMinutes(5);

    /// <summary>One sample reads a single keyframe; a minute means the disk or the file is in trouble.</summary>
    private static readonly TimeSpan SampleTimeout = TimeSpan.FromMinutes(1);

    private readonly ILibraryManager _library;
    private readonly IMediaSourceManager _mediaSources;
    private readonly IMediaEncoder _encoder;
    private readonly CompanionService _companion;
    private readonly ILogger<PictureCropMeasurer> _logger;
    private readonly Channel<Guid> _asked = Channel.CreateUnbounded<Guid>();
    private readonly Channel<Guid> _added = Channel.CreateUnbounded<Guid>();
    private readonly ConcurrentDictionary<Guid, byte> _waiting = new();
    private readonly SemaphoreSlim _oneAtATime = new(1, 1);
    private readonly CancellationTokenSource _stopping = new();
    private (string Path, bool Has)? _cropdetect;
    private Task? _worker;

    public PictureCropMeasurer(ILibraryManager library, IMediaSourceManager mediaSources, IMediaEncoder encoder, CompanionService companion,
        ILogger<PictureCropMeasurer> logger)
    {
        _library = library;
        _mediaSources = mediaSources;
        _encoder = encoder;
        _companion = companion;
        _logger = logger;
    }

    /// <summary>Why nothing can be measured, in plain words. Null when all is well.</summary>
    public string? Problem { get; private set; }

    /// <summary>Items waiting to be measured, not counting the nightly pass.</summary>
    public int Waiting => _waiting.Count;

    public PictureCropQueries Queries => new(_companion.Database);

    private static bool On => CompanionService.Settings.MeasurePictureCrop;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _library.ItemAdded += OnItemAdded;
        _worker = Task.Run(() => RunAsync(_stopping.Token), CancellationToken.None);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _library.ItemAdded -= OnItemAdded;
        await _stopping.CancelAsync().ConfigureAwait(false);
        if (_worker is { } worker)
        {
            await Task.WhenAny(worker, Task.Delay(Timeout.Infinite, cancellationToken)).ConfigureAwait(false);
        }
    }

    public void Dispose()
    {
        _stopping.Dispose();
        _oneAtATime.Dispose();
    }

    // — What can be measured —

    public static bool IsMeasurable(BaseItem? item) =>
        item is not null && !item.IsVirtualItem && item.ExtraType is null && Kinds.Contains(item.GetBaseItemKind());

    /// <summary>One of an item's files: the version named, or its first.</summary>
    public CropTarget? Target(Guid itemId, Guid? mediaSourceId)
    {
        var item = _library.GetItemById(itemId);
        return IsMeasurable(item)
            ? Targets(item!).FirstOrDefault(t => mediaSourceId is not { } id || id == Guid.Empty || t.MediaSourceId == id)
            : null;
    }

    /// <summary>
    /// An item's files that ffmpeg can open directly: not a disc folder or image, not a stream from elsewhere, and with a
    /// video stream Jellyfin has already read the size of.
    /// </summary>
    public IReadOnlyList<CropTarget> Targets(BaseItem item)
    {
        var targets = new List<CropTarget>();
        foreach (var source in _mediaSources.GetStaticMediaSources(item, false))
        {
            if (source.Protocol != MediaProtocol.File || string.IsNullOrEmpty(source.Path) || !Guid.TryParse(source.Id, out var id)
                || source.VideoType is { } type && type != VideoType.VideoFile)
            {
                continue;
            }

            var video = source.MediaStreams?.FirstOrDefault(s => s.Type == MediaStreamType.Video);
            if (video is not { Width: > 0, Height: > 0 })
            {
                continue;
            }

            targets.Add(new CropTarget(item.Id, id, source.Path, video.Width!.Value, video.Height!.Value,
                TimeSpan.FromTicks(source.RunTimeTicks ?? item.RunTimeTicks ?? 0), Stamp(source.Path)));
        }

        return targets;
    }

    /// <summary>What an app is given for a file: the plugin's measurement of it, or an app's report of it, or nothing yet.</summary>
    public PictureCrop? Current(CropTarget target) => CropRules.Choose(Queries.ForSource(target.MediaSourceId), target.File);

    private static FileStamp? Stamp(string path)
    {
        try
        {
            var file = new FileInfo(path);
            return file.Exists ? new FileStamp(path, file.Length, new DateTimeOffset(file.LastWriteTimeUtc)) : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    // — The queue —

    /// <summary>An app asked for an item not yet measured: measure it next, ahead of new arrivals. False when measuring is switched off.</summary>
    public bool AskFor(Guid itemId)
    {
        if (!On)
        {
            return false;
        }

        if (_waiting.TryAdd(itemId, 0))
        {
            _asked.Writer.TryWrite(itemId);
        }

        return true;
    }

    private void OnItemAdded(object? sender, ItemChangeEventArgs e)
    {
        if (On && IsMeasurable(e.Item))
        {
            _ = MeasureSoon(e.Item.Id);
        }
    }

    private async Task MeasureSoon(Guid itemId)
    {
        try
        {
            await Task.Delay(AfterAdding, _stopping.Token).ConfigureAwait(false);
            if (_waiting.TryAdd(itemId, 0))
            {
                _added.Writer.TryWrite(itemId);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task RunAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                if (!_asked.Reader.TryRead(out var itemId) && !_added.Reader.TryRead(out itemId))
                {
                    await Task.WhenAny(_asked.Reader.WaitToReadAsync(token).AsTask(), _added.Reader.WaitToReadAsync(token).AsTask())
                        .ConfigureAwait(false);
                    continue;
                }

                _waiting.TryRemove(itemId, out _);
                if (On && _library.GetItemById(itemId) is { } item)
                {
                    await Measure(item, token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                return;
            }
            catch (Exception e)
            {
                _logger.LogError(e, "JellyMold Companion could not measure black bars");
            }
        }
    }

    // — Measuring —

    /// <summary>Measures each of an item's files that needs it. How many were measured.</summary>
    public async Task<int> Measure(BaseItem item, CancellationToken token)
    {
        var measured = 0;
        if (!_companion.Recording.IsRecording)
        {
            // The history is open for reading only (the settings page says why), so nothing measured could be kept.
            return measured;
        }

        foreach (var target in Targets(item))
        {
            var existing = Queries.ForSource(target.MediaSourceId).FirstOrDefault(c => c.Source == CropSources.Server);
            if (target.Runtime <= TimeSpan.Zero || !CropRules.NeedsMeasuring(existing, target.File, DateTimeOffset.UtcNow))
            {
                continue;
            }

            await _oneAtATime.WaitAsync(token).ConfigureAwait(false);
            try
            {
                if (!await CanMeasure(token).ConfigureAwait(false))
                {
                    return measured;
                }

                var crop = await MeasureFile(target, token).ConfigureAwait(false);
                await _companion.Recording.Write(s =>
                {
                    PictureCrops.Save(s, crop);
                    return true;
                }).ConfigureAwait(false);
                measured++;
            }
            finally
            {
                _oneAtATime.Release();
            }
        }

        return measured;
    }

    private async Task<PictureCrop> MeasureFile(CropTarget target, CancellationToken token)
    {
        var clock = Stopwatch.StartNew();
        var points = new List<IReadOnlyList<Bars>>();
        var unread = 0;
        foreach (var at in CropRules.SamplePoints(target.Runtime))
        {
            var run = await Run(CropRules.FfmpegArguments(target.Path, at), token).ConfigureAwait(false);
            if (run is not { Exit: 0 } ran)
            {
                unread++;
                points.Add([]);
                continue;
            }

            points.Add(CropRules.Frames(ran.Errors, target.Width, target.Height));
        }

        var (outcome, bars, samples) = CropRules.Combine(points, target.Width, target.Height);
        if (outcome == CropOutcomes.Unmeasurable && unread > CropRules.Points / 2)
        {
            outcome = CropOutcomes.Failed;
        }

        _logger.LogInformation(
            "JellyMold Companion measured {Path}: {Outcome}, bars {Top}/{Bottom}/{Left}/{Right} of {Width}x{Height}, {Samples} of {Points} points, {Seconds:0.0} s",
            target.Path, outcome, bars.Top, bars.Bottom, bars.Left, bars.Right, target.Width, target.Height, samples, points.Count,
            clock.Elapsed.TotalSeconds);

        return new PictureCrop
        {
            ItemId = target.ItemId,
            MediaSourceId = target.MediaSourceId,
            Source = CropSources.Server,
            Outcome = outcome,
            Width = target.Width,
            Height = target.Height,
            Bars = bars,
            File = target.File,
            Samples = samples,
            MeasuredAt = DateTimeOffset.UtcNow,
        };
    }

    /// <summary>Whether Jellyfin's ffmpeg has the filter that finds black bars. Asked once for each ffmpeg it is set to.</summary>
    private async Task<bool> CanMeasure(CancellationToken token)
    {
        var path = _encoder.EncoderPath;
        if (string.IsNullOrEmpty(path))
        {
            Problem = "Jellyfin has no ffmpeg set (Dashboard → Playback → Transcoding), so black bars are not measured.";
            return false;
        }

        if (_cropdetect is not { } known || known.Path != path)
        {
            var run = await Run(["-hide_banner", "-filters"], token).ConfigureAwait(false);
            known = (path, run is { Exit: 0 } ran && ran.Output.Contains(" cropdetect ", StringComparison.Ordinal));
            _cropdetect = known;
        }

        Problem = known.Has ? null : $"Jellyfin's ffmpeg ({path}) has no cropdetect filter, so black bars are not measured.";
        return known.Has;
    }

    private sealed record Ran(int Exit, string Output, string Errors);

    /// <summary>Runs Jellyfin's ffmpeg below normal priority. Null if it could not be started or took too long.</summary>
    private async Task<Ran?> Run(IReadOnlyList<string> arguments, CancellationToken token)
    {
        var start = new ProcessStartInfo(_encoder.EncoderPath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = start };
        try
        {
            process.Start();
        }
        catch (Win32Exception e)
        {
            _logger.LogWarning(e, "JellyMold Companion could not start ffmpeg at {Path}", start.FileName);
            return null;
        }

        try
        {
            process.PriorityClass = ProcessPriorityClass.BelowNormal;
        }
        catch (Exception e) when (e is InvalidOperationException or Win32Exception or PlatformNotSupportedException)
        {
            // It has finished already, or this system does not allow it. Either way it runs on one thread.
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(SampleTimeout);
        var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var errors = process.StandardError.ReadToEndAsync(timeout.Token);
        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            return new Ran(process.ExitCode, await output.ConfigureAwait(false), await errors.ConfigureAwait(false));
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
            }

            token.ThrowIfCancellationRequested();
            _logger.LogWarning("JellyMold Companion gave up on ffmpeg after {Seconds} s: {Arguments}", SampleTimeout.TotalSeconds,
                string.Join(' ', arguments));
            return null;
        }
    }
}
