using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Web.Hosting;
using System.Web.Script.Serialization;
using FASSET.eCheckIn_v1.Data_Access_Layer;
using FASSET.eCheckIn_v1.Models;

namespace FASSET.eCheckIn_v1.Services
{
    // Snapshot of the two lists the check-in form needs (departments and
    // active employees), refreshed in the background whenever the database
    // is healthy and kept both in memory and on disk. When the database is
    // unreachable the form still loads and the employee autocomplete still
    // works from this snapshot - otherwise an outage would break the page
    // before anyone could submit a check-in at all.
    public static class ReferenceDataCache
    {
        public class CachedEmployee
        {
            public string Name { get; set; }
            public string Department { get; set; }
        }

        public class Snapshot
        {
            public string SavedAtSast { get; set; }
            public List<string> Departments { get; set; }
            public List<CachedEmployee> Employees { get; set; }
        }

        private static readonly object CacheLock = new object();
        private static Snapshot _memory;
        private static DateTime _lastRefreshUtc = DateTime.MinValue;
        private static int _refreshing;

        private static string SnapshotPath
        {
            get { return Path.Combine(OfflineCheckInQueue.RootFolder, "reference-data.json"); }
        }

        public static void RefreshInBackgroundIfStale()
        {
            RefreshInBackgroundIfStale(30);
        }

        public static void RefreshInBackgroundIfStale(int maxAgeMinutes)
        {
            if (OfflineCheckInQueue.IsCircuitOpen) { return; }
            if ((DateTime.UtcNow - _lastRefreshUtc).TotalMinutes < maxAgeMinutes) { return; }
            if (Interlocked.CompareExchange(ref _refreshing, 1, 0) != 0) { return; }

            try
            {
                HostingEnvironment.QueueBackgroundWorkItem(ct =>
                {
                    try { RefreshNow(new dal()); }
                    catch (Exception ex) { System.Diagnostics.Debug.WriteLine("ReferenceDataCache refresh failed: " + ex.Message); }
                    finally { Interlocked.Exchange(ref _refreshing, 0); }
                });
            }
            catch (Exception)
            {
                Interlocked.Exchange(ref _refreshing, 0);
            }
        }

        public static void RefreshNow(dal db)
        {
            var departments = db.GetDepartments().Select(d => d.DepartmentName).ToList();
            var employees = db.GetActiveEmployeesWithDepartment()
                .Select(kv => new CachedEmployee { Name = kv.Key, Department = kv.Value })
                .ToList();

            var snapshot = new Snapshot
            {
                SavedAtSast = OfflineCheckIn.Format(OfflineCheckInQueue.SastNow()),
                Departments = departments,
                Employees = employees
            };

            lock (CacheLock)
            {
                _memory = snapshot;
                _lastRefreshUtc = DateTime.UtcNow;
                try
                {
                    Directory.CreateDirectory(OfflineCheckInQueue.RootFolder);
                    string temp = SnapshotPath + ".tmp";
                    File.WriteAllText(temp, new JavaScriptSerializer().Serialize(snapshot));
                    if (File.Exists(SnapshotPath)) { File.Delete(SnapshotPath); }
                    File.Move(temp, SnapshotPath);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine("ReferenceDataCache could not write snapshot file: " + ex.Message);
                }
            }
        }

        private static Snapshot GetSnapshot()
        {
            lock (CacheLock)
            {
                if (_memory != null) { return _memory; }
                try
                {
                    if (File.Exists(SnapshotPath))
                    {
                        _memory = new JavaScriptSerializer().Deserialize<Snapshot>(File.ReadAllText(SnapshotPath));
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine("ReferenceDataCache could not read snapshot file: " + ex.Message);
                }
                return _memory;
            }
        }

        public static bool TryGetDepartments(out List<Department> departments)
        {
            Snapshot snap = GetSnapshot();
            if (snap == null || snap.Departments == null || snap.Departments.Count == 0)
            {
                departments = null;
                return false;
            }
            departments = snap.Departments
                .OrderBy(d => d, StringComparer.OrdinalIgnoreCase)
                .Select(d => new Department { DepartmentName = d })
                .ToList();
            return true;
        }

        // Returns null when there is no snapshot yet (caller should rethrow the original DB error).
        public static List<Department> SearchDepartments(string term)
        {
            Snapshot snap = GetSnapshot();
            if (snap == null || snap.Departments == null) { return null; }

            string t = term ?? "";
            return snap.Departments
                .Where(d => d != null && d.IndexOf(t, StringComparison.OrdinalIgnoreCase) >= 0)
                .OrderBy(d => d, StringComparer.OrdinalIgnoreCase)
                .Select(d => new Department { DepartmentName = d })
                .ToList();
        }

        // Same rules as dal.GetEmployeesByTerm: name contains the term, optional
        // department filter, alphabetical, top 5.
        public static List<Employee> SearchEmployees(string term, string department)
        {
            Snapshot snap = GetSnapshot();
            if (snap == null || snap.Employees == null) { return null; }

            string t = term ?? "";
            IEnumerable<CachedEmployee> query = snap.Employees
                .Where(e => e.Name != null && e.Name.IndexOf(t, StringComparison.OrdinalIgnoreCase) >= 0);

            if (!string.IsNullOrEmpty(department))
            {
                query = query.Where(e => string.Equals(e.Department, department, StringComparison.OrdinalIgnoreCase));
            }

            return query
                .OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
                .Take(5)
                .Select(e => new Employee { EmployeeName = e.Name })
                .ToList();
        }
    }
}
