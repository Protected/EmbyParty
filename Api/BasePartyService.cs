using MediaBrowser.Controller.Api;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Querying;
using MediaBrowser.Model.Services;
using MediaBrowser.Common.Progress;
using MediaBrowser.Controller.Providers;
using System;
using System.Collections.Generic;
using System.Text;
using System.Xml.Linq;
using System.Threading;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Logging;
using MediaBrowser.Controller.Plugins;
using System.Threading.Tasks;
using Emby.Media.Model.GraphModel;
using System.IO;
using System.Diagnostics;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Common;
using System.Linq;
using MediaBrowser.Controller.Net;

namespace EmbyParty.Api
{
    public abstract class BasePartyService : BaseApiService
    {

        private ILogger _logger;

        protected PartyManager PartyManager;

        protected ISessionContext SessionContext;

        public BasePartyService(ILogManager logManager, IApplicationHost applicationHost, ISessionContext sessionContext)
        {
            _logger = logManager.GetLogger("Party");
            Plugin plugin = applicationHost.Plugins.OfType<Plugin>().First();
            PartyManager = plugin.PartyManager;
            SessionContext = sessionContext;
        }

        protected void Log(string message)
        {
            _logger.Info(message);
        }

    }

}
