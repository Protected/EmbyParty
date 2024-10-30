using MediaBrowser.Common;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Net;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Services;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace EmbyParty.Api
{
    public sealed class PartyStatusReturn
    {
        public string Id { get; set; }
        public PartyInfo CurrentParty { get; set; }
        public List<PartyAttendeeInfo> Attendees { get; set; }
        public long[] CurrentQueue { get; set; }
        public int CurrentIndex { get; set; }
        public string MediaSourceId { get; set; }
        public int? AudioStreamIndex { get; set; }
        public int? SubtitleStreamIndex { get; set; }
        public bool IsPaused { get; set; }
    }

    [Route("/Party/Status", "GET", Summary = "Gets the user's current party status")]
    [Authenticated]
    public sealed class PartyStatus : IReturn<PartyStatusReturn>
    {
    }

    public class PartyStatusService : BasePartyService
    {
        private IUserManager _userManager;

        public PartyStatusService(ILogManager logManager, IApplicationHost applicationHost, ISessionContext sessionContext, IUserManager userManager) : base(logManager, applicationHost, sessionContext)
        {
            _userManager = userManager;
        }

        public object Get(PartyStatus request)
        {
            SessionInfo session = GetSession(SessionContext);

            PartyStatusReturn result = new PartyStatusReturn();

            Party party = PartyManager.GetAttendeeParty(session.Id);
            if (party != null)
            {
                result.Id = party.Id.ToString();
                result.CurrentParty = new PartyInfo() { Name = party.Name, Id = party.Id };
                result.IsPaused = false;
                result.Attendees = new List<PartyAttendeeInfo>();
                foreach (Attendee attendee in party.Attendees)
                {

                    User user = _userManager.GetUserById(attendee.UserId);
                    bool hasPicture = user.GetImages(ImageType.Primary).Count() > 0;

                    if (attendee.IsHost && attendee.IsPaused)
                    {
                        result.IsPaused = true;
                    }

                    result.Attendees.Add(new PartyAttendeeInfo()
                    {
                        UserId = attendee.UserId,
                        HasPicture = hasPicture,
                        Name = attendee.DisplayName,
                        IsHosting = attendee.IsHost,
                        IsMe = attendee.Id == session.Id,
                        IsRemoteControlled = attendee.IsBeingRemoteControlled
                    });
                }
                result.CurrentQueue = party.CurrentQueue;
                result.CurrentIndex = party.CurrentIndex;
                result.MediaSourceId = party.MediaSourceId;
                result.SubtitleStreamIndex = party.SubtitleStreamIndex;
                result.AudioStreamIndex = party.AudioStreamIndex;
            }

            return result;
        }
    }
}
