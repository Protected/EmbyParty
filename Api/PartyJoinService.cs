using Emby.Web.GenericEdit.Actions;
using MediaBrowser.Common;
using MediaBrowser.Controller.Net;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Services;
using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;

namespace EmbyParty.Api
{
    public sealed class PartyJoinResult
    {
        public bool Success { get; set; }
        public string Reason { get; set; }
    }

    [Route("/Party/Join", "POST", Summary = "Joins or creates a party")]
    [Authenticated]
    public sealed class PartyJoin: IReturn<PartyJoinResult>
    {
        public long? Id { get; set; }
        public string Name { get; set; }
        public string RemoteControl { get; set; }
    }

    public class PartyJoinService : BasePartyService
    {
        public PartyJoinService(ILogManager logManager, IApplicationHost applicationHost, ISessionContext sessionContext) : base(logManager, applicationHost, sessionContext) { }

        public async Task<object> Post(PartyJoin request)
        {
            SessionInfo session = GetSession(SessionContext);

            if (request.Id != null)
            {
                Party joinedParty = await PartyManager.AddToParty(session.Id, (long)request.Id, session);
                if (joinedParty == null)
                {
                    return (object)new PartyJoinResult() { Success = false, Reason = "Party doesn't exist or user doesn't have permission to access media." };
                }

                if (request.RemoteControl != null)
                {
                    joinedParty.SetRemoteControl(session.Id, request.RemoteControl);
                }

                return (object)new PartyJoinResult() { Success = true };
            }
            else if (request.Name != null && request.Name.Length > 0)
            {
                string name = request.Name.Substring(0, Math.Min(24, request.Name.Length));
                if (PartyManager.GetPartyByName(name) != null)
                {
                    return (object)new PartyJoinResult() { Success = false, Reason = "There is already a party with that name." };
                }
                Party createdParty = await PartyManager.CreateParty(session.Id, name, session);
                if (createdParty == null)
                {
                    return (object)new PartyJoinResult() { Success = false, Reason = "Unble to create party with this user." };
                }

                if (request.RemoteControl != null)
                {
                    createdParty.SetRemoteControl(session.Id, request.RemoteControl);
                }

                return (object)new PartyJoinResult() { Success = true };
            }

            return (object)new PartyJoinResult() { Success = false, Reason = "Either the party ID or a party name must be provided." };
        }
    }
}
