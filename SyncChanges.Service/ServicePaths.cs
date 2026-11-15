using System;
using System.IO;

namespace SyncChanges.Service
{
    /// <summary>
    /// Where the service finds its configuration and writes its log.
    /// </summary>
    /// <remarks>
    /// From the zip, both are in the folder of the executable, as in earlier versions. Installed as a dotnet tool,
    /// the program is in a folder of its version in the tool store, which <c>dotnet tool update</c> replaces, so both
    /// are in <c>%ProgramData%\SyncChanges</c> instead. The configuration file can also be given with <c>--config</c>,
    /// which the installer stores in the service's command line; the log is then written next to it.
    /// </remarks>
    static class ServicePaths
    {
        /// <summary>
        /// Gets a value indicating whether the program runs from a dotnet tool installation.
        /// </summary>
        public static bool IsTool { get; } = File.Exists(Path.Combine(AppContext.BaseDirectory, "DotnetToolSettings.xml"));

        /// <summary>
        /// Gets the configuration file used if none is given.
        /// </summary>
        public static string DefaultConfigFile => IsTool
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "SyncChanges", "config.json")
            : Path.Combine(AppContext.BaseDirectory, "config.json");

        /// <summary>
        /// Gets the folder of the log file for a configuration file.
        /// </summary>
        public static string LogDirectory(string configFile, bool explicitConfig) =>
            IsTool || explicitConfig ? Path.GetDirectoryName(configFile) : AppContext.BaseDirectory;
    }
}
