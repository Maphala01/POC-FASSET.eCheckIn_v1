using System;
using System.Collections.Generic;
using System.Configuration;
using System.Data.SqlClient;
using System.IO;
using System.Threading;
using System.Web.Hosting;
using System.Web.Script.Serialization;

namespace FASSET.eCheckIn_v1.Services
{
    public class OfflineQueueStatus
    {
        public int Pending { get; set; }
        public int Failed { get; set; }
        public string OldestPendingCheckInSast { get; set; }
        public string LastError { get; set; }
        public string Folder { get; set; }
        public bool DatabaseCircuitOpen { get; set; }
    }

    // File-based queue for check-ins that couldn't reach the database.
    //   <root>\pending\    waiting to be written to the database
    //   <root>\processed\  written (kept 30 days as an audit trail, then purged)
    //   <root>\failed\     rejected on replay - needs a human (also logged to ErrorLog)
    // One file per check-in (written to .tmp then renamed) so concurrent
    // requests never append to the same file and a crash can't leave a
    // half-written record in pending.
    public static class OfflineCheckInQueue
    {
        private static readonly object FileLock = new object();
        private static long _circuitOpenUntilTicks;

        // SqlClient / SQL Server error numbers meaning "couldn't reach or use
        // the database right now" - as opposed to business-rule or SQL logic
        // errors, which must never be queued.
        private static readonly HashSet<int> ConnectivityErrorNumbers = new HashSet<int>
        {
            -2, -1,                     // client timeout / generic connection failure
            2, 20, 53, 64, 121, 233, 258, // instance not found, network path, semaphore/wait timed out, no process at other end
            1205,                       // deadlock victim (transient)
            4060,                       // cannot open the requested database (offline / recovering)
            10053, 10054, 10060, 10061, // connection aborted / reset / timed out / refused
            10928, 10929, 40197, 40501, 40613, 49918, // throttling / transient
            18456                       // login failed - queued so nothing is lost; shows on the Settings card
        };

        // ---------------- folders ----------------

        public static string RootFolder
        {
            get
            {
                string configured = ConfigurationManager.AppSettings["OfflineCheckInFolder"];
                if (!string.IsNullOrWhiteSpace(configured)) { return configured.Trim(); }

                string mapped = HostingEnvironment.MapPath("~/App_Data/OfflineCheckIns");
                return mapped ?? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "App_Data", "OfflineCheckIns");
            }
        }

        private static string PendingFolder { get { return Path.Combine(RootFolder, "pending"); } }
        private static string ProcessedFolder { get { return Path.Combine(RootFolder, "processed"); } }
        private static string FailedFolder { get { return Path.Combine(RootFolder, "failed"); } }

        // ---------------- time ----------------

        public static DateTime SastNow()
        {
            try
            {
                var zone = TimeZoneInfo.FindSystemTimeZoneById("South Africa Standard Time");
                return TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, zone);
            }
            catch (TimeZoneNotFoundException) { return DateTime.UtcNow.AddHours(2); }
            catch (InvalidTimeZoneException) { return DateTime.UtcNow.AddHours(2); }
        }

        // ---------------- connectivity classification + circuit breaker ----------------

        public static bool IsConnectivityFailure(Exception ex)
        {
            var sqlEx = ex as SqlException;
            if (sqlEx != null)
            {
                foreach (SqlError error in sqlEx.Errors)
                {
                    if (ConnectivityErrorNumbers.Contains(error.Number)) { return true; }
                }
                return ConnectivityErrorNumbers.Contains(sqlEx.Number);
            }

            // Connection-pool exhaustion surfaces as InvalidOperationException, not SqlException.
            var invalid = ex as InvalidOperationException;
            if (invalid != null && invalid.Message != null &&
                invalid.Message.IndexOf("connection from the pool", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return true;
            }
            return false;
        }

        // After a connectivity failure the database is treated as "down" for a
        // short window, so the next people to check in are queued instantly
        // instead of each waiting out a connect timeout.
        public static bool IsCircuitOpen
        {
            get { return DateTime.UtcNow.Ticks < Interlocked.Read(ref _circuitOpenUntilTicks); }
        }

        public static void OpenCircuit() { OpenCircuit(45); }

        public static void OpenCircuit(int seconds)
        {
            Interlocked.Exchange(ref _circuitOpenUntilTicks, DateTime.UtcNow.AddSeconds(seconds).Ticks);
        }

        public static void CloseCircuit()
        {
            Interlocked.Exchange(ref _circuitOpenUntilTicks, 0L);
        }

        // Caps how long a connection attempt may hang, so "database
        // unreachable" is detected in seconds rather than the 30s in Web.config.
        public static string CapConnectTimeout(string connectionString, int maxSeconds)
        {
            try
            {
                var builder = new SqlConnectionStringBuilder(connectionString);
                if (builder.ConnectTimeout > maxSeconds) { builder.ConnectTimeout = maxSeconds; }
                return builder.ConnectionString;
            }
            catch (Exception)
            {
                return connectionString;
            }
        }

        // ---------------- queue operations ----------------

        // Returns false if the record could NOT be saved to disk - the caller
        // must then treat the check-in as failed rather than telling the
        // person it was recorded.
        public static bool TryEnqueue(OfflineCheckIn item, string error)
        {
            try
            {
                lock (FileLock)
                {
                    Directory.CreateDirectory(PendingFolder);

                    item.QueueId = Guid.NewGuid().ToString("N");
                    item.QueuedAtSast = OfflineCheckIn.Format(SastNow());
                    item.Attempts = 0;
                    item.LastError = error;

                    string fileName = item.GetCheckInTime().ToString("yyyyMMddHHmmssfff") + "_" + item.QueueId.Substring(0, 8) + ".json";
                    string finalPath = Path.Combine(PendingFolder, fileName);
                    string tempPath = finalPath + ".tmp";

                    File.WriteAllText(tempPath, Serialize(item));
                    File.Move(tempPath, finalPath);
                }
                return true;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("OfflineCheckInQueue.TryEnqueue failed: " + ex);
                return false;
            }
        }

        // Oldest first (file names start with the check-in timestamp).
        public static string[] GetPendingFiles()
        {
            if (!Directory.Exists(PendingFolder)) { return new string[0]; }
            string[] files = Directory.GetFiles(PendingFolder, "*.json");
            Array.Sort(files, StringComparer.Ordinal);
            return files;
        }

        public static bool HasPending() { return GetPendingFiles().Length > 0; }
        public static int PendingCount() { return GetPendingFiles().Length; }

        public static int FailedCount()
        {
            return Directory.Exists(FailedFolder) ? Directory.GetFiles(FailedFolder, "*.json").Length : 0;
        }

        public static bool TryRead(string path, out OfflineCheckIn item)
        {
            try
            {
                item = new JavaScriptSerializer().Deserialize<OfflineCheckIn>(File.ReadAllText(path));
                return item != null && !string.IsNullOrEmpty(item.CheckInTimeSast) && !string.IsNullOrEmpty(item.Employee);
            }
            catch (Exception)
            {
                item = null;
                return false;
            }
        }

        // Database still unreachable: keep the file, note the attempt.
        public static void RecordAttempt(string path, OfflineCheckIn item, string error)
        {
            try
            {
                item.Attempts++;
                item.LastError = error;
                item.LastAttemptSast = OfflineCheckIn.Format(SastNow());
                lock (FileLock)
                {
                    string temp = path + ".tmp";
                    File.WriteAllText(temp, Serialize(item));
                    if (File.Exists(path)) { File.Delete(path); }
                    File.Move(temp, path);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("OfflineCheckInQueue.RecordAttempt failed: " + ex);
            }
        }

        public static void MarkProcessed(string path, OfflineCheckIn item, string note)
        {
            MoveResolved(ProcessedFolder, path, item, note);
        }

        public static void MarkFailed(string path, OfflineCheckIn item, string note)
        {
            MoveResolved(FailedFolder, path, item, note);
        }

        // For a file too damaged to even deserialize.
        public static void MarkUnreadableFailed(string path)
        {
            try
            {
                lock (FileLock)
                {
                    Directory.CreateDirectory(FailedFolder);
                    string dest = Path.Combine(FailedFolder, Path.GetFileName(path) + ".unreadable");
                    if (File.Exists(dest)) { File.Delete(dest); }
                    File.Move(path, dest);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("OfflineCheckInQueue.MarkUnreadableFailed failed: " + ex);
            }
        }

        private static void MoveResolved(string folder, string path, OfflineCheckIn item, string note)
        {
            lock (FileLock)
            {
                Directory.CreateDirectory(folder);
                item.ResolvedAtSast = OfflineCheckIn.Format(SastNow());
                item.ResolutionNote = note;

                string dest = Path.Combine(folder, Path.GetFileName(path));
                File.WriteAllText(dest, Serialize(item));
                if (File.Exists(path)) { File.Delete(path); }
            }
        }

        // Processed items are only an audit trail (and hold employee location
        // data), so they are not kept forever.
        public static void PurgeProcessed(int olderThanDays)
        {
            try
            {
                if (!Directory.Exists(ProcessedFolder)) { return; }
                DateTime cutoff = DateTime.Now.AddDays(-olderThanDays);
                foreach (string file in Directory.GetFiles(ProcessedFolder, "*.json"))
                {
                    if (File.GetLastWriteTime(file) < cutoff) { File.Delete(file); }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("OfflineCheckInQueue.PurgeProcessed failed: " + ex);
            }
        }

        public static OfflineQueueStatus GetStatus()
        {
            var status = new OfflineQueueStatus
            {
                Pending = 0,
                Failed = FailedCount(),
                Folder = RootFolder,
                DatabaseCircuitOpen = IsCircuitOpen
            };

            string[] pending = GetPendingFiles();
            status.Pending = pending.Length;

            if (pending.Length > 0)
            {
                OfflineCheckIn oldest;
                if (TryRead(pending[0], out oldest))
                {
                    status.OldestPendingCheckInSast = oldest.CheckInTimeSast;
                    status.LastError = oldest.LastError;
                }
            }
            return status;
        }

        private static string Serialize(OfflineCheckIn item)
        {
            return new JavaScriptSerializer().Serialize(item);
        }
    }
}
