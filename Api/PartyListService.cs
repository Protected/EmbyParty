using MediaBrowser.Controller.Api;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Net;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Services;
using System;
using System.Collections.Generic;
using System.Text;
using MediaBrowser.Controller.Session;
using System.IO;
using System.Linq;
using System.Transactions;
using MediaBrowser.Common;

namespace EmbyParty.Api
{
    public sealed class PartyInfo
    {
        public long Id { get; set; }
        public string Name { get; set; }
    }

    [Route("/Party/List", "GET", Summary = "Lists existing watch parties")]
    [Authenticated]
    public sealed class PartyList : IReturn<List<PartyInfo>>
    {
    }

    public class PartyListService : BasePartyService
    {
        public PartyListService(ILogManager logManager, IApplicationHost applicationHost, ISessionContext sessionContext) : base(logManager, applicationHost, sessionContext) { }

        public object Get(PartyList request)
        {
            List<PartyInfo> results = new List<PartyInfo>();
            foreach (Party party in PartyManager.Parties)
            {
                results.Add(new PartyInfo { Id = party.Id, Name = party.Name });
            }
            return ToOptimizedResult(results);
        }

    }

}
