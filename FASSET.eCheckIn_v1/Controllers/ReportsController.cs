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

            var rawLocations = _dbAccess.GetDistinctRawGeoLocations();
            ViewBag.Locations = rawLocations
                .Select(loc => ResolvePlaceName(RoundLocation(loc)))
                .Distinct()
                .OrderBy(x => x)
                .ToList();

            return View();
        }

        // GET: Reports/Data?start=...&end=...&department=...&lateAfter=09:00&location=...
        [HttpGet]
        public JsonResult Data(string start, string end, string department, string lateAfter, string location)
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

            if (!string.IsNullOrWhiteSpace(location))
            {
                rows = rows.Where(r => ResolvePlaceName(RoundLocation(r.GeoLocation)) == location).ToList();
            }

            // ---- Summary ----
            var summary = new
            {
                totalCheckIns = rows.Count,
                uniqueEmployees = rows.Select(r => r.EmployeeName).Distinct().Count(),
                dateRangeDays = (endDate.Date - startDate.Date).Days + 1
            };

            // ---- Attendance / time-based ----

            // Fetched early since byDayOfWeek and byWeek both need it for
            // their "Expected" columns, not just employeeAttendance further down.
            var scheduledDays = _dbAccess.GetScheduledDays(startDate, endDate.Date.AddDays(1), dept);

            string[] dayOrder = { "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday", "Sunday" };
            var expectedByDayOfWeek = scheduledDays
                .GroupBy(s => s.ScheduleDate.DayOfWeek.ToString())
                .ToDictionary(g => g.Key, g => g.Count());
            var dayCounts = rows.GroupBy(r => r.DateCreated.DayOfWeek.ToString())
                                 .ToDictionary(g => g.Key, g => g.Count());
            var byDayOfWeek = dayOrder.Select(d => new
            {
                label = d,
                count = dayCounts.ContainsKey(d) ? dayCounts[d] : 0,
                expected = expectedByDayOfWeek.ContainsKey(d) ? expectedByDayOfWeek[d] : 0
            }).ToList();

            var hourCounts = rows.GroupBy(r => r.DateCreated.Hour).ToDictionary(g => g.Key, g => g.Count());
            var byHour = Enumerable.Range(0, 24)
                                    .Select(h => new { label = h.ToString("00") + ":00", count = hourCounts.ContainsKey(h) ? hourCounts[h] : 0 })
                                    .ToList();

            // Finer 15-minute breakdown - only used by the "View all" drill-down
            // modal, not the inline hourly chart, which stays hourly for readability.
            var quarterHourCounts = rows
                .GroupBy(r => r.DateCreated.Hour * 60 + (r.DateCreated.Minute / 15) * 15)
                .ToDictionary(g => g.Key, g => g.Count());
            var byQuarterHour = Enumerable.Range(0, 96)
                .Select(i => i * 15)
                .Select(totalMinutes => new
                {
                    label = (totalMinutes / 60).ToString("00") + ":" + (totalMinutes % 60).ToString("00"),
                    count = quarterHourCounts.ContainsKey(totalMinutes) ? quarterHourCounts[totalMinutes] : 0
                })
                .ToList();

            // Every week that falls within the selected range, not just ones
            // with actual check-ins - otherwise a week with zero check-ins
            // but scheduled attendance would be silently missing, and its
            // "Expected" figure would never surface.
            var actualByWeek = rows.GroupBy(r => StartOfWeek(r.DateCreated)).ToDictionary(g => g.Key, g => g.Count());
            var expectedByWeek = scheduledDays.GroupBy(s => StartOfWeek(s.ScheduleDate)).ToDictionary(g => g.Key, g => g.Count());

            var allWeekStarts = new List<DateTime>();
            var weekCursor = StartOfWeek(startDate);
            var lastWeekStart = StartOfWeek(endDate);
            while (weekCursor <= lastWeekStart)
            {
                allWeekStarts.Add(weekCursor);
                weekCursor = weekCursor.AddDays(7);
            }

            var byWeek = allWeekStarts
                .Select(w => new
                {
                    label = "Week of " + w.ToString("yyyy-MM-dd"),
                    count = actualByWeek.ContainsKey(w) ? actualByWeek[w] : 0,
                    expected = expectedByWeek.ContainsKey(w) ? expectedByWeek[w] : 0
                })
                .ToList();

            var byMonth = rows.GroupBy(r => new DateTime(r.DateCreated.Year, r.DateCreated.Month, 1))
                               .OrderBy(g => g.Key)
                               .Select(g => new { label = g.Key.ToString("MMM yyyy"), count = g.Count() })
                               .ToList();

            string averageArrivalTime = "-";
            if (rows.Count > 0)
            {
                double avgTicks = rows.Average(r => r.DateCreated.TimeOfDay.Ticks);
                averageArrivalTime = new TimeSpan((long)avgTicks).ToString(@"hh\:mm");
            }

            int lateCount = rows.Count(r => r.DateCreated.TimeOfDay > lateThreshold);
            int onTimeCount = rows.Count - lateCount;
            var lateVsOnTime = new
            {
                threshold = lateThreshold.ToString(@"hh\:mm"),
                onTime = onTimeCount,
                late = lateCount
            };

            // Per-check-in detail, used by the Late/On-time drill-down modal
            // (View column) to show exactly who was late/on-time, and where.
            var checkInDetails = rows
                .Select(r => new
                {
                    employeeName = r.EmployeeName,
                    departmentName = r.DepartmentName ?? "Unknown",
                    location = ResolvePlaceName(RoundLocation(r.GeoLocation)),
                    time = r.DateCreated.ToString("yyyy-MM-dd HH:mm"),
                    isLate = r.DateCreated.TimeOfDay > lateThreshold
                })
                .ToList();

            // Employee Attendance: expected (per schedule) vs. actual
            // (per check-in), for the SAME date range as the rest of the
            // report - not all-time, since "expected" only makes sense
            // relative to a specific window. (scheduledDays was already
            // fetched earlier, above byDayOfWeek/byWeek - reused here.)
            var expectedByEmployee = scheduledDays
                .GroupBy(s => s.EmployeeName)
                .ToDictionary(g => g.Key, g => g.Select(s => s.ScheduleDate.Date).Distinct().Count());

            var actualByEmployee = rows
                .GroupBy(r => r.EmployeeName)
                .ToDictionary(g => g.Key, g => g.Select(r => r.DateCreated.Date).Distinct().Count());

            // Union of everyone who appears in EITHER dataset - covers both
            // "scheduled but never showed up" and "showed up unscheduled".
            var allEmployeeNames = new HashSet<string>(expectedByEmployee.Keys);
            allEmployeeNames.UnionWith(actualByEmployee.Keys);

            var employeeAttendance = allEmployeeNames
                .Select(name =>
                {
                    int expected = expectedByEmployee.ContainsKey(name) ? expectedByEmployee[name] : 0;
                    int actual = actualByEmployee.ContainsKey(name) ? actualByEmployee[name] : 0;
                    return new
                    {
                        employeeName = name,
                        expectedDays = expected,
                        actualDays = actual,
                        ratePercent = expected == 0 ? (double?)null : Math.Round(100.0 * actual / expected, 1)
                    };
                })
                .OrderBy(x => x.ratePercent ?? -1) // worst attendance first, unscheduled employees last
                .ToList();

            // By location - cluster on rounded GPS coordinates from the real
            // GeoLocation column ("lat,lng"), then resolve each cluster to a
            // real place name via Google Geocoding (falls back to raw
            // coordinates if no API key is configured yet). "0,0" means the
            // browser's geolocation prompt was denied/unavailable at check-in time.
            var sites = _dbAccess.GetSites();

            var byLocation = rows
                .GroupBy(r => RoundLocation(r.GeoLocation))
                .Select(g => new
                {
                    label = ResolvePlaceName(g.Key),
                    count = g.Count(),
                    outOfBounds = ClassifyLocation(g.Key, sites)
                })
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
                byQuarterHour,
                byWeek,
                byMonth,
                averageArrivalTime,
                lateVsOnTime,
                checkInDetails,
                employeeAttendance,
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

        // Great-circle distance between two lat/lng points, in meters.
        // Standard Haversine formula - accurate enough for office-scale
        // geofencing (error is well under a meter at this range).
        private static double HaversineDistanceMeters(double lat1, double lon1, double lat2, double lon2)
        {
            const double earthRadiusMeters = 6371000;
            double dLat = (lat2 - lat1) * Math.PI / 180.0;
            double dLon = (lon2 - lon1) * Math.PI / 180.0;
            double a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) +
                       Math.Cos(lat1 * Math.PI / 180.0) * Math.Cos(lat2 * Math.PI / 180.0) *
                       Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
            double c = 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
            return earthRadiusMeters * c;
        }

        // Classifies a rounded "lat, lng" location key against the
        // configured sites, returning which site it's within (if any) or
        // how far outside the nearest one it was.
        private static string ClassifyLocation(string roundedCoordinateKey, List<SiteInfo> sites)
        {
            if (roundedCoordinateKey == "Unknown / location denied")
            {
                return "Unknown (location denied)";
            }

            if (sites == null || sites.Count == 0)
            {
                return "Not yet checked - sites not configured";
            }

            var parts = roundedCoordinateKey.Split(',');
            double lat, lng;
            if (parts.Length != 2 ||
                !double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out lat) ||
                !double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out lng))
            {
                return "Unknown (location denied)";
            }

            SiteInfo nearestSite = null;
            double nearestDistance = double.MaxValue;
            foreach (var site in sites)
            {
                double distance = HaversineDistanceMeters(lat, lng, site.Latitude, site.Longitude);
                if (distance < nearestDistance)
                {
                    nearestDistance = distance;
                    nearestSite = site;
                }
            }

            if (nearestSite != null && nearestDistance <= nearestSite.RadiusMeters)
            {
                return "In bounds (" + nearestSite.SiteName + ")";
            }

            return "Out of bounds (nearest: " + nearestSite.SiteName + ", " + Math.Round(nearestDistance) + "m away)";
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