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
        bool DryRun;
        bool Error = false;
        int Timeout = 0;
        bool Loop = false;
        int Interval = 30;

        static int Main(string[] args)
        {
            try
            {
                System.Console.OutputEncoding = Encoding.UTF8;
                var program = new Program();
                var showHelp = false;

                try
                {
                    var options = new OptionSet {
                        { "h|help", "Show this message and exit", v => showHelp = v != null },
                        { "d|dryrun", "Do not alter target databases, only perform a test run", v => program.DryRun = v != null },
                        { "t|timeout=", "Database command timeout in seconds", (int v) => program.Timeout = v },
                        { "l|loop", "Perform replication in a loop, periodically checking for changes", v => program.Loop = v != null },
                        { "i|interval=", "Replication interval in seconds (default is 30); only relevant in loop mode", (int v) => program.Interval = v },
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

                if (!program.ConfigFiles.Any())
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

        static void ShowHelp(OptionSet p)
        {
            System.Console.WriteLine("Usage: SyncChanges [OPTION]... CONFIGFILE...");
            System.Console.WriteLine("Replicate database changes.");
            System.Console.WriteLine();
            System.Console.WriteLine("Options:");
            p.WriteOptionDescriptions(System.Console.Out);
        }

        void Sync()
        {
            foreach (var configFile in ConfigFiles)
            {
                Config config = null;

                try
                {
                    config = JsonConvert.DeserializeObject<Config>(File.ReadAllText(configFile));
                }
                catch (Exception ex)
                {
                    Log.Error(ex, $"Error reading configuration file {configFile}");
                    Error = true;
                    continue;
                }

                try
                {
                    var synchronizer = new Synchronizer(config) { DryRun = DryRun, Timeout = Timeout };
                    if (!Loop)
                    {
                        var success = synchronizer.Sync();
                        Error = Error || !success;
                    }
                    else
                    {
                        synchronizer.Interval = Interval;
                        using var cancellationTokenSource = new CancellationTokenSource();
                        System.Console.CancelKeyPress += (s, e) =>
                        {
                            cancellationTokenSource.Cancel();
                            e.Cancel = true;
                        };
                        synchronizer.SyncLoop(cancellationTokenSource.Token);
                    }
                }
                catch (Exception ex)
                {
                    Log.Error(ex, $"Error synchronizing databases for configuration {configFile}");
                    Error = true;
                }
            }
        }
    }
}
