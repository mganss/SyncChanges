using Mono.Options;
using Newtonsoft.Json;
using NLog;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace SyncChanges.Console
{
    class Program
    {
        static readonly Logger Log = LogManager.GetCurrentClassLogger();

        List<string> ConfigFiles;
        bool Error = false;
        bool Loop = false;

        // set by options; null where an option is not given, so the configuration file's setting or the default applies
        bool? DryRun;
        int? Timeout;
        int? Interval;
        int? BatchSize;

        static int Main(string[] args)
        {
            try
            {
                System.Console.OutputEncoding = Encoding.UTF8;
                ConfigureLogging();
                var program = new Program();
                var showHelp = false;

                try
                {
                    var options = new OptionSet {
                        { "h|help", "Show this message and exit", v => showHelp = v != null },
                        { "d|dryrun", "Do not alter target databases, only perform a test run", v => program.DryRun = v != null ? true : null },
                        { "t|timeout=", "Database command timeout in seconds", (int v) => program.Timeout = v },
                        { "l|loop", "Perform replication in a loop, periodically checking for changes", v => program.Loop = v != null },
                        { "i|interval=", "Replication interval in seconds (default is 30); only relevant in loop mode", (int v) => program.Interval = v },
                        { "b|batchsize=", "Maximum number of statements sent to a destination database in one round trip (default is 100)", (int v) => program.BatchSize = v },
                        // the options override the settings of a configuration file
                    };

                    program.ConfigFiles = options.Parse(args);

                    if (showHelp)
                    {
                        ShowHelp(options);
                        return 0;
                    }
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "Error parsing command line arguments");
                    return 1;
                }

                if (program.ConfigFiles.Count == 0)
                {
                    Log.Error("No config files supplied");
                    return 1;
                }

                program.Sync();

                return program.Error ? 1 : 0;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "An error has occurred");
                return 2;
            }
        }

        // Installed as a dotnet tool, the executable's folder is in the tool store, where a log file would be hard to
        // find, so the log goes to the user's local application data instead. An NLog.config in the current folder
        // replaces the one of the executable.
        static void ConfigureLogging()
        {
            var isTool = File.Exists(Path.Combine(AppContext.BaseDirectory, "DotnetToolSettings.xml"));
            var logDirectory = isTool
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SyncChanges")
                : AppContext.BaseDirectory;
            GlobalDiagnosticsContext.Set("LogDirectory", logDirectory);

            var localConfig = Path.Combine(Directory.GetCurrentDirectory(), "NLog.config");
            if (File.Exists(localConfig) && !string.Equals(Path.GetFullPath(localConfig), Path.Combine(AppContext.BaseDirectory, "NLog.config"), StringComparison.OrdinalIgnoreCase))
                LogManager.Configuration = new NLog.Config.XmlLoggingConfiguration(localConfig);
        }

        static void ShowHelp(OptionSet p)
        {
            System.Console.WriteLine("Usage: SyncChanges [OPTION]... CONFIGFILE...");
            System.Console.WriteLine("Replicate database changes.");
            System.Console.WriteLine();
            System.Console.WriteLine("Options:");
            p.WriteOptionDescriptions(System.Console.Out);
            System.Console.WriteLine();
            System.Console.WriteLine($"Logs to the console and to {Path.Combine(GlobalDiagnosticsContext.Get("LogDirectory"), "log.txt")};");
            System.Console.WriteLine("an NLog.config in the current folder replaces the default logging configuration.");
        }

        void Sync()
        {
            var synchronizers = new List<(string ConfigFile, Synchronizer Synchronizer)>();

            foreach (var configFile in ConfigFiles)
            {
                try
                {
                    var config = JsonConvert.DeserializeObject<Config>(File.ReadAllText(configFile));
                    var synchronizer = new Synchronizer(config);
                    if (DryRun.HasValue) synchronizer.DryRun = DryRun.Value;
                    if (Timeout.HasValue) synchronizer.Timeout = Timeout.Value;
                    if (Interval.HasValue) synchronizer.Interval = Interval.Value;
                    if (BatchSize.HasValue) synchronizer.BatchSize = BatchSize.Value;
                    synchronizers.Add((configFile, synchronizer));
                }
                catch (Exception ex)
                {
                    Log.Error(ex, $"Error reading configuration file {configFile}");
                    Error = true;
                }
            }

            if (!Loop)
            {
                foreach (var (configFile, synchronizer) in synchronizers)
                {
                    try
                    {
                        var success = synchronizer.Sync();
                        Error = Error || !success;
                    }
                    catch (Exception ex)
                    {
                        Log.Error(ex, $"Error synchronizing databases for configuration {configFile}");
                        Error = true;
                    }
                }
            }
            else
            {
                // a loop never ends by itself, so the loops of all configuration files run at the same time,
                // until Ctrl+C stops them all
                using var cancellationTokenSource = new CancellationTokenSource();

                void Cancel(object sender, ConsoleCancelEventArgs e)
                {
                    e.Cancel = true;
                    cancellationTokenSource.Cancel();
                }

                System.Console.CancelKeyPress += Cancel;

                try
                {
                    var loops = synchronizers.Select(s => Task.Factory.StartNew(() =>
                    {
                        try
                        {
                            s.Synchronizer.SyncLoop(cancellationTokenSource.Token);
                        }
                        catch (Exception ex)
                        {
                            Log.Error(ex, $"Error synchronizing databases for configuration {s.ConfigFile}");
                            Error = true;
                        }
                    }, TaskCreationOptions.LongRunning)).ToArray();

                    Task.WaitAll(loops);
                }
                finally
                {
                    System.Console.CancelKeyPress -= Cancel;
                }
            }
        }
    }
}
