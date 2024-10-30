using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Services;
using MediaBrowser.Model.Session;
using MediaBrowser.Model.System;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Timers;

namespace EmbyParty
{
    public class Party
    {
        private PartyManager _partyManager;
        private ISessionManager _sessionManager;
        private IUserManager _userManager;
        private ILogger _logger;

        private long _id;
        private Dictionary<string, Attendee> _attendees = new Dictionary<string, Attendee>();

        private Dictionary<string, Attendee> _attendeeTargetMap = new Dictionary<string, Attendee>();  //Map target to remote controller

        private Random _tagCharacterRng = new Random();

        public long Id { get => _id; }

        public string Name { get; set; }

        public long[] CurrentQueue { get; set; }
        public int CurrentIndex { get; set; }
        public string MediaSourceId { get; set; }
        public int? AudioStreamIndex { get; set; }
        public int? SubtitleStreamIndex { get; set; }
        public long? PreviousItem { get; set; }

        public bool NoUnpauseAfterSync { get; set; }  //Store whether host sync is taking place from a starting paused or unpaused state
        public bool InSyncConclusion { get; set; }  //True between the final guest reporting playback and the end of the sync process (host pending unpause), prevents out of order pausing
        public bool GuestPauseAction { get; set; }
        public Timer LateClearHost { get; set; }
        
        public Attendee Host { get => _attendees.Values.FirstOrDefault<Attendee>(attendee => attendee.IsHost); }

        public Dictionary<string, Attendee>.ValueCollection Attendees { get => _attendees.Values; }

        public IEnumerable<Attendee> AttendeePlayers { get => _attendees.Values.Where(attendee => !attendee.IsBeingRemoteControlled); }

        public Dictionary<string, Attendee>.KeyCollection AttendeeTargets { get => _attendeeTargetMap.Keys; }

        public IEnumerable<Attendee> AttendeesPaused { get => _attendees.Values.Where(attendee => attendee.IsPaused); }

        public Party(long id, PartyManager partyManager, ISessionManager sessionManager, IUserManager userManager, ILogger logger)
        {
            _partyManager = partyManager;
            _sessionManager = sessionManager;
            _userManager = userManager;
            _logger = logger;
            _id = id;
        }

        public void Log(string message)
        {
            _logger.Info(message);
        }

        protected void LogWarning(string message)
        {
            _logger.Warn(message);
        }

        private string randomTag(int length)
        {
            string pool = "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";
            string result = "";
            for (int i = 0; i < length; i++)
            {
                result += pool.Substring(_tagCharacterRng.Next(0, pool.Length), 1);
            }
            return result;
        }

        private string[] GenerateAlternativeNames(Attendee attendee)
        {
            return new string[] {
                attendee.UserName + " - " + attendee.DeviceName,
                attendee.UserName + " " + randomTag(2)
            };
        }

        public Attendee AttendeeJoin(string sessionId, SessionInfo session)
        {
            if (_attendees.ContainsKey(sessionId)) { return null; }
            
            Attendee newAttendee = new Attendee()
            {
                Id = sessionId,
                Party = this,
                UserId = session.UserId,
                UserName = session.UserName,
                DeviceName = session.DeviceName,
                DeviceType = session.DeviceType,
                DisplayName = session.UserName
            };

            //Resolve name collisions

            string[] altNames = GenerateAlternativeNames(newAttendee);
            int altNameIndex = 0;
            Attendee currentDuplicate = null;
            Attendee duplicate = GetAttendeeByDisplayName(newAttendee.DisplayName);
            string oldDuplicateName = null;
            while (duplicate != null)
            {
                newAttendee.DisplayName = altNames[altNameIndex];
                string[] duplicateAlts = GenerateAlternativeNames(duplicate);
                currentDuplicate = duplicate;
                if (oldDuplicateName == null) { oldDuplicateName = currentDuplicate.DisplayName; }
                currentDuplicate.DisplayName = duplicateAlts[altNameIndex];
                duplicate = GetAttendeeByDisplayName(newAttendee.DisplayName);
                if (altNameIndex < altNames.Length - 1) { altNameIndex++; }
            }

            if (oldDuplicateName != null)
            {
                GeneralCommand renameCommand = new GeneralCommand() { Name = "PartyUpdateName" };
                renameCommand.Arguments["OldName"] = oldDuplicateName;
                renameCommand.Arguments["NewName"] = currentDuplicate.DisplayName;
                SendEventToAttendees(null, renameCommand);
            }

            _attendees.Add(sessionId, newAttendee);

            //Remote control map

            if (_attendeeTargetMap.ContainsKey(newAttendee.Id))
            {
                newAttendee.IsBeingRemoteControlled = true;
            }

            //Other stuff

            User user = _userManager.GetUserById(session.UserId);
            bool hasPicture = user.GetImages(ImageType.Primary).Count() > 0;

            GeneralCommand command = new GeneralCommand() { Name = "PartyJoin" };
            command.Arguments["UserId"] = newAttendee.UserId;
            command.Arguments["HasPicture"] = hasPicture ? "true" : "false";
            command.Arguments["Name"] = newAttendee.DisplayName;
            command.Arguments["IsRemoteControlled"] = newAttendee.IsBeingRemoteControlled ? "true" : "false";
            SendEventToAttendees(sessionId, command);

            return newAttendee;
        }

        public bool SetRemoteControl(string sessionId, string remoteControl)
        {
            if (sessionId == null) { return false; }
            if (sessionId == remoteControl) { remoteControl = null; }
            Attendee attendee = GetAttendee(sessionId);
            if (attendee == null) { return false; }

            Log("Request set remote control in " + Name + ": " + sessionId + " to control " + remoteControl);
            
            if (attendee.RemoteControl == remoteControl)
            {
                //sessionId is already controlling remoteControl
                return true;
            }

            if (attendee.RemoteControl != null)
            {
                //Attendee loses control over its former RemoteControl session, since we're replacing it
                Attendee formerTarget = GetAttendee(attendee.RemoteControl);
                if (formerTarget != null)
                {
                    formerTarget.IsBeingRemoteControlled = false;
                    Log("Former target " + formerTarget.Id + " is now marked as not remote controlled.");

                    GeneralCommand updateRemoteControlled = new GeneralCommand() { Name = "PartyUpdateRemoteControlled" };
                    updateRemoteControlled.Arguments["Name"] = formerTarget.DisplayName;
                    updateRemoteControlled.Arguments["Status"] = "false";
                    SendEventToAttendees(null, updateRemoteControlled);
                }
                if (_attendeeTargetMap.ContainsKey(attendee.RemoteControl))
                {
                    _attendeeTargetMap.Remove(attendee.RemoteControl);
                }
            }

            Attendee newTarget = null;
            if (remoteControl != null)
            {
                //Attendee will control remoteControl
                newTarget = GetAttendee(remoteControl);
                if (newTarget != null)
                {
                    newTarget.IsBeingRemoteControlled = true;
                    Log("New target " + newTarget.Id + " is now marked as remote controlled.");

                    GeneralCommand updateRemoteControlled = new GeneralCommand() { Name = "PartyUpdateRemoteControlled" };
                    updateRemoteControlled.Arguments["Name"] = newTarget.DisplayName;
                    updateRemoteControlled.Arguments["Status"] = "true";
                    SendEventToAttendees(null, updateRemoteControlled);
                }
                _attendeeTargetMap.Add(remoteControl, attendee);
            }

            attendee.RemoteControl = remoteControl;

            //Log("Target map after operation: " + ControlMapForDisplay());
            return true;
        }

        public Attendee AttendeePart(string sessionId)
        {
            Attendee attendee = _attendees[sessionId];
            bool result = _attendees.Remove(sessionId);
            
            foreach (string eachId in _attendeeTargetMap.Keys)
            {
                if (_attendeeTargetMap[eachId].Id == sessionId)
                {
                    _attendeeTargetMap.Remove(eachId);
                }
            }

            GeneralCommand command = new GeneralCommand() { Name = "PartyLeave" };
            command.Arguments["Name"] = attendee.DisplayName;
            command.Arguments["IsHosting"] = attendee.IsHost ? "true" : "false";
            SendEventToAttendees(sessionId, command);

            if (attendee.IsHost && _attendees.Count > 0)
            {
                _attendees.First().Value.IsHost = true;

                GeneralCommand hostnameCommand = new GeneralCommand() { Name = "PartyUpdateHost" };
                hostnameCommand.Arguments["Host"] = _attendees.First().Value.DisplayName;
                SendEventToAttendees(null, hostnameCommand);
            }
            return result ? attendee : null;
        }

        public Attendee GetAttendee(string sessionId)
        {
            return _attendees.ContainsKey(sessionId) ? _attendees[sessionId] : null;
        }

        public string ControlMapForDisplay()
        {
            List<string> items = new List<string>();
            foreach (string key in _attendeeTargetMap.Keys)
            {
                items.Add(key + " => " + _attendeeTargetMap[key].Id);
            }
            return String.Join(", ", items.ToArray());
        }

        public Attendee GetAttendeeByTarget(string sessionId)
        {
            while (_attendeeTargetMap.ContainsKey(sessionId))
            {
                sessionId = _attendeeTargetMap[sessionId].Id;
            }
            return GetAttendee(sessionId);
        }

        public Attendee GetAttendeeByDisplayName(string displayName)
        {
            foreach (Attendee attendee in _attendees.Values)
            {
                if (attendee.DisplayName.ToLower() == displayName.ToLower())
                {
                    return attendee;
                }
            }
            return null;
        }

        public List<User> GetAttendingUsers()
        {
            HashSet<string> userIds = new HashSet<string>();
            foreach (Attendee attendee in _attendees.Values)
            {
                userIds.Add(attendee.UserId);
            }
            return userIds.ToList<string>().Select(userId => _userManager.GetUserById(userId)).ToList();
        }

        public void SetStates(AttendeeState state)
        {
            foreach (Attendee attendee in _attendees.Values)
            {
                attendee.State = state;
            }
        }

        public void SetPlayerStates(AttendeeState? stateForPlayers, AttendeeState? stateForRemoteControlled)
        {
            foreach (Attendee attendee in _attendees.Values)
            {
                if (attendee.IsHost)
                {
                    continue;
                }
                else if (!attendee.IsBeingRemoteControlled)
                {
                    if (stateForPlayers != null)
                    {
                        attendee.State = (AttendeeState)stateForPlayers;
                    }
                }
                else if (stateForRemoteControlled != null)
                {
                    attendee.State = (AttendeeState)stateForRemoteControlled;
                }
            }
        }

        public bool AreAllStates(AttendeeState state)
        {
            foreach (Attendee attendee in _attendees.Values)
            {
                if (attendee.State != state)
                {
                    return false;
                }
            }
            return true;
        }

        public bool AreAllInSync(Attendee reference)
        {
            foreach (Attendee attendee in _attendees.Values)
            {
                if (attendee.Id == reference.Id) { continue; }
                if (attendee.IsOutOfSync(reference.EstimatedPositionTicks))
                {
                    return false;
                }
            }
            return true;
        }

        public void ResetPositionTicks(long? positionTicks)
        {
            foreach (Attendee attendee in _attendees.Values)
            {
                attendee.PositionTicks = (long)positionTicks;
            }
        }

        public void IgnoreNext(ProgressEvent evt)
        {
            foreach (Attendee attendee in AttendeePlayers)
            {
                attendee.IgnoreNext(evt);
            }
        }

        public void ClearIgnores()
        {
            foreach (Attendee attendee in _attendees.Values)
            {
                attendee.ClearIgnores();
                attendee.IsPaused = false;
            }
        }

        public void ClearOngoingInformation()
        {
            CurrentQueue = null;
            CurrentIndex = 0;
            MediaSourceId = null;
            AudioStreamIndex = null;
            SubtitleStreamIndex = null;
        }

        public void SetHost(Attendee attendee, bool state)
        {
            if (LateClearHost != null)
            {
                LateClearHost.Dispose();
                LateClearHost = null;
            }

            if (attendee.IsHost != state)
            {
                attendee.IsHost = state;

                GeneralCommand hostnameCommand = new GeneralCommand() { Name = "PartyUpdateHost" };
                hostnameCommand.Arguments["Host"] = state ? attendee.DisplayName : "";
                SendEventToAttendees(null, hostnameCommand);
            }
        }

        public void ClearHost()
        {
            Attendee host = Host;
            if (host == null) { return; }
            
            SetHost(host, false);
            PreviousItem = null;

            ClearOngoingInformation();
            ClearIgnores();
            
            //Just to be sure
            PlaystateRequest stop = new PlaystateRequest() { Command = PlaystateCommand.Stop };
            foreach (Attendee eachAttendee in Attendees)
            {
                eachAttendee.State = AttendeeState.Idle;
                eachAttendee.ResetAccuracy();
                eachAttendee.CurrentMediaItemId = null;
                if (eachAttendee.Id == host.Id) { continue; }
                _partyManager.SendPlaystateCommandToAttendeeTarget(eachAttendee, stop);
            }
        }

        public void SendEventToAttendees(string except, GeneralCommand request)
        {
            foreach (Attendee attendee in Attendees)
            {
                if (except != null && attendee.Id == except) { continue; }
                try
                {
                    _sessionManager.SendGeneralCommand(null, attendee.Id, request, new System.Threading.CancellationToken());
                }
                catch
                {
                    LogWarning("Failed to send General command (event) to " + attendee.TargetId + ": " + request.Name);
                }
            }
        }

        public void SendEventToSingleAttendee(Attendee attendee, GeneralCommand request)
        {
            if (attendee == null || attendee.Party != this) { return; }
            try
            {
                _sessionManager.SendGeneralCommand(null, attendee.Id, request, new System.Threading.CancellationToken());
            }
            catch
            {
                LogWarning("Failed to send General command (event) to " + attendee.TargetId + ": " + request.Name);
            }
        }

        public void SendSyncStart()
        {
            SendEventToAttendees(null, new GeneralCommand() { Name = "PartySyncStart" });
        }

        public void SendSyncWaiting(Attendee attendee)
        {
            GeneralCommand waiting = new GeneralCommand() { Name = "PartySyncWaiting" };
            waiting.Arguments["Name"] = attendee.DisplayName;
            SendEventToAttendees(null, waiting);
        }

        public void SendSyncReset(Attendee attendee)
        {
            GeneralCommand reset = new GeneralCommand() { Name = "PartySyncReset" };
            reset.Arguments["Name"] = attendee.DisplayName;
            SendEventToAttendees(null, reset);
        }

        public void SendSyncEnd()
        {
            SendEventToAttendees(null, new GeneralCommand() { Name = "PartySyncEnd" });
        }

        public void SendLogMessageToAttendees(string type, string subject)
        {
            GeneralCommand logMessage = new GeneralCommand() { Name = "PartyLogMessage" };
            logMessage.Arguments["Type"] = type;
            logMessage.Arguments["Subject"] = subject;
            SendEventToAttendees(null, logMessage);
            _partyManager.BroadcastToBridges("GeneralCommand", Name, logMessage);
        }

    }
}
