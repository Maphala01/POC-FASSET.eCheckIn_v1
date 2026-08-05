using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Configuration;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Web.Mvc;
using System.Web.Script.Serialization;
using FASSET.eCheckIn_v1.Models;

namespace FASSET.eCheckIn_v1.Controllers
{
    public class ReportsController : Controller
    {
        Data_Access_Layer.dal _dbAccess = new Data_Access_Layer.dal();

        // GET: Reports
        public ActionResult Index()
        {
            ViewBag.Departments = _dbAccess.GetDepartmentNamesForReporting();
            return View();
        }

        // GET: Reports/Data?start=...&end=...&department=...&lateAfter=09:00
        [HttpGet]
        public JsonResult Data(string start, string end, string department, string lateAfter)
        {
            DateTime startDate = string.IsNullOrEmpty(start) ? DateTime.Today.AddDays(-30) : DateTime.Parse(start);
            DateTime endDate = string.IsNullOrEmpty(end) ? DateTime.Today : DateTime.Parse(end);
            string dept = string.IsNullOrWhiteSpace(department) ? null : department;

            TimeSpan lateThreshold;
            if (string.IsNullOrWhiteSpace(lateAfter) || !TimeSpan.TryParse(lateAfter, out lateThreshold))
            {
                lateThreshold = new TimeSpan(9, 0, 0); // default 09:00
            }

            List<CheckInReportRow> rows = _dbAccess.GetCheckInReportData(startDate, endDate, dept);

            // ---- Summary ----
            var summary = new
            {
                totalCheckIns = rows.Count,
                dateRangeDays = (endDate.Date - startDate.Date).Days + 1
            };

            // ---- Attendance / time-based ----

            string[] dayOrder = { "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday", "Sunday" };
            var dayCounts = rows.GroupBy(r => r.DateCreated.DayOfWeek.ToString())
                                 .ToDictionary(g => g.Key, g => g.Count());
            var byDayOfWeek = dayOrder.Select(d => new { label = d, count = dayCounts.ContainsKey(d) ? dayCounts[d] : 0 }).ToList();

            var hourCounts = rows.GroupBy(r => r.DateCreated.Hour).ToDictionary(g => g.Key, g => g.Count());
            var byHour = Enumerable.Range(0, 24)
                                    .Select(h => new { label = h.ToString("00") + ":00", count = hourCounts.ContainsKey(h) ? hourCounts[h] : 0 })
                                    .ToList();

            var byWeek = rows.GroupBy(r => StartOfWeek(r.DateCreated))
                              .OrderBy(g => g.Key)
                              .Select(g => new { label = "Week of " + g.Key.ToString("yyyy-MM-dd"), count = g.Count() })
                              .ToList();

            int lateCount = rows.Count(r => r.DateCreated.TimeOfDay > lateThreshold);
            int onTimeCount = rows.Count - lateCount;
            var lateVsOnTime = new
            {
                threshold = lateThreshold.ToString(@"hh\:mm"),
                onTime = onTimeCount,
                late = lateCount
            };

            var lastCheckIns = _dbAccess.GetLastCheckInPerEmployee(dept);
            var today = DateTime.Today;
            var lastCheckInGaps = lastCheckIns
                .Select(kv => new
                {
                    employeeName = kv.Key,
                    lastCheckIn = kv.Value == DateTime.MinValue ? (string)null : kv.Value.ToString("yyyy-MM-dd"),
                    daysSince = kv.Value == DateTime.MinValue ? (int?)null : (today - kv.Value.Date).Days
                })
                .OrderByDescending(x => x.daysSince ?? int.MaxValue)
                .Take(15)
                .ToList();

            // By location - cluster on rounded GPS coordinates from the real
            // GeoLocation column ("lat,lng"), then resolve each cluster to a
            // real place name via Google Geocoding (falls back to raw
            // coordinates if no API key is configured yet). "0,0" means the
            // browser's geolocation prompt was denied/unavailable at check-in time.
            var byLocation = rows
                .GroupBy(r => RoundLocation(r.GeoLocation))
                .Select(g => new { label = ResolvePlaceName(g.Key), count = g.Count() })
                .OrderByDescending(g => g.count)
                .ToList();

            // ---- Organizational breakdown ----
            var byDepartment = rows
                .GroupBy(r => r.DepartmentName ?? "Unknown")
                .OrderByDescending(g => g.Count())
                .Select(g => new { label = g.Key, count = g.Count() })
                .ToList();

            var headcounts = _dbAccess.GetEmployeeHeadcountByDepartment();
            var checkedInPerDept = rows
                .GroupBy(r => r.DepartmentName ?? "Unknown")
                .ToDictionary(g => g.Key, g => g.Select(r => r.EmployeeName).Distinct().Count());

            var attendanceRateByDepartment = headcounts
                .Where(kv => dept == null || kv.Key == dept)
                .Select(kv => new
                {
                    label = kv.Key,
                    headcount = kv.Value,
                    checkedIn = checkedInPerDept.ContainsKey(kv.Key) ? checkedInPerDept[kv.Key] : 0,
                    ratePercent = kv.Value == 0 ? 0 : Math.Round(100.0 * (checkedInPerDept.ContainsKey(kv.Key) ? checkedInPerDept[kv.Key] : 0) / kv.Value, 1)
                })
                .OrderByDescending(x => x.ratePercent)
                .ToList();

            // ---- Workforce demographics ----
            var byOccupationalLevel = rows
                .GroupBy(r => string.IsNullOrWhiteSpace(r.OccupationalLevel) ? "Not specified" : r.OccupationalLevel)
                .OrderByDescending(g => g.Count())
                .Select(g => new { label = g.Key, count = g.Count() })
                .ToList();

            return Json(new
            {
                summary,
                byDayOfWeek,
                byHour,
                byWeek,
                lateVsOnTime,
                lastCheckInGaps,
                byLocation,
                byDepartment,
                attendanceRateByDepartment,
                byOccupationalLevel
            }, JsonRequestBehavior.AllowGet);
        }

        private static DateTime StartOfWeek(DateTime date)
        {
            int diff = (7 + (date.DayOfWeek - DayOfWeek.Monday)) % 7;
            return date.Date.AddDays(-diff);
        }

        private static string RoundLocation(string rawGeoLocation)
        {
            if (string.IsNullOrWhiteSpace(rawGeoLocation) || rawGeoLocation == "0,0")
            {
                return "Unknown / location denied";
            }

            var parts = rawGeoLocation.Split(',');
            if (parts.Length != 2) return "Unknown / location denied";

            double lat, lng;
            if (double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out lat) &&
                double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out lng))
            {
                return Math.Round(lat, 3).ToString(CultureInfo.InvariantCulture) + ", " + Math.Round(lng, 3).ToString(CultureInfo.InvariantCulture);
            }

            return "Unknown / location denied";
        }

        // In-memory cache: one geocoding lookup per unique rounded
        // coordinate cluster for the lifetime of the app pool, not per
        // request/filter-change. Keeps this cheap even with frequent report
        // refreshes.
        private static readonly ConcurrentDictionary<string, string> _placeNameCache =
            new ConcurrentDictionary<string, string>();

        private static string ResolvePlaceName(string roundedCoordinateKey)
        {
            if (roundedCoordinateKey == "Unknown / location denied")
            {
                return roundedCoordinateKey;
            }

            string cached;
            if (_placeNameCache.TryGetValue(roundedCoordinateKey, out cached))
            {
                return cached;
            }

            string apiKey = ConfigurationManager.AppSettings["GoogleMapsApiKey"];
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                // No key configured yet - show coordinates rather than failing.
                // Once GoogleMapsApiKey is added to Web.config's <appSettings>,
                // this same code path starts returning real place names
                // automatically, no other changes needed.
                _placeNameCache[roundedCoordinateKey] = roundedCoordinateKey;
                return roundedCoordinateKey;
            }

            try
            {
                var parts = roundedCoordinateKey.Split(',');
                string lat = parts[0].Trim();
                string lng = parts[1].Trim();

                string url = string.Format(
                    "https://maps.googleapis.com/maps/api/geocode/json?latlng={0},{1}&key={2}",
                    lat, lng, apiKey);

                using (var client = new WebClient())
                {
                    string json = client.DownloadString(url);
                    var serializer = new JavaScriptSerializer();
                    var parsed = serializer.Deserialize<GeocodeResponse>(json);

                    if (parsed != null && parsed.status == "OK" && parsed.results != null && parsed.results.Length > 0)
                    {
                        string placeName = parsed.results[0].formatted_address;
                        _placeNameCache[roundedCoordinateKey] = placeName;
                        return placeName;
                    }
                }
            }
            catch
            {
                // Network hiccup, quota exceeded, malformed coordinates, etc.
                // Fall back to raw coordinates rather than breaking the report.
            }

            _placeNameCache[roundedCoordinateKey] = roundedCoordinateKey;
            return roundedCoordinateKey;
        }

        // Minimal shape of Google's Geocoding API response — only the
        // fields this report actually uses.
        private class GeocodeResponse
        {
            public GeocodeResult[] results { get; set; }
            public string status { get; set; }
        }

        private class GeocodeResult
        {
            public string formatted_address { get; set; }
        }
    }
}
