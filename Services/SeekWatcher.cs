using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Timers;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Tasks;
using MediaBrowser.Model.Session;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MediaBrowser.Common.Extensions;
using MediaBrowser.Controller.Session;

namespace DejaVu.Services;

class ActiveSubtitle
{
    public long Index { get; set; }
    public long Tick { get; set; }
}

public class SeekWatcher(
IUserDataManager userDataManager,
    ISessionManager sessionManager,
    ILogger<SeekWatcher> logger
) : IHostedService, IDisposable
{
    private readonly IUserDataManager _userDataManager = userDataManager;
    private readonly ISessionManager _sessionManager = sessionManager;
    private readonly ILogger<SeekWatcher> _logger = logger;
    private readonly System.Timers.Timer _playbackTimer = new(1000);
    // Contains the session ID and the timestamp at which the subtitles will be disabled
    private readonly ConcurrentDictionary<string, ActiveSubtitle> _activeSubtitles = new();
    private PluginConfiguration _config = new();
    // Tracking dictionary to see when a user skips back
    // When they do add to `_activeSubtitles`
    private readonly ConcurrentDictionary<string, long?> _watchedSessions = new();

    private int TryFindSubtitleStream(SessionInfo session)
    {
        // Preference:
        // 1. Use the last-loaded subtitles
        // 2. Try to match the user's language preference
        // 3. Use the first available subtitles
        return -1;
    }

    private void PlaybackTimerElapsed(object? sender, ElapsedEventArgs e)
    {
        foreach (var session in _sessionManager.Sessions)
        {
            // If the session isn't playing, do nothing
            if (session.PlayState.IsPaused)
            {
                continue;
            }

            // If a session skipped back check for subtitles and enable them if necessary
            if (session.PlayState.PositionTicks < _watchedSessions.GetOrAdd(session.Id, (_) => session.PlayState.PositionTicks))
            {
                // If subtitles are already enabled then don't do anything
                if (session.PlayState.SubtitleStreamIndex != -1)
                {
                    continue;
                }

                // If subtitles are not enabled then enable them
                var index = TryFindSubtitleStream(session);

                if (index != -1 && session.PlayState.PositionTicks is not null)
                {
                    _logger.LogDebug($"Found subtitles for {session.Id} on track {index}, enabling");

                    _activeSubtitles.TryAdd(session.Id, new ActiveSubtitle { Tick = session.PlayState.PositionTicks.Value, Index = index });

                    _sessionManager.SendGeneralCommand(session.Id, session.Id, new GeneralCommand
                    {
                        Name = GeneralCommandType.SetSubtitleStreamIndex,
                        Arguments = { { "Index", index.ToString() } }
                    }, CancellationToken.None);
                }
            }

            if (!_activeSubtitles.TryGetValue(session.Id, out var activeSubtitle))
            {
                continue;
            }

            if (activeSubtitle.Tick <= session.PlayState.PositionTicks)
            {
                _activeSubtitles.TryRemove(session.Id, out _);

                // If the user changed subtitles between DejaVu turning them on and the off period being hit then don't take any action
                if (session.PlayState.SubtitleStreamIndex != activeSubtitle.Index)
                {
                    continue;
                }

                _logger.LogInformation("Disabling subtitles for session {SessionId}", session.Id);

                _sessionManager.SendGeneralCommand(session.Id, session.Id, new GeneralCommand
                {
                    Name = GeneralCommandType.SetSubtitleStreamIndex,
                    Arguments = {
                        {"Index", "-1"}
                    }
                }, CancellationToken.None);
            }
        }

        // Session cleanup
        foreach (var sessionId in _watchedSessions.Keys)
        {
            if (!_sessionManager.Sessions.Where(session => session.Id == sessionId).Any())
            {
                _watchedSessions.TryRemove(sessionId, out _);
                _activeSubtitles.TryRemove(sessionId, out _);
            }
        }
        ;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogDebug("Starting SeekWatcher");

        _playbackTimer.Elapsed += PlaybackTimerElapsed;
        _playbackTimer.Start();

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _playbackTimer.Stop();
        _playbackTimer.Elapsed -= PlaybackTimerElapsed;

        return Task.CompletedTask;
    }

    public void Dispose()
    {
        _playbackTimer.Dispose();
    }
}
