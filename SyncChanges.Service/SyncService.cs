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
using System.ServiceProcess;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace SyncChanges.Service
{
    public partial class SyncChanges : ServiceBase
    {
        static Logger Log = LogManager.GetCurrentClassLogger();
        private Synchronizer Synchronizer;
        private CancellationTokenSource CancellationTokenSource;
        private Task SyncTask;

        public SyncChanges()
        {
            InitializeComponent();
        }

        protected override async void OnStart(string[] args)
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
                var timeout = int.Parse(ConfigurationManager.AppSettings["Timeout"]);
                var interval = int.Parse(ConfigurationManager.AppSettings["Interval"]);
                var dryRun = ConfigurationManager.AppSettings["DryRun"].Equals("true", StringComparison.OrdinalIgnoreCase);

                CancellationTokenSource = new CancellationTokenSource();
                Synchronizer = new Synchronizer(config) { Timeout = timeout, Interval = interval, DryRun = dryRun };
                SyncTask = Task.Factory.StartNew(() => Synchronizer.SyncLoop(CancellationTokenSource.Token), TaskCreationOptions.LongRunning);
                await SyncTask;
            }
            catch (Exception ex)
            {
                ExitCode = 1064;
                Log.Error(ex, $"Error synchronizing databases");
                throw;
            }
        }

        protected override void OnStop()
        {
            CancellationTokenSource.Cancel();
            SyncTask.Wait();
        }
    }
}
