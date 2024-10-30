using MediaBrowser.Controller.Plugins;
using MediaBrowser.Controller.Session;
using MediaBrowser.Controller.Library;
using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Text;
using MediaBrowser.Model.Session;
using MediaBrowser.Model.Services;
using MediaBrowser.Model.Logging;
using System.Threading.Tasks;
using System.IO;
using System.Linq;
using MediaBrowser.Controller.Security;
using System.Net;
using MediaBrowser.Model.Serialization;
using System.Timers;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Api;
using WebSocketSharp.Server;
using System.Net.Http.Headers;
using EmbyParty.LogBridge;
using System.Diagnostics;

namespace EmbyParty
{
    public class PartyManager
    {

        private const long ONE_SECOND_TICKS = 10000000;
        private const long EXACT_START_TICKS = ONE_SECOND_TICKS;

        //Ping timer interval
        private const int TIMER_INTERVAL = 29000;  //29s

        //How long after host stops playback until hosting ends. Hosting will continue if there's another playback request.
        private const int TIMER_HOSTCLEAR_INTERVAL = 7000;  //7s  (>= Attendee.ACCURACY_WORST)

        //Time at the end of the video during which manual pause requests should be ignored
        private const long NO_PAUSE_AT_THE_END = 50000000;  //5s

        //Time at the end of the video during which stray guests aren't returned to the video, but instead stopped to wait for host to end the video.
        //If the guest is in the next video, they're placed in the syncing state to streamline the next playback.
        private const long NO_RETURN_AT_THE_END = 100000000;  //10s  (> Attendee.ACCURACY_WORST)

        //How long guest playback can be deferred in order to wait for the previous video to end, in case guest is offset from host (includes ping)
        private const int MAX_DELAY_AT_THE_END = 10000;  //10s

        //Time at the end of the video during which we help the host along if it runs into the Emby autoplay bug, caused by ffmpeg failing to transcode the final segments
        private const long ASSUMED_FINISHED_INTERVAL = 50000000;  //5s

        //Bridge sidebar chat with external application
        private const string BRIDGE_HOST = "ws://localhost:8196";
        private const string BRIDGE_PATH = "/bridge";

        private long _nextId = 1;
        private ConcurrentDictionary<long, Party> _parties = new ConcurrentDictionary<long, Party>();
        private ConcurrentDictionary<string, Party> _attendees = new ConcurrentDictionary<string, Party>();

        public ICollection<Party> Parties { get => _parties.Values; }

        private ILogger _logger;
        private ISessionManager _sessionManager;
        private IUserManager _userManager;
        private IJsonSerializer _jsonSerializer;
        private ILibraryManager _libraryManager;

        private Timer _partyUpdates;

        private WebSocketServer _bridgeServer;
        public List<Bridge> Bridges = new List<Bridge>();

        public PartyManager(ISessionManager sessionManager, IUserManager userManager, IJsonSerializer jsonSerializer, ILibraryManager libraryManager, ILogger logger)
        {
            _logger = logger;
            _jsonSerializer = jsonSerializer;
            _sessionManager = sessionManager;
            _userManager = userManager;
            _libraryManager = libraryManager;
        }

        protected void Log(string message)
        {
            _logger.Info(message);
        }

        protected void Debug(string message)
        {
            _logger.Debug(message);
        }

        protected void LogWarning(string message)
        {
            _logger.Warn(message);
        }

        public void Run()
        {
            _sessionManager.PlaybackStart += PlaybackStart;
            _sessionManager.PlaybackProgress += PlaybackProgress;
            _sessionManager.PlaybackStopped += PlaybackStopped;
            _sessionManager.SessionEnded += SessionEnded;

            _partyUpdates = new Timer(TIMER_INTERVAL);
            _partyUpdates.Elapsed += OnHeartbeat;
            _partyUpdates.AutoReset = true;
            _partyUpdates.Start();

            _bridgeServer = new WebSocketServer(BRIDGE_HOST);
            _bridgeServer.AddWebSocketService<Bridge>(BRIDGE_PATH, (bridge) => {
                bridge.PartyManager = this;
                bridge.JsonSerializer = _jsonSerializer;
            });
            _bridgeServer.Start();
        }

        public void Dispose()
        {
            _bridgeServer.Stop();

            _partyUpdates.Stop();
            _partyUpdates.Dispose();

            _sessionManager.PlaybackStart -= PlaybackStart;
            _sessionManager.PlaybackProgress -= PlaybackProgress;
            _sessionManager.PlaybackStopped -= PlaybackStopped;
            _sessionManager.SessionEnded -= SessionEnded;
        }

        private void PlaybackStart(object sender, PlaybackProgressEventArgs e)
        {
            Party party = FindAttendeePartyByTarget(e.Session.Id);
            if (party == null) { return; }

            Attendee host = party.Host;
            Attendee attendee = party.GetAttendeeByTarget(e.Session.Id);

            if (attendee == null) { return; }  //Hopefully impossible
            
            Log(">> Playback event from " + e.Session.Id + " (attendee " + attendee.Id + "). At this time host is set to " + host?.Id);

            attendee.ResetAccuracy();
            attendee.PlaySessionId = e.PlaySessionId;
            attendee.ReportedDeviceId = e.DeviceId;
            attendee.CurrentMediaItemId = e.Item.InternalId;
            attendee.RunTimeTicks = (long)e.Item.RunTimeTicks;

            if (host != null && host.Id != attendee.Id) {
                GuestStart(party, host, attendee, e);
            }
            else
            {
                HostStart(party, attendee, e);
            }
        }

        private async void HostStart(Party party, Attendee attendee, PlaybackProgressEventArgs e)
        {
            Log("Party " + party.Id + " member " + attendee.Id + " initiated playback and becomes the host. Syncing with " + party.AttendeePlayers.Count() + " attendees.");

            Debug("Playlist (" + e.Session.PlaylistIndex + ")" + String.Join(",", e.Session.NowPlayingQueue.Select(QueueItem => QueueItem.Id.ToString())));
            Debug("Attendee map: [" + String.Join("] [", party.Attendees.Select(eachAttendee => eachAttendee.Id + " = " + eachAttendee.DisplayName + " (" + eachAttendee.State + ")")) + "]");

            bool canPlay = await InitiatePartyPlay(attendee, party, e.Session);
            if (!canPlay)
            {
                await SendPlaystateCommandToAttendeeTarget(attendee, new PlaystateRequest() { Command = PlaystateCommand.Stop });
                party.SendLogMessageToAttendees("Reject", "Failed to start video player.");
            }
        }

        private async void PlayNextVideoOrStop(Attendee host, Party party)
        {
            if (party.CurrentIndex + 1 < party.CurrentQueue.Length)
            {
                PlayRequest request = new PlayRequest();
                request.ItemIds = party.CurrentQueue;
                //request.MediaSourceId = party.MediaSourceId;
                request.StartIndex = party.CurrentIndex + 1;
                request.StartPositionTicks = EXACT_START_TICKS;
                request.PlayCommand = PlayCommand.PlayNow;

                await DelayedAttendeePartyPlay(host, host, AttendeeState.Ready, request, 0);
            }
            else
            {
                await SendPlaystateCommandToAttendeeTarget(host, new PlaystateRequest() { Command = PlaystateCommand.Stop });
            }
        }

        private async void GuestStart(Party party, Attendee host, Attendee attendee, PlaybackProgressEventArgs e)
        {
            if (party.CurrentQueue != null && e.Item != null && party.CurrentQueue[party.CurrentIndex] != e.Item.InternalId)
            {
                if (attendee.State == AttendeeState.WaitForPlay)
                {
                    //Ignore wrong video start event during playback sync (assume stray event from multiple successive playback requests)
                }
                else if (e.Item.RunTimeTicks < NO_RETURN_AT_THE_END || host.EstimatedPositionTicks > e.Item.RunTimeTicks - NO_RETURN_AT_THE_END)
                {
                    //Stop guest to wait for host
                    Log("Host is about to end item " + party.CurrentQueue[party.CurrentIndex] + " but guest is in item " + e.Item.InternalId + " so stop guest to wait for host.");

                    if (party.CurrentIndex + 1 < party.CurrentQueue.Length && party.CurrentQueue[party.CurrentIndex + 1] == e.Item.InternalId)
                    {
                        //Guest is ready in advance to play next item, let's use that
                        attendee.State = AttendeeState.Syncing;
                        party.SendSyncWaiting(attendee);
                    }

                    attendee.IgnoreNext(ProgressEvent.Pause);
                    await SendPlaystateCommandToAttendeeTarget(attendee, new PlaystateRequest() { Command = PlaystateCommand.Pause });
                }
                else
                {
                    //Return guest to the correct video
                    Log("Host is in item " + party.CurrentQueue[party.CurrentIndex] + " but guest is in item " + e.Item.InternalId + " so bring guest back to right item.");
                    SyncPartyPlay(party, new Attendee[] { attendee }, attendee.State == AttendeeState.WaitForPlay ? AttendeeState.WaitForPlay : AttendeeState.Ready, PlayCommand.PlayNow);
                }
            }
            else if (attendee.State == AttendeeState.WaitForPlay || attendee.State == AttendeeState.WaitForSeek)
            {
                //During sync, unpause everyone if all guests are playing, otherwise pause guest
                //WaitForSeek can be handled here because of when guests join late or reopen video player during sync.
                attendee.State = AttendeeState.Syncing;
                if (!CheckAndCompleteSync(party, host))
                {
                    ReadyAndContinueSync(party, attendee);
                }
            }
            else if (host.State != AttendeeState.Syncing && host.IsPaused)
            {
                //Host is regular paused
                attendee.IgnoreNext(ProgressEvent.Pause);
                await SendPlaystateCommandToAttendeeTarget(attendee, new PlaystateRequest() { Command = PlaystateCommand.Pause });
            }
        }

        private async void PlaybackProgress(object sender, PlaybackProgressEventArgs e)
        {
            Party party = FindAttendeePartyByTarget(e.Session.Id);
            if (party == null) { return; }

            Attendee attendee = party.GetAttendeeByTarget(e.Session.Id);

            Log(">> Progress event from " + e.Session.Id + (e.Session.Id != attendee.Id ? " (attendee " + attendee.Id + ")" : "") + ": " + e.EventName);

            if (e.Item.InternalId != attendee.CurrentMediaItemId)
            {
                Debug("Discarded out of order event.");
                return;
            }

            try
            {
                if (e.EventName == ProgressEvent.TimeUpdate)
                {
                    if (e.PlaybackPositionTicks != attendee.PositionTicks && (e.PlaybackPositionTicks - attendee.PositionTicks) % ONE_SECOND_TICKS == 0)
                    {
                        //Discard bogus updates
                        return;
                    }
                    if (e.PlaySessionId != attendee.PlaySessionId)
                    {
                        //Playsession changes if audio streams are changed mid-video
                        attendee.PlaySessionId = e.PlaySessionId;
                    }
                }

                Debug("Attendee states: [" + String.Join("] [", party.Attendees.Select(eachAttendee => eachAttendee.Id + " = " + eachAttendee.State)) + "]");

                if (e.EventName == ProgressEvent.Pause)
                {
                    if (attendee.IsPaused) { return; }
                    attendee.PositionTicks = (long)e.PlaybackPositionTicks;
                    attendee.IsPaused = true;
                }

                if (e.EventName == ProgressEvent.Unpause)
                {
                    if (!attendee.IsPaused) { return; }
                    attendee.PositionTicks = (long)e.PlaybackPositionTicks;
                    attendee.IsPaused = false;
                }

                Attendee host = party.Host;
                if (host == null) { return; }

                if (host.Id == attendee.Id)
                {
                    HostProgress(party, host, e);
                }
                else
                {
                    await GuestProgress(party, host, attendee, e);
                }
            }
            catch (Exception ex)
            {
                LogWarning("Exception in playback progress! " + ex.Message);
                LogWarning(new System.Diagnostics.StackTrace().ToString());
            }
        }

        private void HostProgress(Party party, Attendee host, PlaybackProgressEventArgs e)
        {
            if (party.InSyncConclusion && e.EventName == ProgressEvent.Unpause)
            {
                party.InSyncConclusion = false;
            }

            if (host.ShouldIgnore(e.EventName)) { return; }

            Log("Party " + party.Id + " host " + host.Id + " progress update " + e.EventName);

            if (e.EventName == ProgressEvent.TimeUpdate && e.PlaybackPositionTicks != EXACT_START_TICKS)
            {
                //Keep track of host position and detect manual seek

                if (host.PositionTicks > host.RunTimeTicks - ASSUMED_FINISHED_INTERVAL && host.PositionTicks == e.PlaybackPositionTicks)
                {
                    //The host is not making progress and is at the end of the video; play next video

                    Log("Stalled; Forcibly ending current video.");

                    PlayNextVideoOrStop(host, party);

                }
                else if (host.State == AttendeeState.Ready && host.IsOutOfSync((long)e.PlaybackPositionTicks))
                {
                    Log("Host is out of sync with itself, " + e.PlaybackPositionTicks + " is too far from saved pos " + host.EstimatedPositionTicks + " (LU: " + host.LastUpdate.Ticks + " TSU: " + host.TicksSinceUpdate + " NOW:" + DateTimeOffset.UtcNow.Ticks + ") ");

                    if (party.AttendeePlayers.Count() > 1)
                    {
                        if (host.State == AttendeeState.Syncing)
                        {
                            //Use ongoing sync for seeking

                            foreach (Attendee attendee in party.AttendeePlayers)
                            {
                                if (attendee.State == AttendeeState.Syncing) {
                                    attendee.State = AttendeeState.WaitForSeek;
                                    party.SendSyncReset(attendee);
                                }
                            }

                            SyncPlaystateByState(party, host.Id, AttendeeState.WaitForSeek, PlaystateCommand.Seek, e.PlaybackPositionTicks);
                        }
                        else
                        {
                            //Start sync for seeking

                            party.SendSyncStart();

                            if (!host.IsPaused)
                            {
                                //Pause the host while waiting for guests

                                PlaystateRequest pauseForSeek = new PlaystateRequest();
                                pauseForSeek.Command = PlaystateCommand.Pause;
                                pauseForSeek.SeekPositionTicks = e.PlaybackPositionTicks;

                                host.IgnoreNext(ProgressEvent.Pause);
                                SendPlaystateCommandToAttendeeTarget(host, pauseForSeek);
                            }
                            else
                            {
                                party.NoUnpauseAfterSync = true;
                            }

                            party.SetPlayerStates(AttendeeState.WaitForSeek, AttendeeState.Syncing);
                            host.State = AttendeeState.Syncing;

                            Log("Host began syncing sequence.");

                            //Seek guests

                            SyncPlaystate(party, host.Id, PlaystateCommand.Seek, e.PlaybackPositionTicks);
                        }
                    }
                }

                host.PositionTicks = (long)e.PlaybackPositionTicks;
                Debug("Host updated position to " + host.PositionTicks + " at " + host.LastUpdate.Ticks);
            }

            if (e.EventName == ProgressEvent.Pause) {
                if (host.RunTimeTicks > NO_PAUSE_AT_THE_END && e.PlaybackPositionTicks < host.RunTimeTicks - NO_PAUSE_AT_THE_END)
                {
                    if (!party.GuestPauseAction) { party.SendLogMessageToAttendees("Pause", host.DisplayName); }
                    party.GuestPauseAction = false;
                    SyncPlaystate(party, host.Id, PlaystateCommand.Pause, e.PlaybackPositionTicks);
                }
            }
            if (e.EventName == ProgressEvent.Unpause) {
                if (!party.GuestPauseAction) { party.SendLogMessageToAttendees("Unpause", host.DisplayName); }
                party.GuestPauseAction = false;
                SyncPlaystate(party, host.Id, PlaystateCommand.Unpause, e.PlaybackPositionTicks);
            }

            if (e.EventName == ProgressEvent.AudioTrackChange) {
                SyncTrack(party, host.Id, nameof(GeneralCommandType.SetAudioStreamIndex), e.PlaySession.PlayState.AudioStreamIndex);
            }
            if (e.EventName == ProgressEvent.SubtitleTrackChange) {
                SyncTrack(party, host.Id, nameof(GeneralCommandType.SetSubtitleStreamIndex), e.PlaySession.PlayState.SubtitleStreamIndex);
            }
        }

        private async Task GuestProgress(Party party, Attendee host, Attendee attendee, PlaybackProgressEventArgs e)
        {
            if (attendee.ShouldIgnore(e.EventName)) { return; }

            if (e.EventName == ProgressEvent.TimeUpdate)
            {
                if (attendee.State == AttendeeState.WaitForSeek)
                {
                    //Unpause after seek
                    attendee.State = AttendeeState.Syncing;
                    if (!CheckAndCompleteSync(party, host))
                    {
                        ReadyAndContinueSync(party, attendee);
                    }
                }
                else if (attendee.State == AttendeeState.Ready && attendee.CurrentMediaItemId == host.CurrentMediaItemId)
                {
                    //Keep track of guest position and automatically seek if it's off
                    attendee.PositionTicks = (long)e.PlaybackPositionTicks;

                    if (attendee.IsOutOfSync(host.EstimatedPositionTicks))
                    {
                        long newacc = attendee.LowerAccuracy();
                        Log("Guest needs seek because pos " + attendee.PositionTicks + " (" + attendee.Ping + "ms) too far from estimated host pos " + host.EstimatedPositionTicks + " (" + host.Ping + "ms). Accuracy lowered to " + newacc);

                        PlaystateRequest seek = new PlaystateRequest();
                        seek.Command = PlaystateCommand.Seek;
                        seek.SeekPositionTicks = host.EstimatedPositionTicks + attendee.Ping;

                        attendee.PositionTicks = host.EstimatedPositionTicks;
                        await SendPlaystateCommandToAttendeeTarget(attendee, seek);
                    }

                    //Pause the guest if the host is paused. This happens if a playback command was sent to the guest while the host was paused (late joiner, auto return).
                    if (host.IsPaused && !attendee.IsPaused && !party.InSyncConclusion)
                    {
                        attendee.IgnoreNext(ProgressEvent.Pause);
                        await SendPlaystateCommandToAttendeeTarget(attendee, new PlaystateRequest() { Command = PlaystateCommand.Pause });
                    }
                }
            }

            if (e.EventName == ProgressEvent.Pause && !host.IsPaused && attendee.CurrentMediaItemId == host.CurrentMediaItemId)
            {
                Log("Detected manual pause request, propagating.");
                party.SendLogMessageToAttendees("Pause", attendee.DisplayName);
                party.GuestPauseAction = true;
                await SendPlaystateCommandToAttendeeTarget(host, new PlaystateRequest() { Command = PlaystateCommand.Pause });
            }

            if (e.EventName == ProgressEvent.Unpause && host.IsPaused && attendee.CurrentMediaItemId == host.CurrentMediaItemId)
            {
                Log("Detected manual unpause request, propagating.");
                party.SendLogMessageToAttendees("Unpause", attendee.DisplayName);
                party.GuestPauseAction = true;
                await SendPlaystateCommandToAttendeeTarget(host, new PlaystateRequest() { Command = PlaystateCommand.Unpause });
            }
        }

        private bool CheckAndCompleteSync(Party party, Attendee host)
        {
            if (party.AreAllStates(AttendeeState.Syncing))
            {
                Log("All guests have synced with the host.");
                if (!party.NoUnpauseAfterSync)
                {
                    Log("The host wasn't paused before sync, therefore unpausing.");
                    party.IgnoreNext(ProgressEvent.Unpause);
                    SyncPlaystate(party, null, PlaystateCommand.Unpause, host.PositionTicks);
                }

                party.NoUnpauseAfterSync = false;
                party.InSyncConclusion = true;
                party.SetStates(AttendeeState.Ready);

                party.SendSyncEnd();

                return true;
            }
            else
            {
                Debug("Failed sync check.");
            }
            return false;
        }

        private async void ReadyAndContinueSync(Party party, Attendee attendee)
        {
            attendee.IgnoreNext(ProgressEvent.Pause);
            await SendPlaystateCommandToAttendeeTarget(attendee, new PlaystateRequest() { Command = PlaystateCommand.Pause });

            party.SendSyncWaiting(attendee);
        }

        private void PlaybackStopped(object sender, PlaybackStopEventArgs e)
        {
            Party party = FindAttendeePartyByTarget(e.Session.Id);
            if (party == null) { return; }

            Attendee attendee = party.GetAttendeeByTarget(e.Session.Id);

            Log(">> Stop event from " + e.Session.Id + " (attendee " + attendee.Id + ") with play session "+ attendee.PlaySessionId);

            if (attendee.PlaySessionId != e.PlaySessionId) { return; }  //PlaySession has already been replaced

            attendee.PlaySessionId = null;

            Attendee host = party.Host;
            if (host == null || host.Id != attendee.Id) { return; }

            Log("Party " + party.Id + " host " + host.Id + " ended playback and relinquished role as host.");

            party.PreviousItem = null;
            if (party.CurrentQueue != null)
            {
                party.PreviousItem = party.CurrentQueue[party.CurrentIndex];
            }

            if (party.CurrentQueue.Length > 1)
            {
                //Prevent party from losing and re-gaining host on every video change
                party.LateClearHost = new Timer(TIMER_HOSTCLEAR_INTERVAL);
                party.LateClearHost.Elapsed += (source, eea) => party.ClearHost();
                party.LateClearHost.Start();
            }
            else
            {
                party.ClearHost();
            }

        }

        private async Task<bool> InitiatePartyPlay(Attendee host, Party party, SessionInfo session)
        {
            if (session == null || session.PlayState == null || session.PlaylistIndex < 0 || session.PlaylistIndex >= session.NowPlayingQueue.Length) { return false; }

            long[] itemIds = session.NowPlayingQueue.Select(queueItem => queueItem.Id).ToArray();
            List<User> attendingUsers = party.GetAttendingUsers();

            //Check if item is playable by all attendees

            BaseItem item = null;
            if (itemIds != null)
            {
                item = _libraryManager.GetItemById(itemIds[session.PlaylistIndex]);
            }
            if (item == null) { return false; }

            foreach (User user in attendingUsers)
            {
                if (!item.IsVisible(user)) {
                    party.SendLogMessageToAttendees("Reject", item.Name);
                    return false;
                }
            }

            //Sort out previous item            

            BaseItem previousItem = null;
            if (host.State != AttendeeState.Syncing)
            {
                if (party.CurrentQueue != null && party.CurrentIndex < party.CurrentQueue.Length)
                {
                    //Take previous item from queue if Play is called without a Stop
                    Debug("Taking previous item from queue: " + party.CurrentQueue[party.CurrentIndex]);
                    previousItem = _libraryManager.GetItemById(party.CurrentQueue[party.CurrentIndex]);
                }
                else if (party.PreviousItem != null)
                {
                    //Use previous item stored by stop handler if Play was called after Stop
                    Debug("Taking previous item from stop: " + party.PreviousItem);
                    previousItem = _libraryManager.GetItemById((long)party.PreviousItem);
                }
            }

            //Update party status

            party.ClearIgnores();

            party.SetHost(host, true);
            bool hostWasAlreadySyncing = (host.State == AttendeeState.Syncing);
            host.State = AttendeeState.Syncing;
            party.ResetPositionTicks(1);  //(1 tick) Emby starts at exactly 1s. Don't reset to exactly 1s offset from that or first update is discarded.
            host.PositionTicks = (long)session.PlayState.PositionTicks;

            party.CurrentQueue = itemIds;
            party.CurrentIndex = session.PlaylistIndex;
            party.MediaSourceId = session.PlayState.MediaSourceId;
            party.AudioStreamIndex = session.PlayState.AudioStreamIndex;
            party.SubtitleStreamIndex = session.PlayState.SubtitleStreamIndex;

            party.SendLogMessageToAttendees("Now Playing", item.Name);

            Debug("Initiate party play: " + party.CurrentQueue[party.CurrentIndex] + " AI:" + party.AudioStreamIndex + " SI:" + party.SubtitleStreamIndex + " Pos:" + host.PositionTicks + " Midsync:" + hostWasAlreadySyncing + " Att:" + party.Attendees.Count());

            if (party.AttendeePlayers.Count() > 1)
            {
                party.SendSyncStart();

                //Pause the host while waiting for guests

                if (!hostWasAlreadySyncing)
                {
                    PlaystateRequest startPaused = new PlaystateRequest();
                    startPaused.Command = PlaystateCommand.Pause;
                    startPaused.SeekPositionTicks = host.PositionTicks;

                    host.IgnoreNext(ProgressEvent.Pause);
                    await SendPlaystateCommandToAttendeeTarget(host, startPaused);
                }

                //Play on guests

                party.SetPlayerStates(null, AttendeeState.Syncing);
                SyncPartyPlay(party, party.AttendeePlayers.ToArray(), AttendeeState.WaitForPlay, PlayCommand.PlayNow, previousItem);

            }

            return true;
        }

        public void SyncPartyPlay(Party party, Attendee[] attendees, AttendeeState initialState, PlayCommand playCommand)
        {
            SyncPartyPlay(party, attendees, initialState, playCommand, null);
        }
        public async void SyncPartyPlay(Party party, Attendee[] attendees, AttendeeState initialState, PlayCommand playCommand, BaseItem previousItem)
        {
            Attendee host = party.Host;

            PlayRequest request = new PlayRequest();
            request.ItemIds = party.CurrentQueue;
            //request.MediaSourceId = party.MediaSourceId;
            request.StartIndex = party.CurrentIndex;
            request.StartPositionTicks = host.PositionTicks;
            request.PlayCommand = playCommand;
            request.AudioStreamIndex = party.AudioStreamIndex;
            request.SubtitleStreamIndex = party.SubtitleStreamIndex;

            Debug("Assembled party play request:" + _jsonSerializer.SerializeToString(request));

            List<Task<bool>> partyPlays = new List<Task<bool>>();

            foreach (Attendee attendee in attendees)
            {
                if (attendee.Id == host.Id || attendee.IsBeingRemoteControlled) { continue; }

                //Debug("Previous item:" + (previousItem != null ? "true" : "false") + ", Attendee state:" + attendee.State + ", Attendee paused:" + (attendee.IsPaused ? "true" : "false") + ", EPT:" + attendee.EstimatedPositionTicks + ", RPT:" + (previousItem != null ? previousItem.RunTimeTicks.ToString() : ""));

                int delay = 0;
                if (previousItem != null && !attendee.IsPaused
                    && attendee.EstimatedPositionTicks > previousItem.RunTimeTicks - Attendee.ACCURACY_WORST && attendee.EstimatedPositionTicks < previousItem.RunTimeTicks)
                {
                    //Delay playback of next item in queue to account for offset between guest and host
                    delay = (int)(previousItem.RunTimeTicks - attendee.EstimatedPositionTicks) / 10000;
                }
                if (attendee.Ping > 0) { delay += (int)attendee.Ping; }
                if (delay > MAX_DELAY_AT_THE_END) delay = MAX_DELAY_AT_THE_END;

                Log("Sync party play to " + attendee.Id + " at position ticks " + host.PositionTicks + " after " + delay + "ms");
                partyPlays.Add(DelayedAttendeePartyPlay(host, attendee, initialState, request, delay));
            }

            bool[] results = await Task.WhenAll(partyPlays);

            if (results.ToList().Where(result => result).Count() == 0)
            {
                //None of the party play commands fired, so all guests should be ready to go
                CheckAndCompleteSync(party, host);
            }
        }

        private async Task<bool> DelayedAttendeePartyPlay(Attendee host, Attendee attendee, AttendeeState initialState, PlayRequest request, int delay)
        {
            if (delay >= 100) { await Task.Delay(delay); }

            if (attendee.CurrentMediaItemId != null && (long)attendee.CurrentMediaItemId == request.ItemIds[request.StartIndex ?? 0] && attendee.State == AttendeeState.Syncing) {
                //PlaybackStart already arrived for guest, so this isn't necessary
                return false;
            }

            attendee.State = initialState;

            attendee.PositionTicks = host.PositionTicks;
            attendee.IsPaused = false;
            try
            {
                Debug("----- Sending Play command to " + attendee.TargetId);
                await _sessionManager.SendPlayCommand(null, attendee.TargetId, request, new System.Threading.CancellationToken());
            }
            catch
            {
                LogWarning("Failed to send Play command to " + attendee.TargetId);
            }

            return true;
        }

        public void SyncPlaystate(Party party, string fromId, PlaystateCommand command, long? positionTicks)
        {
            SyncPlaystateByState(party, fromId, null, command, positionTicks);
        }
        public void SyncPlaystateByState(Party party, string fromId, AttendeeState? state, PlaystateCommand command, long? positionTicks)
        {
            Debug("----- Sync playstate request: " + command + (fromId != null ? " from " + fromId : ""));

            PlaystateRequest request = new PlaystateRequest();
            request.SeekPositionTicks = positionTicks;
            request.Command = command;

            foreach (Attendee attendee in party.AttendeePlayers)
            {
                if (attendee.Id == fromId) { continue; }
                if (state != null && attendee.State != state) { continue; }
                SendPlaystateCommandToAttendeeTarget(attendee, request);
            }
        }

        public void SyncTrack(Party party, string fromId, string command, int? index)
        {
            GeneralCommand request = new GeneralCommand();
            request.Name = command;
            request.Arguments["Index"] = index.ToString();

            foreach (Attendee attendee in party.AttendeePlayers)
            {
                if (attendee.Id == fromId) { continue; }
                try
                {
                    _sessionManager.SendGeneralCommand(null, attendee.TargetId, request, new System.Threading.CancellationToken());
                }
                catch
                {
                    LogWarning("Failed to send General command to " + attendee.TargetId + ": " + request.Name);
                }
            }
        }

        private void SessionEnded(object sender, SessionEventArgs e)
        {
            RemoveFromParty(e.SessionInfo.Id);
        }

        public async Task<Party> CreateParty(string sessionId, string name, SessionInfo session)
        {
            Log("Creating new party.");
            Party party = new Party(_nextId++, this, _sessionManager, _userManager, _logger);
            party.Name = name;
            _parties.TryAdd(party.Id, party);

            Party withAttendee = await AddToParty(sessionId, party.Id, session);
            if (withAttendee != party)
            {
                _parties.TryRemove(party.Id, out _);
                Log("Party creation failed, rolled back for " + party.Id);
                return null;
            }

            Log("Created party " + party.Id + " (" + name + ") with " + sessionId);
            return party;
        }

        public async Task<Party> AddToParty(string sessionId, long partyId, SessionInfo session)
        {
            Party party = GetParty(partyId);
            if (party == null) { return null; }
            if (party.CurrentQueue != null)
            {
                BaseItem item = _libraryManager.GetItemById(party.CurrentQueue[party.CurrentIndex]);
                User user = _userManager.GetUserById(session.UserId);
                if (!item.IsVisible(user)) { return null; }
            }

            RemoveFromParty(sessionId);
            Attendee newAttendee = party.AttendeeJoin(sessionId, session);
            if (newAttendee == null) {
                Log("Attendee " + sessionId + " was already in party " + partyId);
                return party;
            }
            _attendees.TryAdd(sessionId, party);

            Log("Added " + sessionId + " to party " + partyId);

            if (party.CurrentQueue != null)
            {
                LateJoiner(party, newAttendee);
            }
            else if (session.NowPlayingItem != null)
            {
                //Playing something but no one else is, so become host

                newAttendee.ResetAccuracy();
                newAttendee.ReportedDeviceId = session.DeviceId;
                newAttendee.CurrentMediaItemId = long.Parse(session.NowPlayingItem.Id);
                newAttendee.RunTimeTicks = (long)session.NowPlayingItem.RunTimeTicks;

                bool canPlay = await InitiatePartyPlay(newAttendee, party, session);
                if (!canPlay)
                {
                    await SendPlaystateCommandToAttendeeTarget(newAttendee, new PlaystateRequest() { Command = PlaystateCommand.Stop });
                    party.SendLogMessageToAttendees("Reject", "Failed to host on previous video player.");
                }
            }

            return party;
        }

        public void LateJoiner(Party party, Attendee attendee)
        {
            Attendee host = party.Host;
            SyncPartyPlay(party, new Attendee[] { attendee }, host.State == AttendeeState.Syncing ? AttendeeState.WaitForSeek : AttendeeState.Ready, PlayCommand.PlayNow);
        }

        public bool RemoveFromParty(string sessionId)
        {
            Party party = GetAttendeeParty(sessionId);
            if (party == null) { return false; }
            Attendee formerAttendee = party.AttendeePart(sessionId);

            if (party.Attendees.Count == 0)
            {
                _parties.TryRemove(party.Id, out _);
                Log("Discarding empty party " + party.Id);
                BroadcastToBridges("PartyEnded", party.Name, null);
            }
            else if (formerAttendee.State == AttendeeState.WaitForPlay || formerAttendee.State == AttendeeState.WaitForSeek)
            {
                CheckAndCompleteSync(party, party.Host);
            }

            _attendees.TryRemove(sessionId, out _);
            
            Log("Removed " + sessionId + " from party " + party.Id);

            return true;
        }

        public Task SendPlaystateCommandToAttendeeTarget(Attendee attendee, PlaystateRequest request)
        {
            try
            {
                return _sessionManager.SendPlaystateCommand(null, attendee.TargetId, request, new System.Threading.CancellationToken());
            }
            catch
            {
                LogWarning("Failed to send Playstate command to " + attendee.TargetId + ": " + request.Command);
            }
            return Task.CompletedTask;
        }

        public Party GetParty(long partyId)
        {
            Party party;
            if (_parties.TryGetValue(partyId, out party))
            {
                return party;
            }
            return null;
        }

        public Party GetPartyByName(string name)
        {
            return Parties.Where(party => party.Name == name).FirstOrDefault();
        }

        public Party GetAttendeeParty(string sessionId) {
            Party party;
            if (_attendees.TryGetValue(sessionId, out party))
            {
                return party;
            }
            return null;
        }

        public Party FindAttendeePartyByTarget(string sessionId)
        {
            foreach (Party party in _parties.Values)
            {
                if (party.AttendeeTargets.Contains(sessionId))
                {
                    return party;
                }
            }
            return GetAttendeeParty(sessionId);
        }

        private void OnHeartbeat(Object source, ElapsedEventArgs e)
        {
            long nowms = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            GeneralCommand ping = new GeneralCommand() { Name = "PartyPing" };
            ping.Arguments["ts"] = nowms.ToString();

            foreach (Party party in Parties)
            {
                //Emby Session ping
                foreach (Attendee paused in party.AttendeesPaused)
                {
                    if (paused.PlaySessionId == null) { continue; }
                    Debug("Pinging paused user: " + paused.Id);
                    _sessionManager.PingSession(paused.ReportedDeviceId, paused.PlaySessionId);
                }

                //Our ping
                foreach (Attendee attendee in party.Attendees)
                {
                    if (attendee.PendingPing.Count > 1)  //Can miss 2 pings
                    {
                        Log("Ping timeout: " + attendee.Id);
                        RemoveFromParty(attendee.Id);
                        continue;
                    }

                    attendee.PendingPing.Add(nowms);
                    party.SendEventToSingleAttendee(attendee, ping);
                }

            }
        }

        public void BroadcastToBridges(string messageType, string partyName, object data)
        {
            MessageBase messageObj = new MessageBase() { MessageType = messageType, Party = partyName, Data = data};
            string message = _jsonSerializer.SerializeToString(messageObj);
            foreach (Bridge bridge in Bridges)
            {
                bridge.Send(message);
            }
        }


        public bool IsTargetSafe(Attendee attendee, string remoteCandidate)
        {
            //Prevent remote control loops for sessions we're tracking. Better than nothing.
            return IsTargetSafeInternal(remoteCandidate, new List<string>() { attendee.Id, remoteCandidate });
        }
        private bool IsTargetSafeInternal(string check, List<string> trace)
        {
            if (check == null) { return true; }
            Party party = GetAttendeeParty(check);
            if (party == null) { return true; }
            Attendee attendee = party.GetAttendee(check);
            if (attendee.RemoteControl == null) { return true; }
            if (trace.Contains(attendee.RemoteControl)) { return false; }
            trace.Add(attendee.RemoteControl);
            return IsTargetSafeInternal(attendee.RemoteControl, trace);
        }

    }
}
