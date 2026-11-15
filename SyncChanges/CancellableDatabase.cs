using NLog;
using NPoco;
using System;
using System.Data.Common;
using System.Threading;

namespace SyncChanges
{
    /// <summary>
    /// A database whose commands are cancelled on the server when a cancellation token is signaled while they run.
    /// </summary>
    internal sealed class CancellableDatabase(string connectionString, DatabaseType databaseType, DbProviderFactory provider, CancellationToken token)
        : Database(connectionString, databaseType, provider)
    {
        static readonly Logger Log = LogManager.GetLogger(typeof(Synchronizer).FullName);

        private CancellationTokenRegistration Registration;

        /// <summary>
        /// Gets the cancellation token that cancels the running command.
        /// </summary>
        public CancellationToken Token { get; } = token;

        /// <summary>
        /// Calls the given cancel method of a command (or batch) when the token is signaled while it runs, which cancels
        /// it on the server. Dispose the result once the command has finished.
        /// </summary>
        public CancellationTokenRegistration CancelOnToken(Action cancel)
        {
            Token.ThrowIfCancellationRequested();
            // the command may have been disposed if the registration was not, e.g. after an exception NPoco does not report
            return Token.Register(() =>
            {
                try { cancel(); }
                catch (Exception ex) { Log.Debug(ex, "Error cancelling command"); }
            });
        }

        protected override void OnExecutingCommand(DbCommand cmd)
        {
            base.OnExecutingCommand(cmd);
            Registration.Dispose();
            Registration = CancelOnToken(cmd.Cancel);
        }

        protected override void OnExecutedCommand(DbCommand cmd)
        {
            Registration.Dispose();
            base.OnExecutedCommand(cmd);
        }

        protected override void OnException(Exception exception)
        {
            Registration.Dispose();
            base.OnException(exception);
        }
    }
}
