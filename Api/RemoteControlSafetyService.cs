using MediaBrowser.Common;
using MediaBrowser.Controller.Net;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Serialization;
using MediaBrowser.Model.Services;
using MediaBrowser.Model.Session;
using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
namespace EmbyParty.Api
{
    public sealed class RemoteControlSafetyReturn
    {
        public bool IsSafe { get; set; }
    }

    [Route("/Party/RemoteControlSafety", "GET", Summary = "Checks if it's safe to remote control a session")]
    [Authenticated]
    public sealed class RemoteControlSafety : IReturn<RemoteControlSafetyReturn>
    {
        public string RemoteControl { get; set; }
    }

    public class RemoteControlSafetyService : BasePartyService
    {
        public RemoteControlSafetyService(ILogManager logManager, IApplicationHost applicationHost, ISessionContext sessionContext) : base(logManager, applicationHost, sessionContext) { }

        public object Get(RemoteControlSafety request)
        {
            SessionInfo session = GetSession(SessionContext);

            Party party = PartyManager.GetAttendeeParty(session.Id);
            if (party == null) { return new RemoteControlSafetyReturn() { IsSafe = true }; }

            Attendee attendee = party.GetAttendee(session.Id);

            bool result = PartyManager.IsTargetSafe(attendee, request.RemoteControl);

            return new RemoteControlSafetyReturn() { IsSafe = result };
        }

    }
}
