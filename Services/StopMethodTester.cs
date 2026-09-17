using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Queries;
using Jellyfin.Plugin.KidsLimit.Configuration;
using Jellyfin.Plugin.KidsLimit.State;
using MediaBrowser.Controller.Devices;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Session;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.KidsLimit.Services;

/// <summary>
/// A bench of every distinct way this plugin can make a client stop playing, exposed one
/// lever at a time so a parent standing in front of the TV can fire each in isolation and
/// report which ones the Android TV client actually honors.
///
/// <para>
/// This exists because REQUIREMENTS.md §2.1 ("does the TV honor Stop?") was answered once,
/// by hand, for the Jellyfin 10.x client — and the answer is client-version-specific. The
/// production path (<see cref="PlaybackTerminator"/>) fires several levers at once, which
/// is right for enforcement and useless for diagnosis: when playback stops you cannot tell
/// which lever did it, and when it doesn't you cannot tell which one failed. Every method
/// here is deliberately a *single* mechanism, and the runner records a per-step outcome so
/// a rejected command is distinguishable from a command that was accepted and ignored.
/// </para>
///
/// <para>
/// Nothing here is wired into enforcement. It is reachable only through the parent-token
/// test API, and every state-changing method is undone by <see cref="ReleaseId"/>.
/// </para>
/// </summary>
public sealed class StopMethodTester
{
    /// <summary>The method id that undoes every other method's lasting effect.</summary>
    public const string ReleaseId = "release";

    private const long TicksPerSecond = 10_000_000L;

    private static readonly IReadOnlyList<StopMethodInfo> MethodCatalog = BuildCatalog();

    private readonly ISessionManager _sessionManager;
    private readonly ITranscodeManager _transcodeManager;
    private readonly IDeviceManager _deviceManager;
    private readonly HardBlockEnforcer _enforcer;
    private readonly PlaybackTerminator _terminator;
    private readonly StateStore _store;
    private readonly ILogger<StopMethodTester> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="StopMethodTester"/> class.
    /// </summary>
    /// <param name="sessionManager">Session manager.</param>
    /// <param name="transcodeManager">Transcode manager.</param>
    /// <param name="deviceManager">Device manager (per-device logout).</param>
    /// <param name="enforcer">Hard-block enforcer (policy-level methods).</param>
    /// <param name="terminator">The production terminator, offered as one of the methods.</param>
    /// <param name="store">Daily state store (manual-stop flag).</param>
    /// <param name="logger">Logger.</param>
    public StopMethodTester(
        ISessionManager sessionManager,
        ITranscodeManager transcodeManager,
        IDeviceManager deviceManager,
        HardBlockEnforcer enforcer,
        PlaybackTerminator terminator,
        StateStore store,
        ILogger<StopMethodTester> logger)
    {
        _sessionManager = sessionManager;
        _transcodeManager = transcodeManager;
        _deviceManager = deviceManager;
        _enforcer = enforcer;
        _terminator = terminator;
        _store = store;
        _logger = logger;
    }

    /// <summary>
    /// Gets the catalog of testable stop methods, in the order the test page shows them.
    /// </summary>
    public static IReadOnlyList<StopMethodInfo> Methods => MethodCatalog;

    /// <summary>
    /// Looks a method up by id.
    /// </summary>
    /// <param name="id">The method id.</param>
    /// <returns>The method, or <c>null</c> if the id is unknown.</returns>
    public static StopMethodInfo? Find(string? id) =>
        string.IsNullOrEmpty(id)
            ? null
            : MethodCatalog.FirstOrDefault(m => string.Equals(m.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Runs one stop method against a user.
    /// </summary>
    /// <param name="methodId">The method id (see <see cref="Methods"/>).</param>
    /// <param name="userId">The target user's Guid.</param>
    /// <param name="config">Plugin configuration (needed to reconcile policy blocks).</param>
    /// <returns>A per-step report of what the server attempted and how it went.</returns>
    public async Task<StopAttemptResult> RunAsync(string methodId, Guid userId, PluginConfiguration config)
    {
        var method = Find(methodId);
        var result = new StopAttemptResult
        {
            Method = method?.Id ?? methodId,
            Title = method?.Title ?? methodId,
            StartedUtc = DateTime.UtcNow,
        };

        if (method is null)
        {
            result.Summary = $"Unknown method '{methodId}'.";
            return result;
        }

        // Snapshot the targets up front: several methods end sessions, and re-reading
        // ISessionManager.Sessions mid-run would then silently target nothing.
        var sessions = _sessionManager.Sessions
            .Where(s => s.UserId == userId)
            .ToList();
        var playing = sessions.Where(s => s.NowPlayingItem is not null).ToList();

        result.SessionsSeen = sessions.Count;
        result.SessionsPlaying = playing.Count;
        result.Targets = playing
            .Select(s => $"{s.Client} {s.ApplicationVersion} on {s.DeviceName}")
            .ToList();

        if (method.NeedsSession && playing.Count == 0)
        {
            result.Summary = "Nothing is playing for this user right now — start playback first.";
            return result;
        }

        try
        {
            await DispatchAsync(method, userId, playing, config, result).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // A test lever must never be able to take the server down with it.
            _logger.LogWarning(ex, "KidsLimit: stop-method test '{Method}' threw.", method.Id);
            result.Steps.Add(new StopAttemptStep { Name = "unhandled", Ok = false, Error = Describe(ex) });
        }

        result.FinishedUtc = DateTime.UtcNow;
        if (string.IsNullOrEmpty(result.Summary))
        {
            var ok = result.Steps.Count(s => s.Ok);
            result.Summary = result.Steps.Count == 0
                ? "Nothing was attempted."
                : $"{ok}/{result.Steps.Count} step(s) accepted by the server. {method.WatchFor}";
        }

        _logger.LogInformation(
            "KidsLimit: stop-method test '{Method}' for user {User}: {Summary}",
            method.Id,
            userId,
            result.Summary);

        return result;
    }

    private static IReadOnlyList<StopMethodInfo> BuildCatalog() => new List<StopMethodInfo>
    {
        // ---- Group A: does the remote-control channel work at all? ----------------
        new()
        {
            Id = "ping",
            Group = "1. Is the client listening?",
            Title = "Show a message on the TV",
            Description =
                "Sends DisplayMessage. Does not stop anything. If this text appears on the TV, " +
                "the server→client command channel is alive and any Stop failure is the client " +
                "ignoring Stop specifically — not a dead websocket.",
            WatchFor = "Did a toast/banner appear on the TV?",
            Severity = "probe",
            NeedsSession = true,
        },

        // ---- Group B: playstate commands ----------------------------------------
        new()
        {
            Id = "stop",
            Group = "2. Playstate commands",
            Title = "Stop (one command)",
            Description = "A single PlaystateCommand.Stop — the polite ask, and what production has always sent first.",
            WatchFor = "Playback should end and the player should close within ~2 s.",
            Severity = "command",
            NeedsSession = true,
        },
        new()
        {
            Id = "stop-burst",
            Group = "2. Playstate commands",
            Title = "Stop × 5 (burst)",
            Description =
                "Five Stop commands 800 ms apart. Distinguishes \"client ignores Stop\" from " +
                "\"client dropped one message\" — e.g. a command that lands while the player is " +
                "still initialising.",
            WatchFor = "Does it stop on a later command rather than the first?",
            Severity = "command",
            NeedsSession = true,
        },
        new()
        {
            Id = "pause",
            Group = "2. Playstate commands",
            Title = "Pause",
            Description = "PlaystateCommand.Pause. Some clients honor Pause even where they ignore Stop; a paused kid is a stopped kid.",
            WatchFor = "Does the picture freeze?",
            Severity = "command",
            NeedsSession = true,
        },
        new()
        {
            Id = "seek-end",
            Group = "2. Playstate commands",
            Title = "Seek to the end",
            Description =
                "PlaystateCommand.Seek to 3 s before the runtime. Ends the item the way finishing " +
                "it does. Beware: a client with auto-next-episode on may roll straight into the " +
                "next episode, which is itself a useful result.",
            WatchFor = "Does it jump to the end and exit — or start the next episode?",
            Severity = "command",
            NeedsSession = true,
        },

        // ---- Group C: general (navigation) commands ------------------------------
        new()
        {
            Id = "gohome",
            Group = "3. Navigation commands",
            Title = "Go home",
            Description =
                "GeneralCommandType.GoHome. A different message type from Stop, handled by a " +
                "different part of the client; navigating away from the player usually tears " +
                "playback down as a side effect.",
            WatchFor = "Does the TV jump back to the home screen?",
            Severity = "command",
            NeedsSession = true,
        },
        new()
        {
            Id = "back",
            Group = "3. Navigation commands",
            Title = "Back",
            Description = "GeneralCommandType.Back — the equivalent of pressing Back on the remote, which normally exits the player.",
            WatchFor = "Does the player exit as if Back was pressed?",
            Severity = "command",
            NeedsSession = true,
        },
        new()
        {
            Id = "stop-then-gohome",
            Group = "3. Navigation commands",
            Title = "Stop, then Go home",
            Description =
                "Stop, wait 1.5 s, then GoHome. Covers the client that stops the stream but leaves " +
                "a dead player on screen (which a kid just presses play on again).",
            WatchFor = "Playback ends AND the player screen goes away.",
            Severity = "command",
            NeedsSession = true,
        },

        // ---- Group D: server-side stream teardown --------------------------------
        new()
        {
            Id = "kill-transcode",
            Group = "4. Pull the stream out from under it",
            Title = "Kill the transcode job",
            Description =
                "Kills the device's ffmpeg job and deletes its segments — no client cooperation " +
                "needed. Only bites when the stream is transcoded or remuxed; a direct-played file " +
                "keeps going. Check the diagnostics above: if PlayMethod is DirectPlay, expect " +
                "nothing to happen.",
            WatchFor = "Playback should stall then die once the client's buffer drains (a few seconds).",
            Severity = "server",
            NeedsSession = true,
        },
        new()
        {
            Id = "close-livestream",
            Group = "4. Pull the stream out from under it",
            Title = "Close the live stream",
            Description = "CloseLiveStreamIfNeededAsync. Only relevant for Live TV / a live-stream-backed source.",
            WatchFor = "Live TV playback should die.",
            Severity = "server",
            NeedsSession = true,
        },
        new()
        {
            Id = "production",
            Group = "4. Pull the stream out from under it",
            Title = "Everything production sends today",
            Description =
                "The current PlaybackTerminator path: Stop, then Pause if Stop was rejected, then " +
                "kill transcode + close live stream. This is the baseline that is reportedly not " +
                "working on Jellyfin 12 — run it to confirm.",
            WatchFor = "Whatever the plugin normally does at the limit.",
            Severity = "server",
            NeedsSession = true,
        },

        // ---- Group E: kill the session itself ------------------------------------
        new()
        {
            Id = "end-session",
            Group = "5. Kill the session",
            Title = "Report session ended",
            Description =
                "ReportSessionEnded — the server forgets the session and disposes its controllers, " +
                "closing the websocket. The client's next progress report re-creates a session, so " +
                "this is only a stop if the client reacts badly to losing its socket.",
            WatchFor = "Does playback stop, or does it carry on and silently re-register?",
            Severity = "session",
            NeedsSession = true,
        },
        new()
        {
            Id = "close-session",
            Group = "5. Kill the session",
            Title = "Close session if needed",
            Description = "CloseIfNeededAsync — the server's own \"this session is done\" path, gentler than the above.",
            WatchFor = "Usually nothing visible; worth one data point.",
            Severity = "session",
            NeedsSession = true,
        },
        new()
        {
            Id = "logout-device",
            Group = "5. Kill the session",
            Title = "Log this device out ⚠",
            Description =
                "Deletes the TV's access token, so every subsequent request 401s. The most certain " +
                "server-side stop there is — and the kid must be logged back in by hand afterwards. " +
                "Release does NOT undo this. Test it last.",
            WatchFor = "Playback should die and the app should drop to a login screen.",
            Severity = "nuclear",
            NeedsSession = true,
        },

        // ---- Group F: policy blocks ----------------------------------------------
        new()
        {
            Id = "block-playback",
            Group = "6. Policy blocks (undo with Release)",
            Title = "Disable media playback",
            Description =
                "Flips the user's EnableMediaPlayback policy off, so PlaybackInfo refuses to hand " +
                "out media sources. Stops anything *new* (including auto-play of the next episode); " +
                "an in-flight direct play may run to the end of the current item.",
            WatchFor = "Press play on something else — it should refuse. Does the current item die too?",
            Severity = "policy",
            NeedsSession = false,
        },
        new()
        {
            Id = "block-schedule",
            Group = "6. Policy blocks (undo with Release)",
            Title = "Access schedule → never allowed",
            Description =
                "Drives the user's access schedule to Everyday 00:00–00:00. Jellyfin re-validates " +
                "the schedule on every single request, so even an in-flight direct play dies at its " +
                "next range request. The most forceful reversible lever — it also locks the kid out " +
                "of Jellyfin entirely while it is on.",
            WatchFor = "Playback should die within seconds, even for a direct-played file.",
            Severity = "policy",
            NeedsSession = false,
        },
        new()
        {
            Id = "block-schedule-kill",
            Group = "6. Policy blocks (undo with Release)",
            Title = "Access schedule + kill stream",
            Description =
                "The schedule block plus an immediate transcode kill and Stop command. If any " +
                "combination stops a Jellyfin 12 Android TV client, this is the one — it is the " +
                "candidate for the new production path.",
            WatchFor = "Playback should die within seconds in every play mode.",
            Severity = "policy",
            NeedsSession = false,
        },

        // ---- Undo ------------------------------------------------------------------
        new()
        {
            Id = ReleaseId,
            Group = "Undo",
            Title = "Release everything",
            Description =
                "Clears the test hold, the parent \"Stop now\" flag and any policy block, restoring " +
                "the user's original schedule and playback permission. Does not undo a device " +
                "logout (nothing can — log the TV back in).",
            WatchFor = "The kid can log in and play again.",
            Severity = "reset",
            NeedsSession = false,
        },
    };

    private static string Describe(Exception ex) => ex.GetType().Name + ": " + ex.Message;

    private async Task DispatchAsync(
        StopMethodInfo method,
        Guid userId,
        IReadOnlyList<SessionInfo> playing,
        PluginConfiguration config,
        StopAttemptResult result)
    {
        switch (method.Id)
        {
            case "ping":
                foreach (var s in playing)
                {
                    await StepAsync(result, $"DisplayMessage → {s.DeviceName}", () =>
                        _sessionManager.SendMessageCommand(
                            null,
                            s.Id,
                            new MessageCommand
                            {
                                Header = "Kids Limit test",
                                Text = "If you can read this, the TV is listening to the server.",
                                TimeoutMs = 8000,
                            },
                            CancellationToken.None)).ConfigureAwait(false);
                }

                break;

            case "stop":
                foreach (var s in playing)
                {
                    await PlaystateAsync(result, s, PlaystateCommand.Stop).ConfigureAwait(false);
                }

                break;

            case "stop-burst":
                for (var i = 1; i <= 5; i++)
                {
                    foreach (var s in playing)
                    {
                        await PlaystateAsync(result, s, PlaystateCommand.Stop, $" #{i}").ConfigureAwait(false);
                    }

                    if (i < 5)
                    {
                        await Task.Delay(800).ConfigureAwait(false);
                    }
                }

                break;

            case "pause":
                foreach (var s in playing)
                {
                    await PlaystateAsync(result, s, PlaystateCommand.Pause).ConfigureAwait(false);
                }

                break;

            case "seek-end":
                foreach (var s in playing)
                {
                    var runtime = s.NowPlayingItem?.RunTimeTicks;
                    if (runtime is null or <= 0)
                    {
                        result.Steps.Add(new StopAttemptStep
                        {
                            Name = $"Seek → {s.DeviceName}",
                            Ok = false,
                            Error = "Item has no runtime, cannot compute an end position.",
                        });
                        continue;
                    }

                    var target = Math.Max(0, runtime.Value - (3 * TicksPerSecond));
                    await PlaystateAsync(result, s, PlaystateCommand.Seek, " (end)", target).ConfigureAwait(false);
                }

                break;

            case "gohome":
                foreach (var s in playing)
                {
                    await GeneralAsync(result, s, GeneralCommandType.GoHome).ConfigureAwait(false);
                }

                break;

            case "back":
                foreach (var s in playing)
                {
                    await GeneralAsync(result, s, GeneralCommandType.Back).ConfigureAwait(false);
                }

                break;

            case "stop-then-gohome":
                foreach (var s in playing)
                {
                    await PlaystateAsync(result, s, PlaystateCommand.Stop).ConfigureAwait(false);
                }

                await Task.Delay(1500).ConfigureAwait(false);
                foreach (var s in playing)
                {
                    await GeneralAsync(result, s, GeneralCommandType.GoHome).ConfigureAwait(false);
                }

                break;

            case "kill-transcode":
                foreach (var s in playing)
                {
                    await KillTranscodeAsync(result, s).ConfigureAwait(false);
                }

                break;

            case "close-livestream":
                foreach (var s in playing)
                {
                    var liveStreamId = s.PlayState?.LiveStreamId;
                    if (string.IsNullOrEmpty(liveStreamId))
                    {
                        result.Steps.Add(new StopAttemptStep
                        {
                            Name = $"CloseLiveStream → {s.DeviceName}",
                            Ok = false,
                            Error = "This session holds no live stream (not Live TV) — nothing to close.",
                        });
                        continue;
                    }

                    await StepAsync(result, $"CloseLiveStream → {s.DeviceName}", () =>
                        _sessionManager.CloseLiveStreamIfNeededAsync(liveStreamId, s.Id)).ConfigureAwait(false);
                }

                break;

            case "production":
                foreach (var s in playing)
                {
                    await StepAsync(result, $"PlaybackTerminator → {s.DeviceName}", () =>
                        _terminator.StopSessionAsync(s)).ConfigureAwait(false);
                }

                break;

            case "end-session":
                foreach (var s in playing)
                {
                    await StepAsync(result, $"ReportSessionEnded → {s.DeviceName}", () =>
                        _sessionManager.ReportSessionEnded(s.Id).AsTask()).ConfigureAwait(false);
                }

                break;

            case "close-session":
                foreach (var s in playing)
                {
                    await StepAsync(result, $"CloseIfNeeded → {s.DeviceName}", () =>
                        _sessionManager.CloseIfNeededAsync(s)).ConfigureAwait(false);
                }

                break;

            case "logout-device":
                await LogoutDevicesAsync(result, userId, playing).ConfigureAwait(false);
                break;

            case "block-playback":
                await StepAsync(result, "Policy: EnableMediaPlayback = false", () =>
                    _enforcer.TestBlockAsync(userId, PluginConfiguration.ModeDisablePlayback)).ConfigureAwait(false);
                break;

            case "block-schedule":
                await StepAsync(result, "Policy: access schedule → never", () =>
                    _enforcer.TestBlockAsync(userId, PluginConfiguration.ModeAccessSchedule)).ConfigureAwait(false);
                break;

            case "block-schedule-kill":
                await StepAsync(result, "Policy: access schedule → never", () =>
                    _enforcer.TestBlockAsync(userId, PluginConfiguration.ModeAccessSchedule)).ConfigureAwait(false);
                foreach (var s in playing)
                {
                    await PlaystateAsync(result, s, PlaystateCommand.Stop).ConfigureAwait(false);
                    await KillTranscodeAsync(result, s).ConfigureAwait(false);
                }

                break;

            case ReleaseId:
                await ReleaseAsync(result, userId, config).ConfigureAwait(false);
                break;

            default:
                result.Summary = $"Method '{method.Id}' has no implementation.";
                break;
        }
    }

    private async Task ReleaseAsync(StopAttemptResult result, Guid userId, PluginConfiguration config)
    {
        var local = DateTime.Now;
        var today = LimitCalculator.DateKey(local);
        var userIdN = userId.ToString("N", CultureInfo.InvariantCulture);

        await StepAsync(result, "Clear parent \"Stop now\" flag", () =>
        {
            _store.SetManualStop(userIdN, today, false);
            return Task.CompletedTask;
        }).ConfigureAwait(false);

        await StepAsync(result, "Release policy block + test hold", () =>
            _enforcer.TestReleaseAsync(userId)).ConfigureAwait(false);

        // Reconcile last so anything the *real* limits still require is re-applied rather
        // than left off — Release must not become a backdoor that grants free watch time.
        await StepAsync(result, "Reconcile against real limits", () =>
            _enforcer.ReconcileAsync(config, today, local)).ConfigureAwait(false);

        result.Summary = "Released. Any block that the kid's real limits still call for has been re-applied.";
    }

    private async Task LogoutDevicesAsync(
        StopAttemptResult result,
        Guid userId,
        IReadOnlyList<SessionInfo> playing)
    {
        foreach (var s in playing)
        {
            if (string.IsNullOrEmpty(s.DeviceId))
            {
                continue;
            }

            // Scope the query to this user *and* this device: a shared TV may hold tokens
            // for several accounts, and we must only log out the one that is playing.
            var devices = _deviceManager.GetDevices(new DeviceQuery
            {
                UserId = userId,
                DeviceId = s.DeviceId,
            });

            if (devices.Items.Count == 0)
            {
                result.Steps.Add(new StopAttemptStep
                {
                    Name = $"Logout → {s.DeviceName}",
                    Ok = false,
                    Error = "No stored device/token matched this session's device id.",
                });
                continue;
            }

            foreach (var device in devices.Items)
            {
                await StepAsync(result, $"Logout → {s.DeviceName} ({device.AppName})", () =>
                    _sessionManager.Logout(device)).ConfigureAwait(false);
            }
        }
    }

    private Task KillTranscodeAsync(StopAttemptResult result, SessionInfo session)
    {
        if (string.IsNullOrEmpty(session.DeviceId))
        {
            result.Steps.Add(new StopAttemptStep
            {
                Name = "KillTranscodingJobs",
                Ok = false,
                Error = "Session has no device id.",
            });
            return Task.CompletedTask;
        }

        var direct = session.TranscodingInfo is null;
        var name = $"KillTranscodingJobs → {session.DeviceName}" +
                   (direct ? " (no transcode running — likely DirectPlay)" : string.Empty);

        // Delete-files predicate: always clean up segment files, same as production.
        return StepAsync(result, name, () =>
            _transcodeManager.KillTranscodingJobs(session.DeviceId, null, _ => true));
    }

    private Task PlaystateAsync(
        StopAttemptResult result,
        SessionInfo session,
        PlaystateCommand command,
        string suffix = "",
        long? seekTicks = null)
    {
        var name = $"{command}{suffix} → {session.DeviceName}";
        if (!session.SupportsRemoteControl)
        {
            // Worth recording rather than skipping: SendPlaystateCommand throws for these,
            // and "the server refused to even try" is a different diagnosis from "the TV
            // ignored us".
            name += " [session reports SupportsRemoteControl=false]";
        }

        return StepAsync(result, name, () =>
            _sessionManager.SendPlaystateCommand(
                null,
                session.Id,
                new PlaystateRequest { Command = command, SeekPositionTicks = seekTicks },
                CancellationToken.None));
    }

    private Task GeneralAsync(StopAttemptResult result, SessionInfo session, GeneralCommandType type)
    {
        var supported = session.SupportedCommands?.Contains(type) ?? false;
        var name = $"{type} → {session.DeviceName}" +
                   (supported ? string.Empty : " [client did not advertise this command]");

        return StepAsync(result, name, () =>
            _sessionManager.SendGeneralCommand(
                null,
                session.Id,
                new GeneralCommand { Name = type },
                CancellationToken.None));
    }

    private async Task StepAsync(StopAttemptResult result, string name, Func<Task> action)
    {
        var step = new StopAttemptStep { Name = name };
        try
        {
            await action().ConfigureAwait(false);
            step.Ok = true;
        }
        catch (Exception ex)
        {
            step.Ok = false;
            step.Error = Describe(ex);
            _logger.LogWarning(ex, "KidsLimit: test step '{Step}' failed.", name);
        }

        result.Steps.Add(step);
    }
}

/// <summary>
/// One testable way of stopping playback: what it does, what the tester should watch for,
/// and how disruptive it is.
/// </summary>
public sealed class StopMethodInfo
{
    /// <summary>Gets the stable id used by the API and the test page.</summary>
    public string Id { get; init; } = string.Empty;

    /// <summary>Gets the heading this method is listed under.</summary>
    public string Group { get; init; } = string.Empty;

    /// <summary>Gets the short button label.</summary>
    public string Title { get; init; } = string.Empty;

    /// <summary>Gets the explanation of the mechanism and its known limits.</summary>
    public string Description { get; init; } = string.Empty;

    /// <summary>Gets the observation the tester should make on the TV afterwards.</summary>
    public string WatchFor { get; init; } = string.Empty;

    /// <summary>
    /// Gets how disruptive the method is: <c>probe</c>, <c>command</c>, <c>server</c>,
    /// <c>session</c>, <c>policy</c>, <c>nuclear</c> or <c>reset</c>. The page colours
    /// buttons by this.
    /// </summary>
    public string Severity { get; init; } = "command";

    /// <summary>
    /// Gets a value indicating whether the method needs something to be playing right now.
    /// Policy blocks do not; commands do.
    /// </summary>
    public bool NeedsSession { get; init; }
}

/// <summary>One server-side action inside a stop attempt, and whether it was accepted.</summary>
public sealed class StopAttemptStep
{
    /// <summary>Gets or sets what was attempted.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets a value indicating whether the server accepted the action. Note that
    /// "accepted" only means the message was dispatched — a client that swallows it still
    /// reports Ok.
    /// </summary>
    public bool Ok { get; set; }

    /// <summary>Gets or sets the failure detail, when the action was rejected.</summary>
    public string? Error { get; set; }
}

/// <summary>The full report of one stop-method run, rendered by the test page.</summary>
public sealed class StopAttemptResult
{
    /// <summary>Gets or sets the method id that ran.</summary>
    public string Method { get; set; } = string.Empty;

    /// <summary>Gets or sets the method's display title.</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>Gets or sets when the run started (UTC).</summary>
    public DateTime StartedUtc { get; set; }

    /// <summary>Gets or sets when the run finished (UTC).</summary>
    public DateTime FinishedUtc { get; set; }

    /// <summary>Gets or sets how many sessions the user had, playing or not.</summary>
    public int SessionsSeen { get; set; }

    /// <summary>Gets or sets how many of those were playing something.</summary>
    public int SessionsPlaying { get; set; }

    /// <summary>Gets or sets the human-readable list of targeted sessions.</summary>
    public List<string> Targets { get; set; } = new();

    /// <summary>Gets the per-step outcomes, in execution order.</summary>
    public List<StopAttemptStep> Steps { get; } = new();

    /// <summary>Gets or sets the one-line verdict shown at the top of the result card.</summary>
    public string Summary { get; set; } = string.Empty;
}
