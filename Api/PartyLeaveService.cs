using MediaBrowser.Common;
using MediaBrowser.Controller.Api;
using MediaBrowser.Controller.Net;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Services;
using System;
using System.Collections.Generic;
using System.Text;

namespace EmbyParty.Api
{
    [Route("/Party/Leave", "POST", Summary = "Leaves the current watch party, if any")]
    [Authenticated]
    public sealed class PartyLeave : IReturnVoid
    {
    }

    public class PartyLeaveService : BasePartyService
    {
        public PartyLeaveService(ILogManager logManager, IApplicationHost applicationHost, ISessionContext sessionContext) : base(logManager, applicationHost, sessionContext) { }

        public object Post(PartyLeave request)
        {
            SessionInfo session = GetSession(SessionContext);

            PartyManager.RemoveFromParty(session.Id);

            return null; 
        }
    }
}
