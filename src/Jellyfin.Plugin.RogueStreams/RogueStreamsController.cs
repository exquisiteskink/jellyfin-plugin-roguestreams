using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Session;
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
    public string PlayMethod { get; set; } = string.Empty;
    public string Channel { get; set; } = string.Empty;
}

public sealed class TranscodeInfo
{
    public int Pid { get; set; }
    public string Channel { get; set; } = string.Empty;
    public double RunningSeconds { get; set; }
    public bool Rogue { get; set; }
    public bool Protected { get; set; }
}

public sealed class StatusResult
{
    public List<TunerLease> Tuners { get; set; } = new();
    public List<LiveSessionInfo> Sessions { get; set; } = new();
    public List<TranscodeInfo> Transcodes { get; set; } = new();
    public List<string> ProtectedChannels { get; set; } = new();
    public List<TunerLease> UnmatchedLeases { get; set; } = new();
    public bool UnresolvedViewer { get; set; }
    public string? Refusal { get; set; }
    public int GraceSeconds { get; set; }
}

/// <summary>
/// Enumerates and kills individual Live TV transcodes.
/// </summary>
[ApiController]
[Authorize]
[Route("RogueStreams")]
public class RogueStreamsController : ControllerBase
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(5) };

    // .../native/v8.1  or  .../auto/v19.1 — the channel as Jellyfin numbers it.
    private static readonly Regex ChannelInUrl = new(@"/v([0-9]+(?:\.[0-9]+)?)(?![0-9.])", RegexOptions.Compiled);

    private readonly ISessionManager _sessionManager;
    private readonly ILogger<RogueStreamsController> _logger;

    public RogueStreamsController(ISessionManager sessionManager, ILogger<RogueStreamsController> logger)
    {
        _sessionManager = sessionManager;
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
    /// Kills one transcode. Re-validates that the pid is still an orphan right now;
    /// a pid that belongs to someone playing is refused even if the UI asked for it.
    /// </summary>
    [HttpPost("Kill/{pid:int}")]
    public async Task<ActionResult<StatusResult>> Kill(int pid, CancellationToken ct)
    {
        if (!IsAdmin())
        {
            return Forbid();
        }

        var status = await BuildStatusAsync(ct).ConfigureAwait(false);

        if (status.Refusal is not null)
        {
            return Conflict(new { message = status.Refusal });
        }

        var target = status.Transcodes.FirstOrDefault(t => t.Pid == pid);
        if (target is null)
        {
            return NotFound(new { message = $"No transcode with pid {pid} is running." });
        }

        if (!target.Rogue)
        {
            return Conflict(new { message = $"pid {pid} is on channel {target.Channel}, which is protected by a live session or recording. Not killing it." });
        }

        try
        {
            using var proc = Process.GetProcessById(pid);
            proc.Kill();
            _logger.LogWarning("Rogue Streams: killed orphaned live TV transcode pid {Pid} on channel {Channel}", pid, target.Channel);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Rogue Streams: failed to kill pid {Pid}", pid);
            return Conflict(new { message = $"Could not kill pid {pid}: {ex.Message}" });
        }

        await Task.Delay(TimeSpan.FromSeconds(2), ct).ConfigureAwait(false);
        return await BuildStatusAsync(ct).ConfigureAwait(false);
    }

    private bool IsAdmin()
    {
        return User?.Identity?.IsAuthenticated == true
               && (User!.IsInRole("Administrator") || User.HasClaim("IsAdministrator", "true"));
    }

    private async Task<StatusResult> BuildStatusAsync(CancellationToken ct)
    {
        var result = new StatusResult { GraceSeconds = Plugin.Instance?.Configuration.GraceSeconds ?? 20 };

        // 1. Who is legitimately watching, by channel.
        foreach (var session in _sessionManager.Sessions)
        {
            var item = session.NowPlayingItem;
            if (item is null)
            {
                continue;
            }

            var number = item.ChannelNumber;
            var isLive = item.IsLive == true || item.ChannelId is not null;

            if (!isLive)
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(number))
            {
                // A live viewer whose channel Jellyfin will not name: nothing may be killed.
                result.UnresolvedViewer = true;
                number = "?";
            }
            else
            {
                result.ProtectedChannels.Add(number!);
            }

            result.Sessions.Add(new LiveSessionInfo
            {
                User = session.UserName ?? "?",
                Device = session.DeviceName ?? "?",
                PlayMethod = session.PlayState?.PlayMethod?.ToString() ?? "?",
                Channel = number ?? "?"
            });
        }

        result.ProtectedChannels = result.ProtectedChannels.Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        if (result.UnresolvedViewer)
        {
            result.Refusal = "A live session is playing but Jellyfin does not report its channel, so no transcode can be proven to be nobody's.";
        }

        // 2. What the tuners say is locked.
        var tunerUrl = Plugin.Instance?.Configuration.TunerUrl ?? "";
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
                    Channel = number!,
                    Name = Str(el, "VctName"),
                    TargetIp = Str(el, "TargetIP"),
                    NetworkRate = Num(el, "NetworkRate")
                });
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Rogue Streams: tuner status unavailable at {Url}", tunerUrl);
            result.Refusal ??= $"Tuner at {tunerUrl} did not answer, so leases cannot be verified.";
        }

        // 3. Which transcodes are running, and on what channel.
        foreach (var proc in Process.GetProcesses())
        {
            try
            {
                if (!proc.ProcessName.Contains("ffmpeg", StringComparison.OrdinalIgnoreCase))
                {
                    proc.Dispose();
                    continue;
                }

                var cmdline = ReadCmdline(proc.Id);
                if (cmdline is null)
                {
                    proc.Dispose();
                    continue;
                }

                var m = ChannelInUrl.Match(cmdline);
                if (!m.Success)
                {
                    proc.Dispose();
                    continue;
                }

                var channel = m.Groups[1].Value;
                var age = (DateTime.Now - proc.StartTime).TotalSeconds;

                result.Transcodes.Add(new TranscodeInfo
                {
                    Pid = proc.Id,
                    Channel = channel,
                    RunningSeconds = Math.Round(age, 1),
                    Protected = result.ProtectedChannels.Contains(channel, StringComparer.OrdinalIgnoreCase),
                    Rogue = !result.ProtectedChannels.Contains(channel, StringComparer.OrdinalIgnoreCase)
                });
                proc.Dispose();
            }
            catch
            {
                proc.Dispose();
            }
        }

        result.Transcodes = result.Transcodes.OrderBy(t => t.Channel).ToList();

        // 4. Tuner leases nobody accounts for: context for the operator, not killable here.
        result.UnmatchedLeases = result.Tuners
            .Where(t => !result.ProtectedChannels.Contains(t.Channel, StringComparer.OrdinalIgnoreCase))
            .ToList();

        return result;
    }

    private static string? ReadCmdline(int pid)
    {
        try
        {
            var raw = System.IO.File.ReadAllText($"/proc/{pid}/cmdline").Replace('\0', ' ');
            return raw.Length == 0 ? null : raw;
        }
        catch
        {
            return null;
        }
    }

    private static string Str(JsonElement el, string name)
    {
        return el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() ?? string.Empty
            : v.ValueKind == JsonValueKind.Number ? v.ToString() : string.Empty;
    }

    private static long Num(JsonElement el, string name)
    {
        return el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n) ? n : 0;
    }
}
