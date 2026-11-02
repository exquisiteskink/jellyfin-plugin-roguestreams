using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.LiveTv;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.RogueStreams;

public sealed class TunerLease
{
    public string Resource { get; set; } = string.Empty;
    public string Channel { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string TargetIp { get; set; } = string.Empty;
    public long NetworkRate { get; set; }
}

public sealed class LiveSessionInfo
{
    public string User { get; set; } = string.Empty;
    public string Device { get; set; } = string.Empty;
    public string Client { get; set; } = string.Empty;
    public string PlayMethod { get; set; } = string.Empty;
    public string Channel { get; set; } = string.Empty;
    public string ItemName { get; set; } = string.Empty;
    public string? LiveStreamId { get; set; }
}

public sealed class RecordingInfo
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Channel { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
}

public sealed class ViewerInfo
{
    public string User { get; set; } = string.Empty;
    public string Device { get; set; } = string.Empty;
}

public sealed class LiveStreamRow
{
    public string LiveStreamId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string TunerHostId { get; set; } = string.Empty;
    public int ConsumerCount { get; set; }
    public string? Path { get; set; }
    public long? FileBytes { get; set; }
    public List<ViewerInfo> Viewers { get; set; } = new();
    public List<string> Recordings { get; set; } = new();
    public bool InUse => Viewers.Count > 0 || Recordings.Count > 0;
}

public sealed class StatusResult
{
    public List<LiveStreamRow> Streams { get; set; } = new();
    public List<TunerLease> Tuners { get; set; } = new();
    public List<LiveSessionInfo> Sessions { get; set; } = new();
    public List<RecordingInfo> Recordings { get; set; } = new();
    public string? Note { get; set; }
}

public sealed class CloseRequest
{
    public string LiveStreamId { get; set; } = string.Empty;
}

/// <summary>
/// Lists Jellyfin's open Live TV streams and closes them through IMediaSourceManager.
/// </summary>
[ApiController]
[Authorize]
[Route("RogueStreams")]
public class RogueStreamsController : ControllerBase
{
    private const int MaxClosePasses = 16;
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(5) };
    private static readonly BindingFlags StreamMapFlags =
        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    private readonly IMediaSourceManager _mediaSourceManager;
    private readonly ISessionManager _sessionManager;
    private readonly ILiveTvManager _liveTvManager;
    private readonly ILogger<RogueStreamsController> _logger;

    public RogueStreamsController(
        IMediaSourceManager mediaSourceManager,
        ISessionManager sessionManager,
        ILiveTvManager liveTvManager,
        ILogger<RogueStreamsController> logger)
    {
        _mediaSourceManager = mediaSourceManager;
        _sessionManager = sessionManager;
        _liveTvManager = liveTvManager;
        _logger = logger;
    }

    [HttpGet("Status")]
    public async Task<ActionResult<StatusResult>> GetStatus(CancellationToken ct)
    {
        if (!IsAdmin())
        {
            return Forbid();
        }

        return await BuildStatusAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Closes one open live stream via CloseLiveStream, repeating until it leaves
    /// the open-stream map so a leaked consumer count still releases the tuner.
    /// </summary>
    [HttpPost("Close")]
    public async Task<ActionResult<StatusResult>> Close([FromBody] CloseRequest request, CancellationToken ct)
    {
        if (!IsAdmin())
        {
            return Forbid();
        }

        var id = request.LiveStreamId?.Trim();
        if (string.IsNullOrEmpty(id))
        {
            return BadRequest(new { message = "liveStreamId is required." });
        }

        if (_mediaSourceManager.GetLiveStreamInfo(id) is null)
        {
            return NotFound(new { message = "That live stream is not open." });
        }

        try
        {
            for (var i = 0; i < MaxClosePasses; i++)
            {
                if (_mediaSourceManager.GetLiveStreamInfo(id) is null)
                {
                    break;
                }

                await _mediaSourceManager.CloseLiveStream(id).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Rogue Streams: failed to close live stream {LiveStreamId}", id);
            return Conflict(new { message = $"Could not close stream: {ex.Message}" });
        }

        if (_mediaSourceManager.GetLiveStreamInfo(id) is not null)
        {
            _logger.LogWarning("Rogue Streams: live stream {LiveStreamId} still open after {Passes} CloseLiveStream calls", id, MaxClosePasses);
            return Conflict(new { message = "Jellyfin still has this stream open after repeated CloseLiveStream calls. A process restart may be required." });
        }

        _logger.LogWarning("Rogue Streams: closed live stream {LiveStreamId}", id);
        await Task.Delay(TimeSpan.FromSeconds(1), ct).ConfigureAwait(false);
        return await BuildStatusAsync(ct).ConfigureAwait(false);
    }

    private bool IsAdmin()
    {
        return User?.Identity?.IsAuthenticated == true
               && (User!.IsInRole("Administrator") || User.HasClaim("IsAdministrator", "true"));
    }

    private async Task<StatusResult> BuildStatusAsync(CancellationToken ct)
    {
        var result = new StatusResult();
        var recordings = await GetActiveRecordingsAsync(ct).ConfigureAwait(false);
        result.Recordings = recordings;

        foreach (var session in _sessionManager.Sessions)
        {
            var item = session.NowPlayingItem;
            var liveStreamId = session.PlayState?.LiveStreamId;
            if (item is null && string.IsNullOrEmpty(liveStreamId))
            {
                continue;
            }

            if (!IsLiveTvSession(session))
            {
                continue;
            }

            result.Sessions.Add(new LiveSessionInfo
            {
                User = session.UserName ?? "?",
                Device = session.DeviceName ?? "?",
                Client = session.Client ?? "?",
                PlayMethod = session.PlayState?.PlayMethod?.ToString() ?? "?",
                Channel = ChannelLabel(item),
                ItemName = item?.Name ?? string.Empty,
                LiveStreamId = string.IsNullOrEmpty(liveStreamId) ? null : liveStreamId
            });
        }

        var open = EnumerateOpenLiveStreams();
        if (open is null)
        {
            result.Note = "Could not enumerate Jellyfin's open live-stream map; listing sessions and tuners only. Close still works if you paste a liveStreamId from a session.";
            open = Array.Empty<KeyValuePair<string, ILiveStream>>();
        }

        foreach (var entry in open)
        {
            var stream = entry.Value;
            var source = stream.MediaSource;
            var path = source?.Path;
            result.Streams.Add(new LiveStreamRow
            {
                LiveStreamId = entry.Key,
                Name = StreamName(source, stream),
                TunerHostId = stream.TunerHostId ?? string.Empty,
                ConsumerCount = stream.ConsumerCount,
                Path = string.IsNullOrEmpty(path) ? null : Path.GetFileName(path),
                FileBytes = TryFileLength(path),
                Viewers = result.Sessions
                    .Where(s => string.Equals(s.LiveStreamId, entry.Key, StringComparison.OrdinalIgnoreCase))
                    .Select(s => new ViewerInfo { User = s.User, Device = s.Device })
                    .ToList(),
                Recordings = recordings
                    .Where(r => RecordingMatches(r, source, stream))
                    .Select(r => r.Name)
                    .ToList()
            });
        }

        result.Streams = result.Streams
            .OrderByDescending(s => s.InUse)
            .ThenBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        await FillTunersAsync(result, ct).ConfigureAwait(false);
        return result;
    }

    private static bool IsLiveTvSession(SessionInfo session)
    {
        if (!string.IsNullOrEmpty(session.PlayState?.LiveStreamId))
        {
            return true;
        }

        var item = session.NowPlayingItem;
        if (item is null)
        {
            return false;
        }

        var type = item.Type.ToString();
        return type.Contains("TvChannel", StringComparison.OrdinalIgnoreCase)
               || type.Contains("LiveTv", StringComparison.OrdinalIgnoreCase)
               || item.ChannelId != Guid.Empty;
    }

    private static string ChannelLabel(MediaBrowser.Model.Dto.BaseItemDto? item)
    {
        if (item is null)
        {
            return "?";
        }

        if (!string.IsNullOrWhiteSpace(item.ChannelNumber) && !string.IsNullOrWhiteSpace(item.ChannelName))
        {
            return item.ChannelNumber + " " + item.ChannelName;
        }

        return item.ChannelName
               ?? item.ChannelNumber
               ?? item.Name
               ?? "?";
    }

    private static string StreamName(MediaBrowser.Model.Dto.MediaSourceInfo? source, ILiveStream stream)
    {
        if (!string.IsNullOrWhiteSpace(source?.Name))
        {
            return source!.Name;
        }

        if (!string.IsNullOrWhiteSpace(source?.Path))
        {
            return Path.GetFileName(source!.Path) ?? stream.UniqueId;
        }

        return stream.UniqueId;
    }

    private static bool RecordingMatches(RecordingInfo recording, MediaBrowser.Model.Dto.MediaSourceInfo? source, ILiveStream stream)
    {
        if (!string.IsNullOrEmpty(recording.Channel) && !string.IsNullOrEmpty(source?.Name)
            && source.Name.Contains(recording.Channel, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!string.IsNullOrEmpty(recording.Id)
            && (stream.OriginalStreamId?.Contains(recording.Id, StringComparison.OrdinalIgnoreCase) == true
                || source?.Id?.Contains(recording.Id, StringComparison.OrdinalIgnoreCase) == true))
        {
            return true;
        }

        return !string.IsNullOrEmpty(recording.Channel)
               && stream.OriginalStreamId?.Contains(recording.Channel, StringComparison.OrdinalIgnoreCase) == true;
    }

    private async Task<List<RecordingInfo>> GetActiveRecordingsAsync(CancellationToken ct)
    {
        try
        {
            var timers = await _liveTvManager.GetTimers(
                new TimerQuery { IsActive = true },
                ct).ConfigureAwait(false);

            return (timers.Items ?? [])
                .Where(t => t.Status == RecordingStatus.InProgress)
                .Select(t => new RecordingInfo
                {
                    Id = t.Id ?? string.Empty,
                    Name = t.Name ?? "Recording",
                    Channel = t.ChannelName
                               ?? (t.ChannelId == Guid.Empty ? string.Empty : t.ChannelId.ToString("N")),
                    Status = t.Status.ToString()
                })
                .ToList();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Rogue Streams: could not list active recordings");
            return new List<RecordingInfo>();
        }
    }

    private IReadOnlyList<KeyValuePair<string, ILiveStream>>? EnumerateOpenLiveStreams()
    {
        var manager = _mediaSourceManager;
        var type = manager.GetType();

        foreach (var field in type.GetFields(StreamMapFlags))
        {
            object? value;
            try
            {
                value = field.GetValue(manager);
            }
            catch
            {
                continue;
            }

            if (value is ConcurrentDictionary<string, ILiveStream> typed)
            {
                return typed.ToArray();
            }
        }

        foreach (var field in type.GetFields(StreamMapFlags))
        {
            object? value;
            try
            {
                value = field.GetValue(manager);
            }
            catch
            {
                continue;
            }

            if (value is not System.Collections.IDictionary dict)
            {
                continue;
            }

            var list = new List<KeyValuePair<string, ILiveStream>>();
            foreach (System.Collections.DictionaryEntry entry in dict)
            {
                if (entry.Key is string key && entry.Value is ILiveStream stream)
                {
                    list.Add(new KeyValuePair<string, ILiveStream>(key, stream));
                }
            }

            if (list.Count > 0 || field.Name.Contains("open", StringComparison.OrdinalIgnoreCase))
            {
                return list;
            }
        }

        _logger.LogWarning("Rogue Streams: no open-stream map found on {Type}", type.FullName);
        return null;
    }

    private async Task FillTunersAsync(StatusResult result, CancellationToken ct)
    {
        var tunerUrl = Plugin.Instance?.Configuration.TunerUrl;
        if (string.IsNullOrWhiteSpace(tunerUrl))
        {
            return;
        }

        try
        {
            using var resp = await Http.GetAsync($"{tunerUrl.TrimEnd('/')}/status.json", ct).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();
            var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            foreach (var el in JsonDocument.Parse(json).RootElement.EnumerateArray())
            {
                var number = Str(el, "VctNumber");
                if (string.IsNullOrWhiteSpace(number) || number.Equals("none", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                result.Tuners.Add(new TunerLease
                {
                    Resource = Str(el, "Resource"),
                    Channel = number,
                    Name = Str(el, "VctName"),
                    TargetIp = Str(el, "TargetIP"),
                    NetworkRate = Num(el, "NetworkRate")
                });
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Rogue Streams: tuner status unavailable at {Url}", tunerUrl);
        }
    }

    private static long? TryFileLength(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return null;
        }

        try
        {
            var info = new FileInfo(path);
            return info.Exists ? info.Length : null;
        }
        catch
        {
            return null;
        }
    }

    private static string Str(JsonElement el, string name)
    {
        if (!el.TryGetProperty(name, out var v))
        {
            return string.Empty;
        }

        return v.ValueKind == JsonValueKind.String
            ? v.GetString() ?? string.Empty
            : v.ValueKind == JsonValueKind.Number ? v.ToString() : string.Empty;
    }

    private static long Num(JsonElement el, string name)
    {
        return el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n) ? n : 0;
    }
}
