using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Common.Api;
using MediaBrowser.Common.Configuration;
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
    public string TunerHostId { get; set; } = string.Empty;
    public TunerInfo? Tuner { get; set; }
}

public sealed class TunerInfo
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public string Kind { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    public string DeviceId { get; set; } = string.Empty;
    public string Source { get; set; } = string.Empty;
    public int TunerCount { get; set; }
    public bool Configured { get; set; }
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
    public string StreamType { get; set; } = string.Empty;
    public TunerInfo? Tuner { get; set; }
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

public sealed class DiscoveredTuner
{
    public string Url { get; set; } = string.Empty;
    public string FriendlyName { get; set; } = string.Empty;
    public string DeviceId { get; set; } = string.Empty;
    public int TunerCount { get; set; }
}

public sealed class TunerUrlRequest
{
    public string Url { get; set; } = string.Empty;
}

public sealed class TunerProbeResult
{
    public string Url { get; set; } = string.Empty;
    public bool Reachable { get; set; }
    public string FriendlyName { get; set; } = string.Empty;
    public string ModelNumber { get; set; } = string.Empty;
    public string DeviceId { get; set; } = string.Empty;
    public int TunerCount { get; set; }
    public string Message { get; set; } = string.Empty;
}

/// <summary>
/// Lists Jellyfin's open Live TV streams and closes them through IMediaSourceManager.
/// </summary>
[ApiController]
[Authorize(Policy = Policies.RequiresElevation)]
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
    private readonly ITunerHostManager _tunerHostManager;
    private readonly IConfigurationManager _configurationManager;
    private readonly ILogger<RogueStreamsController> _logger;

    public RogueStreamsController(
        IMediaSourceManager mediaSourceManager,
        ISessionManager sessionManager,
        ILiveTvManager liveTvManager,
        ITunerHostManager tunerHostManager,
        IConfigurationManager configurationManager,
        ILogger<RogueStreamsController> logger)
    {
        _mediaSourceManager = mediaSourceManager;
        _sessionManager = sessionManager;
        _liveTvManager = liveTvManager;
        _tunerHostManager = tunerHostManager;
        _configurationManager = configurationManager;
        _logger = logger;
    }

    [HttpGet("Status")]
    public async Task<ActionResult<StatusResult>> GetStatus(CancellationToken ct)
    {
        return await BuildStatusAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Uses Jellyfin's own tuner discovery to find HDHomeRun devices on the LAN.
    /// Nothing is saved here; the administrator confirms the URL in the page.
    /// </summary>
    [HttpGet("DiscoverTuners")]
    public async Task<ActionResult<List<DiscoveredTuner>>> DiscoverTuners()
    {
        var found = new List<DiscoveredTuner>();
        try
        {
            await foreach (var tuner in _tunerHostManager.DiscoverTuners(false))
            {
                if (!string.Equals(tuner.Type, "hdhomerun", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var url = NormalizeTunerUrl(tuner.Url);
                if (url is null || found.Any(t => string.Equals(t.Url, url, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                found.Add(new DiscoveredTuner
                {
                    Url = url,
                    FriendlyName = tuner.FriendlyName ?? string.Empty,
                    DeviceId = tuner.DeviceId ?? string.Empty,
                    TunerCount = tuner.TunerCount
                });
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Rogue Streams: HDHomeRun discovery failed");
        }

        found.Sort((a, b) => string.Compare(a.FriendlyName, b.FriendlyName, StringComparison.OrdinalIgnoreCase));
        return Ok(found);
    }

    /// <summary>
    /// Checks a manually entered or auto-detected URL without saving it.
    /// </summary>
    [HttpPost("TestTuner")]
    public async Task<ActionResult<TunerProbeResult>> TestTuner([FromBody] TunerUrlRequest request, CancellationToken ct)
    {
        var url = NormalizeTunerUrl(request?.Url);
        if (url is null)
        {
            return BadRequest(new { message = "Enter an http:// or https:// HDHomeRun address." });
        }

        var result = new TunerProbeResult { Url = url };
        try
        {
            using (var discover = await Http.GetAsync($"{url}/discover.json", ct).ConfigureAwait(false))
            {
                if (discover.IsSuccessStatusCode)
                {
                    var json = await discover.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                    using var doc = JsonDocument.Parse(json);
                    var root = doc.RootElement;
                    result.Reachable = true;
                    result.FriendlyName = Str(root, "FriendlyName");
                    result.ModelNumber = Str(root, "ModelNumber");
                    result.DeviceId = Str(root, "DeviceID");
                    result.TunerCount = (int)Num(root, "TunerCount");
                    result.Message = "HDHomeRun responded to /discover.json.";
                    return Ok(result);
                }

                using var status = await Http.GetAsync($"{url}/status.json", ct).ConfigureAwait(false);
                if (status.IsSuccessStatusCode)
                {
                    result.Reachable = true;
                    result.Message = "Tuner responded to /status.json, but not /discover.json. It may be an older model.";
                    return Ok(result);
                }

                result.Message = $"Tuner returned HTTP {(int)discover.StatusCode} for /discover.json and HTTP {(int)status.StatusCode} for /status.json.";
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Rogue Streams: tuner probe failed for {Url}", url);
            result.Message = $"Could not reach {url}: {ex.Message}";
        }

        return Ok(result);
    }

    /// <summary>
    /// Closes one open live stream via CloseLiveStream, repeating until it leaves
    /// the open-stream map so a leaked consumer count still releases the tuner.
    /// </summary>
    [HttpPost("Close")]
    public async Task<ActionResult<StatusResult>> Close([FromBody] CloseRequest request, CancellationToken ct)
    {
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

    private async Task<StatusResult> BuildStatusAsync(CancellationToken ct)
    {
        var result = new StatusResult();
        var configuredTuners = GetConfiguredTuners();
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

            if (!IsLiveTvSession(session) && string.IsNullOrEmpty(liveStreamId) && item is null)
            {
                continue;
            }

            if (!IsLiveTvSession(session) && string.IsNullOrEmpty(liveStreamId))
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

        var ids = EnumerateOpenLiveStreamIds();
        foreach (var session in result.Sessions)
        {
            if (!string.IsNullOrEmpty(session.LiveStreamId))
            {
                ids.Add(session.LiveStreamId!);
            }
        }

        foreach (var id in ids.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var stream = _mediaSourceManager.GetLiveStreamInfo(id);
            var source = stream?.MediaSource;
            var path = source?.Path;
            var sessionName = result.Sessions
                .FirstOrDefault(s => string.Equals(s.LiveStreamId, id, StringComparison.OrdinalIgnoreCase));
            var tuner = ResolveTuner(stream, configuredTuners);
            result.Streams.Add(new LiveStreamRow
            {
                LiveStreamId = id,
                Name = stream is not null
                    ? StreamName(source, stream)
                    : (sessionName?.Channel ?? sessionName?.ItemName ?? id),
                TunerHostId = stream?.TunerHostId ?? string.Empty,
                StreamType = stream?.GetType().Name ?? string.Empty,
                Tuner = tuner,
                ConsumerCount = stream?.ConsumerCount ?? 0,
                Path = string.IsNullOrEmpty(path) ? null : Path.GetFileName(path),
                FileBytes = TryFileLength(path),
                Viewers = result.Sessions
                    .Where(s => string.Equals(s.LiveStreamId, id, StringComparison.OrdinalIgnoreCase))
                    .Select(s => new ViewerInfo { User = s.User, Device = s.Device })
                    .ToList(),
                Recordings = stream is null
                    ? new List<string>()
                    : recordings.Where(r => RecordingMatches(r, source, stream)).Select(r => r.Name).ToList()
            });
        }

        result.Streams = result.Streams
            .OrderByDescending(s => s.InUse)
            .ThenBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var streamsById = result.Streams
            .Where(s => !string.IsNullOrEmpty(s.LiveStreamId))
            .ToDictionary(s => s.LiveStreamId, StringComparer.OrdinalIgnoreCase);
        foreach (var session in result.Sessions)
        {
            if (session.LiveStreamId is not null
                && streamsById.TryGetValue(session.LiveStreamId, out var row))
            {
                session.TunerHostId = row.TunerHostId;
                session.Tuner = row.Tuner;
            }
        }

        if (result.Streams.Count == 0)
        {
            var total = _sessionManager.Sessions.Count();
            result.Note = $"No open live streams. Jellyfin has {total} session(s); {result.Sessions.Count} look like Live TV. Playing on the web should appear here once PlaybackInfo has opened the live stream.";
        }

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
               || type.Contains("Program", StringComparison.OrdinalIgnoreCase)
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

    private List<string> EnumerateOpenLiveStreamIds()
    {
        var ids = new List<string>();
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

            if (value is not System.Collections.IDictionary dict)
            {
                continue;
            }

            var looksLikeStreamMap = field.Name.Contains("open", StringComparison.OrdinalIgnoreCase)
                || field.Name.Contains("LiveStream", StringComparison.OrdinalIgnoreCase)
                || field.Name.Contains("liveStream", StringComparison.OrdinalIgnoreCase);

            var fromField = new List<string>();
            foreach (System.Collections.DictionaryEntry entry in dict)
            {
                if (entry.Key is string key && !string.IsNullOrEmpty(key))
                {
                    fromField.Add(key);
                }
            }

            if (fromField.Count > 0 || looksLikeStreamMap)
            {
                ids.AddRange(fromField);
                if (looksLikeStreamMap)
                {
                    break;
                }
            }
        }

        if (ids.Count == 0)
        {
            _logger.LogWarning("Rogue Streams: no open-stream map keys on {Type}", type.FullName);
        }

        return ids;
    }

    private async Task FillTunersAsync(StatusResult result, CancellationToken ct)
    {
        var tunerUrl = NormalizeTunerUrl(Plugin.Instance?.Configuration.TunerUrl);
        if (tunerUrl is null)
        {
            AppendNote(result, "No HDHomeRun URL configured. Use the settings above to auto-detect or enter one.");
            return;
        }

        try
        {
            using var resp = await Http.GetAsync($"{tunerUrl}/status.json", ct).ConfigureAwait(false);
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

    private Dictionary<string, TunerInfo> GetConfiguredTuners()
    {
        var byId = new Dictionary<string, TunerInfo>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var typeNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var pair in _tunerHostManager.GetTunerHostTypes())
            {
                if (!string.IsNullOrWhiteSpace(pair.Id))
                {
                    typeNames[pair.Id] = pair.Name ?? pair.Id;
                }
            }

            var options = _configurationManager.GetConfiguration<LiveTvOptions>("livetv");
            foreach (var host in options?.TunerHosts ?? Array.Empty<TunerHostInfo>())
            {
                if (string.IsNullOrWhiteSpace(host.Id))
                {
                    continue;
                }

                byId[host.Id] = ToTunerInfo(host, typeNames);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Rogue Streams: could not read Live TV tuner configuration");
        }

        return byId;
    }

    private static TunerInfo ToTunerInfo(TunerHostInfo host, IReadOnlyDictionary<string, string> typeNames)
    {
        var type = host.Type ?? string.Empty;
        var typeName = typeNames.TryGetValue(type, out var registeredName) && !string.IsNullOrWhiteSpace(registeredName)
            ? registeredName
            : type;
        var source = host.Source ?? string.Empty;
        var name = !string.IsNullOrWhiteSpace(host.FriendlyName)
            ? host.FriendlyName
            : (!string.IsNullOrWhiteSpace(typeName) ? typeName : "Tuner");

        return new TunerInfo
        {
            Id = host.Id ?? string.Empty,
            Name = name,
            Type = type,
            Kind = TunerKind(type, typeName, source),
            Url = SafeTunerUrl(host.Url),
            DeviceId = host.DeviceId ?? string.Empty,
            Source = source,
            TunerCount = host.TunerCount,
            Configured = true
        };
    }

    private static string TunerKind(string type, string typeName, string source)
    {
        if (string.Equals(type, "hdhomerun", StringComparison.OrdinalIgnoreCase))
        {
            return "HDHomeRun";
        }

        if (string.Equals(type, "m3u", StringComparison.OrdinalIgnoreCase))
        {
            return "M3U tuner";
        }

        if (!string.IsNullOrWhiteSpace(typeName))
        {
            return typeName;
        }

        if (!string.IsNullOrWhiteSpace(source))
        {
            return source;
        }

        return "Other tuner";
    }

    private static TunerInfo? ResolveTuner(ILiveStream? stream, IReadOnlyDictionary<string, TunerInfo> configured)
    {
        var id = stream?.TunerHostId;
        if (string.IsNullOrWhiteSpace(id))
        {
            return null;
        }

        if (configured.TryGetValue(id, out var tuner))
        {
            return tuner;
        }

        return new TunerInfo
        {
            Id = id,
            Name = "Unconfigured tuner",
            Kind = "Unknown tuner",
            Configured = false
        };
    }

    private static string SafeTunerUrl(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return string.Empty;
        }

        var value = raw.Trim();
        if (Uri.TryCreate(value, UriKind.Absolute, out var uri) && !string.IsNullOrEmpty(uri.Host))
        {
            return uri.Scheme + "://" + uri.Host + (uri.IsDefaultPort ? string.Empty : ":" + uri.Port);
        }

        var fileName = Path.GetFileName(value);
        return string.IsNullOrWhiteSpace(fileName) ? "local file" : fileName;
    }

    private static void AppendNote(StatusResult result, string note)
    {
        result.Note = string.IsNullOrWhiteSpace(result.Note)
            ? note
            : result.Note + " " + note;
    }

    private static string? NormalizeTunerUrl(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var value = raw.Trim();
        if (!value.Contains("://", StringComparison.Ordinal))
        {
            value = "http://" + value;
        }

        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || string.IsNullOrEmpty(uri.Host)
            || (!string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)))
        {
            return null;
        }

        var builder = new UriBuilder(uri)
        {
            Query = string.Empty,
            Fragment = string.Empty
        };
        return builder.Uri.AbsoluteUri.TrimEnd('/');
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
