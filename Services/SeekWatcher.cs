using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Timers;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Subtitles;
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

class SessionData
{
    public long? Tick { get; set; }
    public int? LastSubtitleIndex { get; set; }
}

public class SeekWatcher(
IUserDataManager userDataManager,
    ISessionManager sessionManager,
    ILogger<SeekWatcher> logger,
    IUserManager userManager
) : IHostedService, IDisposable
{
    private readonly IUserDataManager _userDataManager = userDataManager;
    private readonly ISessionManager _sessionManager = sessionManager;
    private readonly IUserManager _userManager = userManager;
    private readonly ILogger<SeekWatcher> _logger = logger;
    private readonly System.Timers.Timer _playbackTimer = new(1000);
    // Contains the session ID and the timestamp at which the subtitles will be disabled
    private readonly ConcurrentDictionary<string, ActiveSubtitle> _activeSubtitles = new();
    private PluginConfiguration _config = new();
    // Tracking dictionary to see when a user skips back
    // When they do add to `_activeSubtitles`
    private readonly ConcurrentDictionary<string, SessionData> _watchedSessions = new();

    private int? FindSubtitleStreamWithPredicate(IEnumerable<MediaStream> streams, Func<MediaStream, bool> predicate)
    {
        var stream = streams.FirstOrDefault(predicate);
        return stream?.Index;
    }
    private int TryFindSubtitleStream(SessionInfo session)
    {
        // Preference:
        // 1. Use the last-loaded subtitles
        // 2. Use the default subtitle stream
        // 3. Use the user's preferred language

        var streams = session.NowPlayingItem.MediaStreams.Where(stream => stream.Type == MediaStreamType.Subtitle);

        var userLanguagePreference = _userManager.GetUserById(session.UserId)!.SubtitleLanguagePreference;

        // Data guaranteed to exist
        _watchedSessions.TryGetValue(session.Id, out var data);
        var lastLoadedSubtitleIndex = FindSubtitleStreamWithPredicate(streams, stream => stream.Index == data!.LastSubtitleIndex);
        if (lastLoadedSubtitleIndex.HasValue && lastLoadedSubtitleIndex.Value >= 0)
        {
            return lastLoadedSubtitleIndex.Value;
        }

        var defaultSubtitleIndex = FindSubtitleStreamWithPredicate(streams, stream => stream.IsDefault);
        if (defaultSubtitleIndex.HasValue && defaultSubtitleIndex.Value >= 0)
        {
            return defaultSubtitleIndex.Value;
        }

        var preferredSubtitleIndex = FindSubtitleStreamWithPredicate(streams, stream => string.IsNullOrWhiteSpace(userLanguagePreference) || stream.Language == userLanguagePreference);
        if (preferredSubtitleIndex.HasValue && preferredSubtitleIndex.Value >= 0)
        {
            return preferredSubtitleIndex.Value;
        }

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
            _logger.LogTrace($"Session {session.Id} tick ({session.PlayState.SubtitleStreamIndex}, {session.PlayState.PositionTicks})");

            // If a session skipped back check for subtitles and enable them if necessary
            var watchedSession = _watchedSessions.GetOrAdd(session.Id, (_) => new SessionData { LastSubtitleIndex = session.PlayState.SubtitleStreamIndex, Tick = session.PlayState.PositionTicks });
            if (session.PlayState.PositionTicks is not null && session.PlayState.PositionTicks < watchedSession.Tick && session.PlayState.SubtitleStreamIndex == -1)
            {
                // Since subtitles are not enabled, enable them
                var index = TryFindSubtitleStream(session);

                if (index != -1)
                {
                    _logger.LogDebug($"Found subtitles for {session.Id} on track {index}, enabling until {watchedSession.Tick}");

                    _activeSubtitles.TryAdd(session.Id, new ActiveSubtitle { Tick = watchedSession.Tick.Value, Index = index });

                    _sessionManager.SendGeneralCommand(session.Id, session.Id, new GeneralCommand
                    {
                        Name = GeneralCommandType.SetSubtitleStreamIndex,
                        Arguments = { { "Index", index.ToString() } }
                    }, CancellationToken.None);
                }
                else
                {
                    _logger.LogError($"Failed to find subtitles for {session.Id}");
                }
            }

            watchedSession!.Tick = session.PlayState.PositionTicks;
            if (session.PlayState.SubtitleStreamIndex != -1)
            {
                watchedSession!.LastSubtitleIndex = session.PlayState.SubtitleStreamIndex;
            }

            if (_activeSubtitles.TryGetValue(session.Id, out var activeSubtitle))
            {
                if (activeSubtitle.Tick <= session.PlayState.PositionTicks)
                {
                    _activeSubtitles.TryRemove(session.Id, out _);

                    // If the user changed subtitles between DejaVu turning them on and the off period being hit then don't take any action
                    if (session.PlayState.SubtitleStreamIndex != activeSubtitle.Index)
                    {
                        _logger.LogDebug($"User changed subtitles during DejaVu period for session {session.Id} (SSI {session.PlayState.SubtitleStreamIndex} != {activeSubtitle.Index}), skipping");
                        continue;
                    }

                    _logger.LogDebug($"Disabling subtitles for session {session.Id}");

                    _sessionManager.SendGeneralCommand(session.Id, session.Id, new GeneralCommand
                    {
                        Name = GeneralCommandType.SetSubtitleStreamIndex,
                        Arguments = {
                                        {"Index", "-1"}
                                    }
                    }, CancellationToken.None);
                }
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
        _playbackTimer.AutoReset = true;
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
