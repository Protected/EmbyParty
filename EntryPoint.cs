using MediaBrowser.Common;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Serialization;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;

namespace EmbyParty
{
    public class EntryPoint : IServerEntryPoint
    {

        private ILogger _logger;
        private ISessionManager _sessionManager;
        private IUserManager _userManager;
        private ILibraryManager _libraryManager;
        private Plugin _plugin;

        public EntryPoint(ISessionManager sessionManager, IUserManager userManager, ILogManager logManager, IApplicationHost applicationHost, IJsonSerializer jsonSerializer, ILibraryManager libraryManager)
        {
            _logger = logManager.GetLogger("Party");
            _sessionManager = sessionManager;
            _libraryManager = libraryManager;
            _userManager = userManager;
            _plugin = applicationHost.Plugins.OfType<Plugin>().First();
            _plugin.PartyManager = new PartyManager(_sessionManager, _userManager, jsonSerializer, _libraryManager, _logger);
        }

        protected void Log(string message)
        {
            _logger.Info(message);
        }

        public void Run()
        {
            _plugin.ExtractResources();
            _plugin.PartyManager.Run();
        }

        public void Dispose()
        {
            _plugin.PartyManager.Dispose();
        }

    }
}
