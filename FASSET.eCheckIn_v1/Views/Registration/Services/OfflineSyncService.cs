using System;
using System.Threading;
using System.Web.Hosting;
using FASSET.eCheckIn_v1.Data_Access_Layer;

namespace FASSET.eCheckIn_v1.Services
{
    public class SyncResult
    {
        public int Synced { get; set; }
        public int AlreadyThere { get; set; }
        public int Failed { get; set; }
        public bool StillOffline { get; set; }
        public bool Skipped { get; set; }
        public int PendingAfter { get; set; }

        // Set when syncing is blocked by a setup problem (not by any one check-in).
        public string Message { get; set; }
    }

    // Drains the offline queue into mst_dailyCheckIn_tbl. Triggered three ways:
    //   1. right after any successful live check-in (proof the DB is back),
    //   2. a self-stopping timer that starts when something is queued,
    //   3. the "Sync now" button on the Settings page.
    // Only one sync runs at a time.
    public static class OfflineSyncService
    {
        private static readonly object SyncLock = new object();
        private static readonly object TimerLock = new object();
        private static System.Threading.Timer _timer;

        public static SyncResult SyncPending()
        {
            var result = new SyncResult();

            if (!Monitor.TryEnter(SyncLock))
            {
                result.Skipped = true;
                result.PendingAfter = OfflineCheckInQueue.PendingCount();
                return result;
            }

            try
            {
                var db = new dal();

                foreach (string path in OfflineCheckInQueue.GetPendingFiles())
                {
                    OfflineCheckIn item;
                    if (!OfflineCheckInQueue.TryRead(path, out item))
                    {
                        OfflineCheckInQueue.MarkUnreadableFailed(path);
                        result.Failed++;
                        continue;
                    }

                    try
                    {
                        bool alreadyExisted;
                        int code = db.ReplayOfflineCheckIn(item, out alreadyExisted);
                        OfflineCheckInQueue.CloseCircuit();

                        if (code == 99)
                        {
                            OfflineCheckInQueue.MarkProcessed(path, item, "Written to the database with its original check-in time.");
                            result.Synced++;
                        }
                        else if (code == 0)
                        {
                            OfflineCheckInQueue.MarkProcessed(path, item, alreadyExisted
                                ? "A check-in already existed for this employee on that day - nothing to write."
                                : "The stored procedure reported this employee had already checked in on that day - nothing to write.");
                            result.AlreadyThere++;
                        }
                        else
                        {
                            Quarantine(db, path, item, "Stored procedure returned " + code + " (" + Describe(code) + ").");
                            result.Failed++;
                        }
                    }
                    catch (OfflineSyncSetupException ex)
                    {
                        // Setup problem, not a problem with this item: leave everything queued.
                        OfflineCheckInQueue.RecordAttempt(path, item, ex.Message);
                        result.Message = ex.Message;
                        break;
                    }
                    catch (Exception ex) when (OfflineCheckInQueue.IsConnectivityFailure(ex))
                    {
                        // Still down: leave this and everything behind it queued.
                        OfflineCheckInQueue.RecordAttempt(path, item, ex.Message);
                        result.StillOffline = true;
                        break;
                    }
                    catch (Exception ex)
                    {
                        // Anything else is a problem with this one item - park it so it can't block the rest.
                        Quarantine(db, path, item, "Unexpected error: " + ex.Message);
                        result.Failed++;
                    }
                }

                OfflineCheckInQueue.PurgeProcessed(30);
            }
            finally
            {
                Monitor.Exit(SyncLock);
            }

            result.PendingAfter = OfflineCheckInQueue.PendingCount();
            return result;
        }

        private static void Quarantine(dal db, string path, OfflineCheckIn item, string reason)
        {
            OfflineCheckInQueue.MarkFailed(path, item, reason);
            try
            {
                db.LogApplicationError(
                    "Offline check-in could not be replayed: " + reason + " [employee: " + item.Employee +
                    ", checked in " + item.CheckInTimeSast + ", queue id " + item.QueueId + "]",
                    null,
                    item.Employee);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("OfflineSyncService: could not log quarantined item: " + ex.Message);
            }
        }

        // Codes as actually returned by mst_spCheckInEmployee.
        private static string Describe(int code)
        {
            switch (code)
            {
                case -1: return "department does not exist";
                case -2: return "user does not exist";
                case -3: return "the check-in insert failed - see ErrorLog";
                default: return "unexpected code";
            }
        }

        // Cheap file check only - safe to call from Application_Start.
        public static void EnsureRunning()
        {
            try
            {
                if (!OfflineCheckInQueue.HasPending()) { return; }
                lock (TimerLock)
                {
                    if (_timer == null)
                    {
                        _timer = new System.Threading.Timer(OnTimer, null, TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(2));
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("OfflineSyncService.EnsureRunning failed: " + ex);
            }
        }

        private static void OnTimer(object state)
        {
            // An unhandled exception on a timer thread would take down the worker process.
            try
            {
                SyncPending();
                if (!OfflineCheckInQueue.HasPending()) { StopTimer(); }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("OfflineSyncService timer tick failed: " + ex);
            }
        }

        private static void StopTimer()
        {
            lock (TimerLock)
            {
                if (_timer != null)
                {
                    _timer.Dispose();
                    _timer = null;
                }
            }
        }

        // Called after a successful live check-in: the DB just worked, so drain the queue.
        public static void TriggerIfPending()
        {
            try
            {
                if (!OfflineCheckInQueue.HasPending()) { return; }
                HostingEnvironment.QueueBackgroundWorkItem(ct => { SyncPending(); });
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("OfflineSyncService.TriggerIfPending failed: " + ex);
            }
        }
    }
}
