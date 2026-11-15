using Mono.Options;
using NLog;
using System;
using System.IO;
using System.ServiceProcess;

namespace SyncChanges.Service
{
    static class Program
    {
        /// <summary>
        /// The main entry point for the application.
        /// </summary>
        static int Main(string[] args)
        {
            if (Installer.IsCommand(args))
                return Installer.Run(args);

            // started from a command prompt rather than by the service control manager
            if (Environment.UserInteractive)
            {
                Installer.ShowHelp();
                Console.WriteLine();
                Console.WriteLine("Run SyncChanges.Service help for the options.");
                return 1;
            }

            // the arguments of the service's command line, as stored by the installer
            string configFile = null;
            new OptionSet { { "c|config=", v => configFile = v } }.Parse(args);
            var explicitConfig = configFile != null;
            configFile = Path.GetFullPath(configFile ?? ServicePaths.DefaultConfigFile);

            ConfigureLogging(ServicePaths.LogDirectory(configFile, explicitConfig));
            ServiceBase.Run(new SyncChanges(configFile));
            return 0;
        }

        // An NLog.config in the folder of the log replaces the one of the executable.
        static void ConfigureLogging(string logDirectory)
        {
            GlobalDiagnosticsContext.Set("LogDirectory", logDirectory);

            var localConfig = Path.Combine(logDirectory, "NLog.config");
            if (File.Exists(localConfig) && !string.Equals(Path.GetFullPath(localConfig), Path.Combine(AppContext.BaseDirectory, "NLog.config"), StringComparison.OrdinalIgnoreCase))
                LogManager.Configuration = new NLog.Config.XmlLoggingConfiguration(localConfig);
        }
    }
}
