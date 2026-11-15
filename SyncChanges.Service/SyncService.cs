using Newtonsoft.Json;
using NLog;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Configuration;
using System.Data;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.ServiceProcess;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace SyncChanges.Service
{
    public partial class SyncChanges : ServiceBase
    {
        static readonly Logger Log = LogManager.GetCurrentClassLogger();
        private Synchronizer Synchronizer;
        private CancellationTokenSource CancellationTokenSource;
        private Task SyncTask;

        public SyncChanges()
        {
            InitializeComponent();
        }

        // OnStart must return once the service is running. An exception thrown here reports the start as failed.
        protected override void OnStart(string[] args)
        {
            Config config = null;

            try
            {
                var path = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
                config = JsonConvert.DeserializeObject<Config>(File.ReadAllText(Path.Combine(path, "config.json")));
            }
            catch (Exception ex)
            {
                ExitCode = 1064;
                Log.Error(ex, $"Error reading configuration file config.json");
                throw;
            }

            try
            {
                // The settings in config.json take precedence over those in SyncChanges.Service.dll.config,
                // which are kept for installations configured there.
                CancellationTokenSource = new();
                Synchronizer = new Synchronizer(config);
                if (config.Timeout == null && AppSetting("Timeout") is string timeout)
                    Synchronizer.Timeout = int.Parse(timeout);
                if (config.Interval == null && AppSetting("Interval") is string interval)
                    Synchronizer.Interval = int.Parse(interval);
                if (config.DryRun == null && AppSetting("DryRun") is string dryRun)
                    Synchronizer.DryRun = dryRun.Equals("true", StringComparison.OrdinalIgnoreCase);
                if (config.BatchSize == null && AppSetting("BatchSize") is string batchSize)
                    Synchronizer.BatchSize = int.Parse(batchSize);
            }
            catch (Exception ex)
            {
                ExitCode = 1064;
                Log.Error(ex, $"Error setting up synchronization");
                throw;
            }

            SyncTask = Task.Factory.StartNew(() => Synchronizer.SyncLoop(CancellationTokenSource.Token), TaskCreationOptions.LongRunning);

            // the loop handles the errors of a replication itself, so this is an error it cannot recover from
            SyncTask.ContinueWith(t =>
            {
                Log.Error(t.Exception, "Error synchronizing databases");
                ExitCode = 1064;
                Stop();
            }, TaskContinuationOptions.OnlyOnFaulted);
        }

        private static string AppSetting(string key) =>
            ConfigurationManager.AppSettings[key] is string value && !string.IsNullOrWhiteSpace(value) ? value : null;

        protected override void OnStop()
        {
            // null if the service failed to start
            CancellationTokenSource?.Cancel();

            try
            {
                // cancelling stops the running command and rolls back its transaction, which can take a while;
                // the service control manager gives up on a service that does not stop within its timeout
                while (SyncTask != null && !SyncTask.Wait(TimeSpan.FromSeconds(10)))
                    RequestAdditionalTime(15000);
            }
            catch (AggregateException)
            {
                // logged when the task failed
            }
        }
    }
}
