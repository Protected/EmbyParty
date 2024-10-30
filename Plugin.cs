using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Serialization;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;

namespace EmbyParty
{
    public class Plugin : BasePlugin<PluginConfiguration>
    {

        private string[] extractResourcesNames = {
            "EmbyParty.dashboard_ui"
        };

        private string _resourcesPath;
        private string _pluginDataPath;
        private ILogger _logger;

        private List<Action<PartyManager>> _partyManagerSetHandlers = new List<Action<PartyManager>>();

        private PartyManager _partyManager;
        public PartyManager PartyManager {
            get => _partyManager;
            set {
                _partyManager = value;
                foreach (Action<PartyManager> action in _partyManagerSetHandlers)
                {
                    action(value);
                }
                _partyManagerSetHandlers.Clear();
            }
        }

        public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer, ILogManager logManager) : base(applicationPaths, xmlSerializer)
        {
            _resourcesPath = applicationPaths.ProgramSystemPath;
            _logger = logManager.GetLogger("Party");
            _pluginDataPath = Path.Combine(applicationPaths.DataPath, "EmbyParty");
        }

        public override string Name => "Emby Party";

        public override string Description => "Easy to use solution for synchronizing user playback experiences";

        public override Guid Id => new Guid("A7572F2B-B3E5-4055-945D-640A57D2C09B");

        public void OnPartyManagerSet(Action<PartyManager> action)
        {
            if (PartyManager != null)
            {
                action(PartyManager);
            }
            else
            {
                _partyManagerSetHandlers.Add(action);
            }
        }

        public override void OnUninstalling()
        {
            base.OnUninstalling();

            CleanupResources(true);
        }

        public void ExtractResources()
        {
            Assembly assembly = Assembly.GetAssembly(typeof(Plugin));
            bool update = ReadResourceVersion() != Version.ToString();

            foreach (string resourceName in assembly.GetManifestResourceNames())
            {
                if (extractResourcesNames.Where(prefix => resourceName.StartsWith(prefix)).Count() == 0) { continue; }

                string outputPath = Path.Combine(_resourcesPath, ConvertResourceNameToPath(assembly, resourceName));

                if (!update && File.Exists(outputPath)) { continue; }

                _logger.Info("Extracting resource " + resourceName);

                Directory.CreateDirectory(Path.GetDirectoryName(outputPath));

                using (Stream resourceStream = assembly.GetManifestResourceStream(resourceName))
                using (FileStream fileStream = new FileStream(outputPath, FileMode.Create, FileAccess.Write))
                {
                    resourceStream?.CopyTo(fileStream);
                }
            }

            if (update) { WriteResourceVersion(); }
        }

        private void CleanupResources(bool andDirectory)
        {
            Assembly assembly = Assembly.GetAssembly(typeof(Plugin));
            foreach (string resourceName in assembly.GetManifestResourceNames())
            {
                if (extractResourcesNames.Where(prefix => resourceName.StartsWith(prefix)).Count() == 0) { continue; }

                string outputPath = Path.Combine(_resourcesPath, ConvertResourceNameToPath(assembly, resourceName));

                _logger.Info("Deleting resource " + resourceName);

                File.Delete(outputPath);

                string dir = Path.GetDirectoryName(outputPath);
                if (andDirectory && Directory.GetFileSystemEntries(dir).Length == 0)
                {
                    Directory.Delete(dir);
                }
            }
        }

        private string ConvertResourceNameToPath(Assembly assembly, string resourceName)
        {
            string assemblyNamespace = assembly.GetName().Name;
            string relativePath = resourceName.Replace(assemblyNamespace + ".", "");

            int lastDotIndex = relativePath.LastIndexOf('.');
            if (lastDotIndex > -1)
            {
                relativePath = relativePath.Substring(0, lastDotIndex)
                        .Replace('.', Path.DirectorySeparatorChar)
                        .Replace('_', '-')
                    + relativePath.Substring(lastDotIndex);
            }

            return relativePath;
        }

        private void WriteResourceVersion()
        {
            Directory.CreateDirectory(_pluginDataPath);
            File.WriteAllText(Path.Combine(_pluginDataPath, "rversion.txt"), Version.ToString());
        }

        private string ReadResourceVersion()
        {
            try
            {
                return File.ReadAllText(Path.Combine(_pluginDataPath, "rversion.txt"));
            }
            catch
            {
                return "";
            }
        }

    }
}
