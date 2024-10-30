using MediaBrowser.Controller.Entities;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Session;
using MediaBrowser.Model.System;
using System;
using System.Collections.Generic;
using System.Text;
using WebSocketSharp;

namespace EmbyParty
{
    public enum AttendeeState
    {
        Idle,               //Not playing anything.
        WaitForPlay,        //This Guest has been sent a playback order but hasn't reported initiation of playback yet.
        WaitForSeek,        //This Guest has been sent a seek order but hasn't delivered a time update yet. 
        Syncing,            //This Attendee is ready to continue but one or more Guests aren't yet.
        Ready               //Synced (stable) playback in progress.
    }

    public class Attendee
    {
        private const long ACCURACY_START = 20000000;  //2s
        private const long ACCURACY_DECAY = 10000000;  //1s
        public const long ACCURACY_WORST = 70000000;  //7s

        public string Id { get; set; }

        public Party Party { get; set; }

        public string RemoteControl { get; set; }  //Session this attendee is controlling
        public string TargetId { get => SafeTargetId(new List<string>() {Id}); }
        public bool IsBeingRemoteControlled { get; set; }

        public long Ping;  //ms
        public List<long> PendingPing { get; set; }

        public string UserId { get; set; }

        /* Fields used for client-side display */
        public string UserName { get; set; }
        public string DeviceName { get; set; }
        public string DeviceType {  get; set; }
        public string DisplayName {  get; set; }
        /* --- */

        private DateTimeOffset _lastStateChange;
        private AttendeeState _state;
        public AttendeeState State {
            get => _state;
            set {
                _state = value;
                _lastStateChange = DateTimeOffset.UtcNow;
            }
        }
        public long MsSinceStateChange { get => (long)Math.Floor((DateTimeOffset.UtcNow - _lastStateChange).TotalMilliseconds); }

        public string ReportedDeviceId { get; set; }
        public string PlaySessionId { get; set; }
        public long? CurrentMediaItemId { get; set; }
        public long RunTimeTicks { get; set; }

        public bool IsPaused { get; set; }

        public bool IsHost { get; set; }

        private long _positionTicks;
        private DateTimeOffset _lastUpdate;
        public long PositionTicks {
            get => _positionTicks;
            set {
                _positionTicks = value;
                _lastUpdate = DateTimeOffset.UtcNow;
            }
        }
        public DateTimeOffset LastUpdate { get => _lastUpdate; }
        public long TicksSinceUpdate { get => (long)Math.Floor((DateTimeOffset.UtcNow - _lastUpdate).TotalMilliseconds) * 10000; }
        public long EstimatedPositionTicks { get => IsPaused ? PositionTicks : PositionTicks + TicksSinceUpdate; }

        private long _accuracy = ACCURACY_START;

        private HashSet<ProgressEvent> _ignores = new HashSet<ProgressEvent>();

        public Attendee()
        {
            State = AttendeeState.Idle;
            PendingPing = new List<long>();
        }

        public void IgnoreNext(ProgressEvent command)
        {
            _ignores.Add(command);
        }

        public bool ShouldIgnore(ProgressEvent command)
        {
            if (_ignores.Contains(command))
            {
                _ignores.Remove(command);
                return true;
            }
            return false;
        }

        public void ClearIgnores()
        {
            _ignores.Clear();
        }

        public bool IsOutOfSync(long ticks)
        {
            return ticks < EstimatedPositionTicks - _accuracy || ticks > EstimatedPositionTicks + _accuracy;
        }

        public long UpdatePositionTicksFromEstimate()
        {
            PositionTicks = EstimatedPositionTicks;
            return PositionTicks;
        }

        public long LowerAccuracy()
        {
            if (_accuracy < ACCURACY_WORST)
            {
                _accuracy += ACCURACY_DECAY;
            }
            return _accuracy;
        }

        public void ResetAccuracy()
        {
            _accuracy = ACCURACY_START;
        }

        public string SafeTargetId(List<string> except)
        {
            //Prevents endless recursion
            if (RemoteControl != null)
            {
                Attendee target = Party.GetAttendee(RemoteControl);
                if (target != null && !except.Contains(target.Id))
                {
                    except.Add(target.Id);
                    return target.SafeTargetId(except);
                }
                return RemoteControl;
            }
            return Id;
        }

    }
}
