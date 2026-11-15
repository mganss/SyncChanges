using Humanizer;
using System;
using System.Diagnostics;

namespace SyncChanges
{
    /// <summary>
    /// Tracks the progress of a run over a known number of items and estimates when it will be complete.
    /// </summary>
    /// <param name="total">The total number of items.</param>
    /// <param name="interval">The minimum time between two reports. Default is 10 seconds.</param>
    internal sealed class Progress(int total, TimeSpan? interval = null)
    {
        private readonly Stopwatch Stopwatch = Stopwatch.StartNew();
        private readonly TimeSpan Interval = interval ?? TimeSpan.FromSeconds(10);
        private TimeSpan LastReport;

        /// <summary>
        /// Gets the time since the run started.
        /// </summary>
        public TimeSpan Elapsed => Stopwatch.Elapsed;

        /// <summary>
        /// Determines whether a report is due, i.e. at least the interval has passed since the last one and the run
        /// is not complete, and if so, describes the progress and the estimated completion time.
        /// </summary>
        /// <param name="done">The number of items done so far.</param>
        /// <param name="report">The description, e.g. <c>(25%, estimated completion at 14:05:10, in 3 minutes)</c>.</param>
        /// <returns><c>true</c> if a report is due; otherwise, <c>false</c>.</returns>
        public bool Due(int done, out string report)
        {
            report = null;
            var elapsed = Stopwatch.Elapsed;
            if (done <= 0 || done >= total || elapsed - LastReport < Interval) return false;

            LastReport = elapsed;
            report = Describe(done, total, elapsed, DateTime.Now);
            return true;
        }

        /// <summary>
        /// Describes the progress and the completion time estimated from the average rate so far.
        /// </summary>
        internal static string Describe(int done, int total, TimeSpan elapsed, DateTime now)
        {
            var remaining = TimeSpan.FromTicks((long)(elapsed.Ticks * ((double)(total - done) / done)));
            var completion = now + remaining;
            var format = completion.Date == now.Date ? "T" : "G";
            return $"({(double)done / total:P0}, estimated completion at {completion.ToString(format)}, in {remaining.Humanize(2, minUnit: TimeUnit.Second)})";
        }
    }
}
