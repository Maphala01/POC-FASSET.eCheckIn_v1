using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Configuration;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Web.Mvc;
using System.Web.Script.Serialization;
using FASSET.eCheckIn_v1.Models;
using OfficeOpenXml;
using OfficeOpenXml.Drawing.Chart;
using OfficeOpenXml.Style;
using OfficeOpenXml.Table;

namespace FASSET.eCheckIn_v1.Controllers
{
    public class ReportsController : Controller
    {
        Data_Access_Layer.dal _dbAccess = new Data_Access_Layer.dal();

        // Brand colors, reused across the whole workbook so every sheet
        // looks like it belongs to the same report.
        private static readonly Color BrandNavy = Color.FromArgb(0x1b, 0x3f, 0x6e);
        private static readonly Color BrandNavyDark = Color.FromArgb(0x12, 0x29, 0x4a);
        private static readonly Color BrandGold = Color.FromArgb(0xf2, 0xc4, 0x0c);
        private static readonly Color BrandGreen = Color.FromArgb(0x2e, 0x9e, 0x4a);
        private static readonly Color BrandRed = Color.FromArgb(0xa1, 0x30, 0x22);
        private static readonly Color BrandTeal = Color.FromArgb(0x0c, 0x64, 0x83);
        private static readonly Color LightRowFill = Color.FromArgb(0xf7, 0xf9, 0xfb);

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

        // GET: Reports/ExportExcel?start=...&end=...&department=...&lateAfter=09:00&location=...
        // Builds the same report as Data() above, but as a real, native
        // .xlsx workbook via EPPlus (already used elsewhere in this
        // solution for the schedule calendar import) - which, unlike the
        // client-side SheetJS export this replaces, can embed the logo,
        // real colors, and native Excel charts.
        //
        // NOTE: this intentionally duplicates Data()'s query/grouping logic
        // rather than sharing it, so a future change to the live dashboard's
        // JSON shape can't silently break this export (or vice versa). If
        // you change how a number is calculated in Data(), make the same
        // change here.
        [HttpGet]
        public FileResult ExportExcel(string start, string end, string department, string lateAfter, string location)
        {
            DateTime startDate = string.IsNullOrEmpty(start) ? DateTime.Today.AddDays(-30) : DateTime.Parse(start);
            DateTime endDate = string.IsNullOrEmpty(end) ? DateTime.Today : DateTime.Parse(end);
            string dept = string.IsNullOrWhiteSpace(department) ? null : department;
            string locationFilter = string.IsNullOrWhiteSpace(location) ? null : location;

            TimeSpan lateThreshold;
            if (string.IsNullOrWhiteSpace(lateAfter) || !TimeSpan.TryParse(lateAfter, out lateThreshold))
            {
                lateThreshold = new TimeSpan(9, 0, 0);
            }

            List<CheckInReportRow> rows = _dbAccess.GetCheckInReportData(startDate, endDate, dept);
            if (locationFilter != null)
            {
                rows = rows.Where(r => ResolvePlaceName(RoundLocation(r.GeoLocation)) == locationFilter).ToList();
            }

            var scheduledDays = _dbAccess.GetScheduledDays(startDate, endDate.Date.AddDays(1), dept);
            var sites = _dbAccess.GetSites();
            var headcounts = _dbAccess.GetEmployeeHeadcountByDepartment();

            // Overview's new "Expected/Planned Check-ins" KPI card and the
            // Actual-vs-Planned chart both just need the total scheduled
            // seat-day count for the whole selected range - not broken out
            // by month like By Month's PlannedSeatDays.
            int expectedCheckIns = scheduledDays.Count;

            int totalCheckIns = rows.Count;
            int uniqueEmployees = rows.Select(r => r.EmployeeName).Distinct().Count();
            int dateRangeDays = (endDate.Date - startDate.Date).Days + 1;
            int lateCount = rows.Count(r => r.DateCreated.TimeOfDay > lateThreshold);
            int onTimeCount = totalCheckIns - lateCount;

            string averageArrivalTime = "-";
            if (rows.Count > 0)
            {
                double avgTicks = rows.Average(r => r.DateCreated.TimeOfDay.Ticks);
                averageArrivalTime = new TimeSpan((long)avgTicks).ToString(@"hh\:mm");
            }

            string[] dayOrder = { "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday", "Sunday" };
            var dayCounts = rows.GroupBy(r => r.DateCreated.DayOfWeek.ToString()).ToDictionary(g => g.Key, g => g.Count());
            var byDayOfWeek = dayOrder.Select(d => new { Label = d, Count = dayCounts.ContainsKey(d) ? dayCounts[d] : 0 }).ToList();

            var hourCounts = rows.GroupBy(r => r.DateCreated.Hour).ToDictionary(g => g.Key, g => g.Count());
            var byHour = Enumerable.Range(0, 24).Select(h => new { Label = h.ToString("00") + ":00", Count = hourCounts.ContainsKey(h) ? hourCounts[h] : 0 }).ToList();

            var allWeekStarts = new List<DateTime>();
            var weekCursor = StartOfWeek(startDate);
            var lastWeekStart = StartOfWeek(endDate);
            while (weekCursor <= lastWeekStart)
            {
                allWeekStarts.Add(weekCursor);
                weekCursor = weekCursor.AddDays(7);
            }

            var actualByWeek = rows.GroupBy(r => StartOfWeek(r.DateCreated)).ToDictionary(g => g.Key, g => g.Count());
            var byWeek = allWeekStarts.Select(w => new { Label = "Week of " + w.ToString("yyyy-MM-dd"), Count = actualByWeek.ContainsKey(w) ? actualByWeek[w] : 0 }).ToList();

            // Full Week x Hour grid, including zero rows, so the "week
            // filter" (an Excel AutoFilter on the Week column, once this is
            // written as a Table below) always shows all 24 hours for
            // whichever week the user filters to.
            var hourByWeekCounts = rows
                .GroupBy(r => new { Week = StartOfWeek(r.DateCreated), Hour = r.DateCreated.Hour })
                .ToDictionary(g => g.Key, g => g.Count());
            var hourByWeek = new List<Tuple<string, string, int>>();
            foreach (var w in allWeekStarts)
            {
                for (int h = 0; h < 24; h++)
                {
                    var key = new { Week = w, Hour = h };
                    int count = hourByWeekCounts.ContainsKey(key) ? hourByWeekCounts[key] : 0;
                    hourByWeek.Add(Tuple.Create("Week of " + w.ToString("yyyy-MM-dd"), h.ToString("00") + ":00", count));
                }
            }

            // By Month: union of months that have EITHER actual check-ins OR
            // scheduled seat-days, so a fully-missed month still shows up
            // with Planned Seat-Days > 0 and Actual = 0, rather than being
            // silently dropped.
            var monthKeys = new HashSet<DateTime>(rows.Select(r => new DateTime(r.DateCreated.Year, r.DateCreated.Month, 1)));
            monthKeys.UnionWith(scheduledDays.Select(s => new DateTime(s.ScheduleDate.Year, s.ScheduleDate.Month, 1)));

            var actualByMonth = rows.GroupBy(r => new DateTime(r.DateCreated.Year, r.DateCreated.Month, 1)).ToDictionary(g => g.Key, g => g.Count());
            var plannedByMonth = scheduledDays.GroupBy(s => new DateTime(s.ScheduleDate.Year, s.ScheduleDate.Month, 1)).ToDictionary(g => g.Key, g => g.Count());
            var locationsByMonth = rows
                .GroupBy(r => new DateTime(r.DateCreated.Year, r.DateCreated.Month, 1))
                .ToDictionary(g => g.Key, g => string.Join(", ", g.Select(r => ResolvePlaceName(RoundLocation(r.GeoLocation))).Distinct().OrderBy(x => x)));

            var byMonth = monthKeys.OrderBy(m => m).Select(m => new
            {
                Label = m.ToString("MMM yyyy"),
                ActualCheckIns = actualByMonth.ContainsKey(m) ? actualByMonth[m] : 0,
                PlannedSeatDays = plannedByMonth.ContainsKey(m) ? plannedByMonth[m] : 0,
                Locations = locationsByMonth.ContainsKey(m) ? locationsByMonth[m] : "-"
            }).ToList();

            // Employee Attendance, now with a computed "Primary Location" -
            // the location they check in from most often - so the sheet
            // has something meaningful for a Location filter to act on.
            var expectedByEmployee = scheduledDays.GroupBy(s => s.EmployeeName).ToDictionary(g => g.Key, g => g.Select(s => s.ScheduleDate.Date).Distinct().Count());
            var actualByEmployee = rows.GroupBy(r => r.EmployeeName).ToDictionary(g => g.Key, g => g.Select(r => r.DateCreated.Date).Distinct().Count());
            var employeeLocations = rows
                .GroupBy(r => r.EmployeeName)
                .ToDictionary(g => g.Key, g => g.GroupBy(r => ResolvePlaceName(RoundLocation(r.GeoLocation)))
                                                 .OrderByDescending(x => x.Count())
                                                 .First().Key);

            var allEmployeeNames = new HashSet<string>(expectedByEmployee.Keys);
            allEmployeeNames.UnionWith(actualByEmployee.Keys);

            var employeeAttendance = allEmployeeNames.Select(name =>
            {
                int expected = expectedByEmployee.ContainsKey(name) ? expectedByEmployee[name] : 0;
                int actual = actualByEmployee.ContainsKey(name) ? actualByEmployee[name] : 0;
                return new
                {
                    EmployeeName = name,
                    ExpectedDays = expected,
                    ActualDays = actual,
                    RatePercent = expected == 0 ? (double?)null : Math.Round(100.0 * actual / expected, 1),
                    PrimaryLocation = employeeLocations.ContainsKey(name) ? employeeLocations[name] : "No check-ins in range"
                };
            })
            .OrderBy(x => x.RatePercent ?? -1)
            .ToList();

            var byLocation = rows
                .GroupBy(r => RoundLocation(r.GeoLocation))
                .Select(g => new { Label = ResolvePlaceName(g.Key), Count = g.Count(), Status = ClassifyLocation(g.Key, sites) })
                .OrderByDescending(g => g.Count)
                .ToList();

            var byDepartment = rows
                .GroupBy(r => r.DepartmentName ?? "Unknown")
                .OrderByDescending(g => g.Count())
                .Select(g => new { Label = g.Key, Count = g.Count() })
                .ToList();

            var checkedInPerDept = rows.GroupBy(r => r.DepartmentName ?? "Unknown").ToDictionary(g => g.Key, g => g.Select(r => r.EmployeeName).Distinct().Count());
            var attendanceRateByDepartment = headcounts
                .Where(kv => dept == null || kv.Key == dept)
                .Select(kv => new
                {
                    Label = kv.Key,
                    Headcount = kv.Value,
                    CheckedIn = checkedInPerDept.ContainsKey(kv.Key) ? checkedInPerDept[kv.Key] : 0,
                    RatePercent = kv.Value == 0 ? 0 : Math.Round(100.0 * (checkedInPerDept.ContainsKey(kv.Key) ? checkedInPerDept[kv.Key] : 0) / kv.Value, 1)
                })
                .OrderByDescending(x => x.RatePercent)
                .ToList();

            var byOccupationalLevel = rows
                .GroupBy(r => string.IsNullOrWhiteSpace(r.OccupationalLevel) ? "Not specified" : r.OccupationalLevel)
                .OrderByDescending(g => g.Count())
                .Select(g => new { Label = g.Key, Count = g.Count() })
                .ToList();

            // ---- Build the workbook ----
            // Per-employee-per-day expected HostLocation, for the combined
            // sheet's "Expected Location" column. Keyed by "name|yyyy-MM-dd"
            // (a plain string, not an anonymous type) so this dictionary
            // can be passed as a parameter to another method.
            var scheduledLocationByEmployeeDate = scheduledDays
                .GroupBy(s => s.EmployeeName + "|" + s.ScheduleDate.Date.ToString("yyyy-MM-dd"))
                .ToDictionary(g => g.Key, g => g.First().HostLocation);

            var scheduledSitesByEmployee = scheduledDays
                .GroupBy(s => s.EmployeeName)
                .ToDictionary(g => g.Key, g => string.Join(", ", g.Select(s => s.HostLocation).Distinct().OrderBy(x => x)));

            using (var package = new ExcelPackage())
            {
                BuildCoverSheet(package, startDate, endDate, dept, locationFilter, lateThreshold);
                BuildOverviewSheet(package, totalCheckIns, expectedCheckIns, dateRangeDays, onTimeCount, lateCount,
                    averageArrivalTime, lateThreshold, rows, scheduledDays, sites);
                BuildGuideSheet(package);
                BuildCombinedCheckInSheet(package, rows, lateThreshold, employeeAttendance.Select(e =>
                    Tuple.Create(e.EmployeeName, e.ExpectedDays, e.ActualDays, e.RatePercent, e.PrimaryLocation)).ToList(),
                    scheduledLocationByEmployeeDate, scheduledSitesByEmployee);
                BuildAttendanceByDepartmentSheet(package, attendanceRateByDepartment.Select(d =>
                    Tuple.Create(d.Label, d.CheckedIn, d.Headcount, d.RatePercent)).ToList());
                BuildLabelCountSheet(package, "Check-ins by Department", "Department", byDepartment.Select(d => Tuple.Create(d.Label, d.Count)).ToList(), eChartType.BarClustered, null);
                BuildDayOfWeekSheet(package, rows);
                BuildHourByWeekSheet(package, hourByWeek);
                BuildByMonthSheet(package, byMonth.Select(m => Tuple.Create(m.Label, m.ActualCheckIns, m.PlannedSeatDays, m.Locations)).ToList());
                BuildByLocationSheet(package, rows, allWeekStarts, sites);
                BuildLabelCountSheet(package, "Occupational Level", "Level", byOccupationalLevel.Select(o => Tuple.Create(o.Label, o.Count)).ToList(), eChartType.Pie, null);

                var bytes = package.GetAsByteArray();
                string fileName = "FASSET_CheckIn_Report_" + startDate.ToString("yyyy-MM-dd") + "_to_" + endDate.ToString("yyyy-MM-dd") + ".xlsx";
                return File(bytes, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", fileName);
            }
        }

        // ---- Workbook-building helpers ----

        private void StyleHeaderCell(ExcelRange cell, Color bg)
        {
            cell.Style.Font.Bold = true;
            cell.Style.Font.Color.SetColor(Color.White);
            cell.Style.Fill.PatternType = ExcelFillStyle.Solid;
            cell.Style.Fill.BackgroundColor.SetColor(bg);
            cell.Style.VerticalAlignment = ExcelVerticalAlignment.Center;
        }

        // Excel table/chart names must start with a letter or underscore
        // and contain only letters, numbers, underscores, or periods - no
        // spaces, hyphens, or other punctuation. Strips anything else out.
        private static string SanitizeExcelName(string raw)
        {
            var chars = raw.Where(c => char.IsLetterOrDigit(c) || c == '_').ToArray();
            var cleaned = new string(chars);
            if (cleaned.Length == 0 || char.IsDigit(cleaned[0]))
            {
                cleaned = "_" + cleaned;
            }
            return cleaned;
        }

        private void ApplyThinBorder(ExcelRange range)
        {
            range.Style.Border.Top.Style = ExcelBorderStyle.Thin;
            range.Style.Border.Bottom.Style = ExcelBorderStyle.Thin;
            range.Style.Border.Left.Style = ExcelBorderStyle.Thin;
            range.Style.Border.Right.Style = ExcelBorderStyle.Thin;
            range.Style.Border.Top.Color.SetColor(Color.FromArgb(0xd7, 0xdd, 0xe3));
            range.Style.Border.Bottom.Color.SetColor(Color.FromArgb(0xd7, 0xdd, 0xe3));
            range.Style.Border.Left.Color.SetColor(Color.FromArgb(0xd7, 0xdd, 0xe3));
            range.Style.Border.Right.Color.SetColor(Color.FromArgb(0xd7, 0xdd, 0xe3));
        }

        private void BuildCoverSheet(ExcelPackage package, DateTime startDate, DateTime endDate, string dept, string location, TimeSpan lateThreshold)
        {
            var ws = package.Workbook.Worksheets.Add("Cover");
            ws.View.ShowGridLines = false;

            // All 10 columns share the same width, so the logo (centered
            // within columns A-J), the title (merged/centered across the
            // same A-J span), and the info table (starting at the same
            // column the logo starts at) all line up on one visual axis.
            for (int c = 1; c <= 10; c++) ws.Column(c).Width = 15;

            try
            {
                var logoPath = Server.MapPath("~/Content/Small_Fasset.png");
                if (System.IO.File.Exists(logoPath))
                {
                    using (var img = Image.FromFile(logoPath))
                    {
                        var pic = ws.Drawings.AddPicture("Logo", img);
                        pic.SetPosition(1, 0, 4, 25);
                        pic.SetSize(170, 170);
                    }
                }
            }
            catch
            {
                // Logo is a nice-to-have - never let a missing/locked file
                // prevent the report itself from being generated.
            }

            ws.Cells["A11:J11"].Merge = true;
            ws.Cells["A11"].Value = "FASSET® Check-In Report";
            ws.Cells["A11"].Style.Font.Size = 26;
            ws.Cells["A11"].Style.Font.Bold = true;
            ws.Cells["A11"].Style.Font.Color.SetColor(BrandNavy);
            ws.Cells["A11"].Style.HorizontalAlignment = ExcelHorizontalAlignment.Center;

            ws.Cells["A12:J12"].Merge = true;
            ws.Cells["A12"].Value = "Attendance & workforce breakdown";
            ws.Cells["A12"].Style.Font.Size = 13;
            ws.Cells["A12"].Style.Font.Color.SetColor(Color.Gray);
            ws.Cells["A12"].Style.HorizontalAlignment = ExcelHorizontalAlignment.Center;

            // Info table starts at column E - the same column the logo is
            // anchored to above - so the whole block reads as one centered
            // unit instead of three separately-positioned pieces.
            int tableStartRow = 15;
            int tableStartCol = 5;
            ws.Cells[tableStartRow, tableStartCol].Value = "Field";
            ws.Cells[tableStartRow, tableStartCol + 1].Value = "Value";
            StyleHeaderCell(ws.Cells[tableStartRow, tableStartCol], BrandNavy);
            StyleHeaderCell(ws.Cells[tableStartRow, tableStartCol + 1], BrandNavy);

            var infoRows = new List<Tuple<string, string>>
            {
                Tuple.Create("Report range", startDate.ToString("d MMMM yyyy") + " \u2013 " + endDate.ToString("d MMMM yyyy")),
                Tuple.Create("Generated", DateTime.Now.ToString("d MMMM yyyy, HH:mm")),
                Tuple.Create("Department filter", dept ?? "All departments"),
                Tuple.Create("Location filter", location ?? "All locations"),
                Tuple.Create("Late after", lateThreshold.ToString(@"hh\:mm"))
            };

            for (int i = 0; i < infoRows.Count; i++)
            {
                int r = tableStartRow + 1 + i;
                ws.Cells[r, tableStartCol].Value = infoRows[i].Item1;
                ws.Cells[r, tableStartCol].Style.Font.Bold = true;
                ws.Cells[r, tableStartCol + 1].Value = infoRows[i].Item2;
                if (i % 2 == 1)
                {
                    ws.Cells[r, tableStartCol, r, tableStartCol + 1].Style.Fill.PatternType = ExcelFillStyle.Solid;
                    ws.Cells[r, tableStartCol, r, tableStartCol + 1].Style.Fill.BackgroundColor.SetColor(LightRowFill);
                }
            }

            var fullTableRange = ws.Cells[tableStartRow, tableStartCol, tableStartRow + infoRows.Count, tableStartCol + 1];
            ApplyThinBorder(fullTableRange);
            ws.Column(tableStartCol + 1).Width = 30;

            ws.Cells[tableStartRow + infoRows.Count + 3, 1, tableStartRow + infoRows.Count + 3, 10].Merge = true;
            ws.Cells[tableStartRow + infoRows.Count + 3, 1].Value = "See the \"Guide\" sheet for what each tab in this workbook shows.";
            ws.Cells[tableStartRow + infoRows.Count + 3, 1].Style.Font.Italic = true;
            ws.Cells[tableStartRow + infoRows.Count + 3, 1].Style.Font.Color.SetColor(Color.Gray);
            ws.Cells[tableStartRow + infoRows.Count + 3, 1].Style.HorizontalAlignment = ExcelHorizontalAlignment.Center;
        }

        private void BuildOverviewSheet(ExcelPackage package, int totalCheckIns, int expectedCheckIns, int dateRangeDays,
            int onTimeCount, int lateCount, string averageArrivalTime, TimeSpan lateThreshold,
            List<CheckInReportRow> rows, List<ScheduledDayRow> scheduledDays, List<SiteInfo> sites)
        {
            var ws = package.Workbook.Worksheets.Add("Overview");
            for (int c = 1; c <= 10; c++) ws.Column(c).Width = 20;

            ws.Cells["A1"].Value = "Overview";
            ws.Cells["A1"].Style.Font.Size = 16;
            ws.Cells["A1"].Style.Font.Bold = true;
            ws.Cells["A1"].Style.Font.Color.SetColor(BrandNavy);

            int execTotal = onTimeCount + lateCount;
            string onTimePct = execTotal > 0 ? Math.Round(100.0 * onTimeCount / execTotal, 1) + "%" : "-";

            string paragraph = string.Format(
                "This report covers {0} day{1}, during which {2} check-in{3} were recorded against {4} planned seat-day{5} for the period. " +
                "The average arrival time was {6}, with {7} of check-ins on time (before {8}) and {9} late ({10} of the total).",
                dateRangeDays, dateRangeDays == 1 ? "" : "s",
                totalCheckIns, totalCheckIns == 1 ? "" : "s",
                expectedCheckIns, expectedCheckIns == 1 ? "" : "s",
                averageArrivalTime,
                onTimePct,
                lateThreshold.ToString(@"hh\:mm"),
                lateCount,
                execTotal > 0 ? Math.Round(100.0 * lateCount / execTotal, 1) + "%" : "-");

            ws.Cells[3, 1, 3, 8].Merge = true;
            ws.Cells[3, 1].Value = paragraph;
            ws.Cells[3, 1].Style.WrapText = true;
            ws.Cells[3, 1].Style.VerticalAlignment = ExcelVerticalAlignment.Center;
            ws.Row(3).Height = 55;

            // Per-location cards are now keyed by SITE NAME (matched via
            // GetNearestSiteName against the same mst_Sites table the
            // By Location "Status" column uses) rather than raw resolved
            // street address - that's what lets Actual (GPS-derived) and
            // Expected (Schedules.HostLocation-derived) share a key space.
            var actualBySite = rows
                .GroupBy(r => GetNearestSiteName(RoundLocation(r.GeoLocation), sites) ?? "Unmatched to a site")
                .ToDictionary(g => g.Key, g => g.Count());
            var expectedBySite = scheduledDays
                .GroupBy(s => s.HostLocation ?? "Unknown")
                .ToDictionary(g => g.Key, g => g.Count());

            var onTimeBySite = rows.Where(r => r.DateCreated.TimeOfDay <= lateThreshold)
                .GroupBy(r => GetNearestSiteName(RoundLocation(r.GeoLocation), sites) ?? "Unmatched to a site")
                .ToDictionary(g => g.Key, g => g.Count());
            var lateBySite = rows.Where(r => r.DateCreated.TimeOfDay > lateThreshold)
                .GroupBy(r => GetNearestSiteName(RoundLocation(r.GeoLocation), sites) ?? "Unmatched to a site")
                .ToDictionary(g => g.Key, g => g.Count());

            var allSiteNames = new HashSet<string>(actualBySite.Keys);
            allSiteNames.UnionWith(expectedBySite.Keys);
            var topSites = allSiteNames
                .OrderByDescending(s => actualBySite.ContainsKey(s) ? actualBySite[s] : 0)
                .Take(3)
                .ToList();

            // ---- Row 1: Total check-ins (actual/expected) + one card per
            // top site, each also showing actual/expected. ----
            var row1Labels = new List<string> { "Total check-ins" };
            var row1Values = new List<string> { totalCheckIns + " / " + expectedCheckIns };
            var row1Colors = new List<Color> { BrandNavy };
            Color[] locationCardColors = { BrandTeal, BrandGreen, Color.FromArgb(0x7d, 0x5b, 0xa6) };

            for (int i = 0; i < topSites.Count; i++)
            {
                string site = topSites[i];
                int actual = actualBySite.ContainsKey(site) ? actualBySite[site] : 0;
                int expected = expectedBySite.ContainsKey(site) ? expectedBySite[site] : 0;
                row1Labels.Add("Total check-ins \u2014 " + site);
                row1Values.Add(actual + " / " + expected);
                row1Colors.Add(locationCardColors[i % locationCardColors.Length]);
            }

            for (int i = 0; i < row1Labels.Count; i++)
            {
                WriteKpiCard(ws, 6, i * 2 + 1, row1Labels[i], row1Values[i], row1Colors[i]);
            }

            // Total's sub-line: nicer multi-line bulleted breakdown instead
            // of one long dot-separated string.
            string totalSub = topSites.Count > 0
                ? string.Join("\n", topSites.Select(s => "\u2022 " + s + ": " + (actualBySite.ContainsKey(s) ? actualBySite[s] : 0) + " / " + (expectedBySite.ContainsKey(s) ? expectedBySite[s] : 0)))
                : "No check-ins in range";
            WriteKpiSub(ws, 8, 1, 2, totalSub, Math.Max(1, topSites.Count));

            // ---- Row 2: On-time / Late, each with a per-site sub ----
            WriteKpiCard(ws, 11, 1, "On time", onTimeCount.ToString(), BrandGreen);
            WriteKpiCard(ws, 11, 3, "Late", lateCount.ToString(), BrandRed);

            string onTimeSub = topSites.Count > 0
                ? string.Join("\n", topSites.Select(s => "\u2022 " + s + ": " + (onTimeBySite.ContainsKey(s) ? onTimeBySite[s] : 0)))
                : "No check-ins in range";
            string lateSub = topSites.Count > 0
                ? string.Join("\n", topSites.Select(s => "\u2022 " + s + ": " + (lateBySite.ContainsKey(s) ? lateBySite[s] : 0)))
                : "No check-ins in range";
            WriteKpiSub(ws, 13, 1, 2, onTimeSub, Math.Max(1, topSites.Count));
            WriteKpiSub(ws, 13, 3, 4, lateSub, Math.Max(1, topSites.Count));

            // ---- Charts: moved left and closer together, instead of
            // spreading across the full sheet width. ----
            var dataWs = package.Workbook.Worksheets.Add("_OverviewData");
            dataWs.Hidden = eWorkSheetHidden.VeryHidden;

            dataWs.Cells["A1"].Value = "Status";
            dataWs.Cells["B1"].Value = "Count";
            dataWs.Cells["A2"].Value = "On-time";
            dataWs.Cells["B2"].Value = onTimeCount;
            dataWs.Cells["A3"].Value = "Late";
            dataWs.Cells["B3"].Value = lateCount;

            var pie = ws.Drawings.AddChart("chartLateVsOnTime", eChartType.Pie);
            pie.Title.Text = "Late vs. On-Time";
            pie.Series.Add(dataWs.Cells["B2:B3"], dataWs.Cells["A2:A3"]);
            pie.SetPosition(17, 0, 0, 0);
            pie.SetSize(360, 250);

            dataWs.Cells["A5"].Value = "Metric";
            dataWs.Cells["B5"].Value = "Count";
            dataWs.Cells["A6"].Value = "Actual check-ins";
            dataWs.Cells["B6"].Value = totalCheckIns;
            dataWs.Cells["A7"].Value = "Expected / Planned check-ins";
            dataWs.Cells["B7"].Value = expectedCheckIns;

            var cmpChart = ws.Drawings.AddChart("chartActualVsPlanned", eChartType.ColumnClustered);
            cmpChart.Title.Text = "Actual vs. Planned Check-ins";
            var cmpSeries = cmpChart.Series.Add(dataWs.Cells["B6:B7"], dataWs.Cells["A6:A7"]);
            cmpSeries.Header = "Count";
            cmpChart.Legend.Remove();
            cmpChart.VaryColors = true;
            cmpChart.SetPosition(17, 0, 5, 0);
            cmpChart.SetSize(360, 250);
        }

        private void WriteKpiCard(ExcelWorksheet ws, int labelRow, int col, string label, string value, Color color)
        {
            var labelCell = ws.Cells[labelRow, col];
            var valueCell = ws.Cells[labelRow + 1, col];
            labelCell.Value = label;
            valueCell.Value = value;
            labelCell.Style.Fill.PatternType = ExcelFillStyle.Solid;
            labelCell.Style.Fill.BackgroundColor.SetColor(color);
            labelCell.Style.Font.Color.SetColor(Color.White);
            labelCell.Style.Font.Bold = true;
            valueCell.Style.Fill.PatternType = ExcelFillStyle.Solid;
            valueCell.Style.Fill.BackgroundColor.SetColor(color);
            valueCell.Style.Font.Color.SetColor(Color.White);
            valueCell.Style.Font.Size = 16;
            valueCell.Style.Font.Bold = true;
        }

        private void WriteKpiSub(ExcelWorksheet ws, int row, int colStart, int colEnd, string text, int lineCount)
        {
            ws.Cells[row, colStart, row, colEnd].Merge = true;
            ws.Cells[row, colStart].Value = text;
            ws.Cells[row, colStart].Style.Font.Size = 9;
            ws.Cells[row, colStart].Style.Font.Color.SetColor(Color.Gray);
            ws.Cells[row, colStart].Style.WrapText = true;
            ws.Cells[row, colStart].Style.VerticalAlignment = ExcelVerticalAlignment.Top;
            ws.Row(row).Height = 14 * lineCount + 8;
        }


        // Trims a full resolved address down to something short enough to
        // sit in a KPI card sub-line (e.g. "137 11th Rd, Carlswald, Midrand,
        // 1684, South Africa" -> "137 11th Rd, Carlswald").
        private static string ShortenLocationLabel(string fullLabel)
        {
            if (string.IsNullOrWhiteSpace(fullLabel)) return fullLabel;
            var parts = fullLabel.Split(',');
            return parts.Length <= 2 ? fullLabel : (parts[0].Trim() + ", " + parts[1].Trim());
        }

        private void BuildGuideSheet(ExcelPackage package)
        {
            var ws = package.Workbook.Worksheets.Add("Guide");

            ws.Cells["A1"].Value = "What each sheet shows";
            ws.Cells["A1"].Style.Font.Size = 16;
            ws.Cells["A1"].Style.Font.Bold = true;
            ws.Cells["A1"].Style.Font.Color.SetColor(BrandNavy);

            var entries = new List<Tuple<string, string>>
            {
                Tuple.Create("Cover", "Report title, logo, and the date range/filters this workbook was generated with, in a table."),
                Tuple.Create("Overview", "A summary paragraph, four KPI cards (Total Check-ins, with a sub-line breaking it down by location; Expected/Planned Check-ins; On Time; Late), and two charts: Actual vs. Planned Check-ins, and Late vs. On-Time."),
                Tuple.Create("Check-Ins & Attendance", "One row per individual check-in (employee, department, resolved location, date, time, late or on-time), with that employee's Expected (scheduled days), Actual (days checked in), Rate % = Actual \u00f7 Expected for this date range (\"Not scheduled\" means they checked in without being rostered at all), and Primary Location (their single most common check-in spot) repeated alongside it. Employees who never checked in at all still get one row each. Click the dropdown arrow on any column header to filter."),
                Tuple.Create("Attendance by Department", "Per department: headcount, how many checked in during this range, and the resulting attendance rate."),
                Tuple.Create("Check-ins by Department", "Total check-ins per department, as a table and bar chart."),
                Tuple.Create("By Day of Week", "Total check-ins for each day of the week, Monday through Sunday, summed across every week in the selected range onto one axis."),
                Tuple.Create("By Week", "Total check-ins per week, broken out by location as separate columns (plus a Total column), so you can compare how each site trended week to week."),
                Tuple.Create("By Hour", "Total check-ins for each hour of the day, summed across the whole selected range onto one axis."),
                Tuple.Create("By Hour (by Week)", "The same hourly breakdown, but split out per week and charted. Use the dropdown on the Week column header to filter to a single week \u2014 the chart updates with it."),
                Tuple.Create("By Month", "Per calendar month: actual check-ins, Planned Seat-Days (total scheduled seat-days across everyone that month, from the Schedules table), and which location(s) were checked into that month."),
                Tuple.Create("By Location", "Every resolved check-in location, how many check-ins came from it, and a Status showing whether it falls inside a configured site's radius or how far outside the nearest one it was."),
                Tuple.Create("Occupational Level", "Check-in counts grouped by occupational level (e.g. Skilled, Management), as a pie chart.")
            };

            int headerRow = 3;
            ws.Cells[headerRow, 1].Value = "Sheet";
            ws.Cells[headerRow, 2].Value = "What it shows";
            StyleHeaderCell(ws.Cells[headerRow, 1], BrandNavy);
            StyleHeaderCell(ws.Cells[headerRow, 2], BrandNavy);

            int row = headerRow + 1;
            foreach (var e in entries)
            {
                ws.Cells[row, 1].Value = e.Item1;
                ws.Cells[row, 1].Style.Font.Bold = true;
                ws.Cells[row, 2].Value = e.Item2;
                ws.Cells[row, 2].Style.WrapText = true;
                ws.Row(row).Height = 40;
                row++;
            }

            var tableRange = ws.Cells[headerRow, 1, row - 1, 2];
            var table = ws.Tables.Add(tableRange, "GuideTable");
            table.TableStyle = TableStyles.Medium9;

            ws.Column(1).Width = 26;
            ws.Column(2).Width = 100;
        }

        // Combines the old separate "Raw Data" and "Employee Attendance"
        // sheets into one: every check-in row now also carries that
        // employee's Expected/Actual/Rate%/Primary Location alongside it
        // (repeated per row - standard denormalized reporting style),
        // plus their Expected Location for that specific day (from the
        // schedule) next to the Actual Location they actually checked in
        // from. Employees with zero check-ins in range still get one
        // placeholder row each, so nobody who was scheduled just silently
        // disappears.
        private void BuildCombinedCheckInSheet(ExcelPackage package, List<CheckInReportRow> rows, TimeSpan lateThreshold,
            List<Tuple<string, int, int, double?, string>> employeeAttendance,
            Dictionary<string, string> scheduledLocationByEmployeeDate,
            Dictionary<string, string> scheduledSitesByEmployee)
        {
            var ws = package.Workbook.Worksheets.Add("Check-Ins & Attendance");
            string[] headers = { "Employee", "Department", "Actual Location", "Expected Location", "Date", "Time", "Late?", "Expected", "Actual", "Rate %", "Primary Location" };
            for (int i = 0; i < headers.Length; i++)
            {
                StyleHeaderCell(ws.Cells[1, i + 1], BrandNavy);
                ws.Cells[1, i + 1].Value = headers[i];
            }

            var attendanceByName = employeeAttendance.ToDictionary(e => e.Item1, e => e);
            var namesWithCheckIns = new HashSet<string>();

            int r = 2;
            foreach (var row in rows.OrderBy(x => x.EmployeeName).ThenBy(x => x.DateCreated))
            {
                namesWithCheckIns.Add(row.EmployeeName);

                ws.Cells[r, 1].Value = row.EmployeeName;
                ws.Cells[r, 2].Value = row.DepartmentName ?? "Unknown";
                ws.Cells[r, 3].Value = ResolvePlaceName(RoundLocation(row.GeoLocation));

                var scheduleKey = row.EmployeeName + "|" + row.DateCreated.Date.ToString("yyyy-MM-dd");
                string expectedLocation;
                ws.Cells[r, 4].Value = scheduledLocationByEmployeeDate.TryGetValue(scheduleKey, out expectedLocation)
                    ? expectedLocation
                    : "Not scheduled that day";

                ws.Cells[r, 5].Value = row.DateCreated.ToString("yyyy-MM-dd");
                ws.Cells[r, 6].Value = row.DateCreated.ToString("HH:mm");

                bool isLate = row.DateCreated.TimeOfDay > lateThreshold;
                ws.Cells[r, 7].Value = isLate ? "Late" : "On time";
                ws.Cells[r, 7].Style.Font.Color.SetColor(isLate ? BrandRed : BrandGreen);

                Tuple<string, int, int, double?, string> att;
                if (attendanceByName.TryGetValue(row.EmployeeName, out att))
                {
                    ws.Cells[r, 8].Value = att.Item2;
                    ws.Cells[r, 9].Value = att.Item3;
                    if (att.Item4.HasValue)
                    {
                        ws.Cells[r, 10].Value = att.Item4.Value;
                        ws.Cells[r, 10].Style.Numberformat.Format = "0.0\"%\"";
                    }
                    else
                    {
                        ws.Cells[r, 10].Value = "Not scheduled";
                    }
                    ws.Cells[r, 11].Value = att.Item5;
                }
                r++;
            }

            // Scheduled employees who never checked in at all don't appear
            // in `rows`, so they need their own placeholder row here.
            foreach (var att in employeeAttendance.Where(a => !namesWithCheckIns.Contains(a.Item1)).OrderBy(a => a.Item1))
            {
                ws.Cells[r, 1].Value = att.Item1;
                ws.Cells[r, 2].Value = "-";
                ws.Cells[r, 3].Value = "-";
                string sites;
                ws.Cells[r, 4].Value = scheduledSitesByEmployee.TryGetValue(att.Item1, out sites) ? sites : "-";
                ws.Cells[r, 5].Value = "-";
                ws.Cells[r, 6].Value = "-";
                ws.Cells[r, 7].Value = "-";
                ws.Cells[r, 8].Value = att.Item2;
                ws.Cells[r, 9].Value = att.Item3;
                if (att.Item4.HasValue)
                {
                    ws.Cells[r, 10].Value = att.Item4.Value;
                    ws.Cells[r, 10].Style.Numberformat.Format = "0.0\"%\"";
                }
                else
                {
                    ws.Cells[r, 10].Value = "Not scheduled";
                }
                ws.Cells[r, 11].Value = att.Item5;
                r++;
            }

            if (r > 2)
            {
                var range = ws.Cells[1, 1, r - 1, headers.Length];
                var table = ws.Tables.Add(range, "CombinedCheckInTable");
                table.TableStyle = TableStyles.Medium9;
            }

            ws.Column(1).Width = 22;
            ws.Column(2).Width = 18;
            ws.Column(3).Width = 45;
            ws.Column(4).Width = 20;
            ws.Column(5).Width = 12;
            ws.Column(6).Width = 8;
            ws.Column(7).Width = 9;
            ws.Column(8).Width = 10;
            ws.Column(9).Width = 8;
            ws.Column(10).Width = 10;
            ws.Column(11).Width = 45;

        }

        private void BuildRawDataSheet(ExcelPackage package, List<CheckInReportRow> rows, TimeSpan lateThreshold)
        {
            var ws = package.Workbook.Worksheets.Add("Raw Data");
            string[] headers = { "Employee", "Department", "Location", "Date", "Time", "Late?" };
            for (int i = 0; i < headers.Length; i++)
            {
                StyleHeaderCell(ws.Cells[1, i + 1], BrandNavy);
                ws.Cells[1, i + 1].Value = headers[i];
            }

            int r = 2;
            foreach (var row in rows.OrderBy(x => x.DateCreated))
            {
                ws.Cells[r, 1].Value = row.EmployeeName;
                ws.Cells[r, 2].Value = row.DepartmentName ?? "Unknown";
                ws.Cells[r, 3].Value = ResolvePlaceName(RoundLocation(row.GeoLocation));
                ws.Cells[r, 4].Value = row.DateCreated.ToString("yyyy-MM-dd");
                ws.Cells[r, 5].Value = row.DateCreated.ToString("HH:mm");
                ws.Cells[r, 6].Value = row.DateCreated.TimeOfDay > lateThreshold ? "Late" : "On time";
                if (row.DateCreated.TimeOfDay > lateThreshold)
                {
                    ws.Cells[r, 6].Style.Font.Color.SetColor(BrandRed);
                }
                else
                {
                    ws.Cells[r, 6].Style.Font.Color.SetColor(BrandGreen);
                }
                r++;
            }

            if (r > 2)
            {
                var range = ws.Cells[1, 1, r - 1, headers.Length];
                var table = ws.Tables.Add(range, "RawDataTable");
                table.TableStyle = TableStyles.Medium9;
            }

            ws.Cells[ws.Dimension.Address].AutoFitColumns();
        }

        private void BuildEmployeeAttendanceSheet(ExcelPackage package, List<Tuple<string, int, int, double?, string>> rows)
        {
            var ws = package.Workbook.Worksheets.Add("Employee Attendance");
            string[] headers = { "Employee", "Expected", "Actual", "Rate %", "Primary Location" };
            for (int i = 0; i < headers.Length; i++)
            {
                StyleHeaderCell(ws.Cells[1, i + 1], BrandNavy);
                ws.Cells[1, i + 1].Value = headers[i];
            }

            int r = 2;
            foreach (var row in rows)
            {
                ws.Cells[r, 1].Value = row.Item1;
                ws.Cells[r, 2].Value = row.Item2;
                ws.Cells[r, 3].Value = row.Item3;
                if (row.Item4.HasValue)
                {
                    ws.Cells[r, 4].Value = row.Item4.Value;
                    ws.Cells[r, 4].Style.Numberformat.Format = "0.0\"%\"";
                }
                else
                {
                    ws.Cells[r, 4].Value = "Not scheduled";
                }
                ws.Cells[r, 5].Value = row.Item5;
                r++;
            }

            if (r > 2)
            {
                var range = ws.Cells[1, 1, r - 1, headers.Length];
                var table = ws.Tables.Add(range, "EmployeeAttendanceTable");
                table.TableStyle = TableStyles.Medium9;
            }

            ws.Cells[ws.Dimension.Address].AutoFitColumns();
        }

        private void BuildAttendanceByDepartmentSheet(ExcelPackage package, List<Tuple<string, int, int, double>> rows)
        {
            var ws = package.Workbook.Worksheets.Add("Attendance by Department");
            string[] headers = { "Department", "Checked In", "Headcount", "Rate %" };
            for (int i = 0; i < headers.Length; i++)
            {
                StyleHeaderCell(ws.Cells[1, i + 1], BrandNavy);
                ws.Cells[1, i + 1].Value = headers[i];
            }

            int r = 2;
            foreach (var row in rows)
            {
                ws.Cells[r, 1].Value = row.Item1;
                ws.Cells[r, 2].Value = row.Item2;
                ws.Cells[r, 3].Value = row.Item3;
                ws.Cells[r, 4].Value = row.Item4;
                ws.Cells[r, 4].Style.Numberformat.Format = "0.0\"%\"";
                r++;
            }

            if (r > 2)
            {
                ws.Tables.Add(ws.Cells[1, 1, r - 1, headers.Length], "AttendanceByDeptTable").TableStyle = TableStyles.Medium9;
            }
            ws.Cells[ws.Dimension.Address].AutoFitColumns();
        }

        // Generic "label, count" sheet used for several of the simpler
        // breakdowns (Department, Day of Week, Hour, Occupational Level) -
        // table plus one chart, whose type the caller picks. `caption`, if
        // given, is shown as an italic note above the table (used to
        // clarify that a sheet's totals span the whole selected range).
        private void BuildLabelCountSheet(ExcelPackage package, string sheetName, string labelHeader, List<Tuple<string, int>> rows, eChartType chartType, string caption)
        {
            var ws = package.Workbook.Worksheets.Add(sheetName);

            int headerRow = 1;
            if (!string.IsNullOrEmpty(caption))
            {
                ws.Cells[1, 1, 1, 4].Merge = true;
                ws.Cells[1, 1].Value = caption;
                ws.Cells[1, 1].Style.Font.Italic = true;
                ws.Cells[1, 1].Style.Font.Color.SetColor(Color.Gray);
                ws.Cells[1, 1].Style.WrapText = true;
                ws.Row(1).Height = 30;
                headerRow = 2;
            }

            ws.Cells[headerRow, 1].Value = labelHeader;
            ws.Cells[headerRow, 2].Value = "Check-ins";
            StyleHeaderCell(ws.Cells[headerRow, 1], BrandNavy);
            StyleHeaderCell(ws.Cells[headerRow, 2], BrandNavy);

            for (int i = 0; i < rows.Count; i++)
            {
                ws.Cells[headerRow + 1 + i, 1].Value = rows[i].Item1;
                ws.Cells[headerRow + 1 + i, 2].Value = rows[i].Item2;
            }

            if (rows.Count > 0)
            {
                var tableRange = ws.Cells[headerRow, 1, headerRow + rows.Count, 2];
                var table = ws.Tables.Add(tableRange, "Tbl_" + SanitizeExcelName(sheetName));
                table.TableStyle = TableStyles.Medium9;

                var chart = ws.Drawings.AddChart("chart_" + SanitizeExcelName(sheetName), chartType);
                chart.Title.Text = sheetName;
                chart.VaryColors = true;
                var series = chart.Series.Add(
                    ws.Cells[headerRow + 1, 2, headerRow + rows.Count, 2],
                    ws.Cells[headerRow + 1, 1, headerRow + rows.Count, 1]);
                series.Header = "Check-ins";

                if (chartType != eChartType.Pie)
                {
                    // Single series - the axis labels already say what each
                    // bar/column is, so a "Series1" legend just adds clutter.
                    chart.Legend.Remove();
                    chart.XAxis.Title.Text = labelHeader;
                    chart.YAxis.Title.Text = "Check-ins";
                }

                chart.SetPosition(headerRow - 1, 0, 4, 0);
                chart.SetSize(460, 280);
            }

            ws.Column(1).Width = 26;
            ws.Column(2).Width = 14;
        }

        // By Day of Week, broken out per resolved location as separate
        // columns (plus a Total) - same pattern as By Location's weekly
        // breakdown, so you can see which site drives which weekday's
        // numbers. Replaces the old single-column version and the
        // standalone By Week sheet, which this (plus By Location's weekly
        // breakdown) now covers.
        private void BuildDayOfWeekSheet(ExcelPackage package, List<CheckInReportRow> rows)
        {
            var ws = package.Workbook.Worksheets.Add("By Day of Week");
            string[] dayOrder = { "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday", "Sunday" };

            var distinctLocations = rows
                .Select(r => ResolvePlaceName(RoundLocation(r.GeoLocation)))
                .Distinct()
                .OrderBy(x => x)
                .ToList();

            var counts = rows
                .GroupBy(r => new { Day = r.DateCreated.DayOfWeek.ToString(), Loc = ResolvePlaceName(RoundLocation(r.GeoLocation)) })
                .ToDictionary(g => g.Key, g => g.Count());

            int totalCol = 1 + distinctLocations.Count + 1;

            ws.Cells[1, 1, 1, totalCol].Merge = true;
            ws.Cells[1, 1].Value = "(every week combined onto one Mon\u2013Sun axis) \u2014 this is not a single week's numbers.";
            ws.Cells[1, 1].Style.Font.Italic = true;
            ws.Cells[1, 1].Style.Font.Color.SetColor(Color.Gray);
            ws.Cells[1, 1].Style.WrapText = true;
            ws.Row(1).Height = 30;

            int headerRow = 2;
            ws.Cells[headerRow, 1].Value = "Day";
            StyleHeaderCell(ws.Cells[headerRow, 1], BrandNavy);
            for (int i = 0; i < distinctLocations.Count; i++)
            {
                ws.Cells[headerRow, 2 + i].Value = distinctLocations[i];
                StyleHeaderCell(ws.Cells[headerRow, 2 + i], BrandNavy);
            }
            ws.Cells[headerRow, totalCol].Value = "Total";
            StyleHeaderCell(ws.Cells[headerRow, totalCol], BrandNavy);

            for (int d = 0; d < dayOrder.Length; d++)
            {
                int r = headerRow + 1 + d;
                ws.Cells[r, 1].Value = dayOrder[d];

                int rowTotal = 0;
                for (int i = 0; i < distinctLocations.Count; i++)
                {
                    var key = new { Day = dayOrder[d], Loc = distinctLocations[i] };
                    int count = counts.ContainsKey(key) ? counts[key] : 0;
                    ws.Cells[r, 2 + i].Value = count;
                    rowTotal += count;
                }
                ws.Cells[r, totalCol].Value = rowTotal;
                ws.Cells[r, totalCol].Style.Font.Bold = true;
            }

            ws.Tables.Add(ws.Cells[headerRow, 1, headerRow + dayOrder.Length, totalCol], "ByDayOfWeekTable").TableStyle = TableStyles.Medium9;

            ws.Column(1).Width = 14;
            for (int i = 0; i < distinctLocations.Count; i++) ws.Column(2 + i).Width = 22;
            ws.Column(totalCol).Width = 10;
        }

        private void BuildHourByWeekSheet(ExcelPackage package, List<Tuple<string, string, int>> rows)
        {
            var ws = package.Workbook.Worksheets.Add("By Hour (by Week)");
            string[] headers = { "Week", "Hour", "Check-ins" };
            for (int i = 0; i < headers.Length; i++)
            {
                StyleHeaderCell(ws.Cells[1, i + 1], BrandNavy);
                ws.Cells[1, i + 1].Value = headers[i];
            }

            for (int i = 0; i < rows.Count; i++)
            {
                ws.Cells[i + 2, 1].Value = rows[i].Item1;
                ws.Cells[i + 2, 2].Value = rows[i].Item2;
                ws.Cells[i + 2, 3].Value = rows[i].Item3;
            }

            if (rows.Count > 0)
            {
                // This Table is what gives the Week column a real Excel
                // filter dropdown - select one week's rows via the arrow
                // on the header, same as any other AutoFilter. The chart
                // below reads from the same range, so filtering the table
                // to one week updates the chart too.
                ws.Tables.Add(ws.Cells[1, 1, rows.Count + 1, headers.Length], "HourByWeekTable").TableStyle = TableStyles.Medium9;

                var chart = ws.Drawings.AddChart("chartHourByWeek", eChartType.Line);
                chart.Title.Text = "Check-ins by Hour (filter by Week above)";
                var series = chart.Series.Add(ws.Cells[2, 3, rows.Count + 1, 3], ws.Cells[2, 2, rows.Count + 1, 2]);
                series.Header = "Check-ins";
                chart.Legend.Remove();
                chart.XAxis.Title.Text = "Hour";
                chart.YAxis.Title.Text = "Check-ins";
                chart.SetPosition(0, 0, 5, 0);
                chart.SetSize(480, 280);
            }

            ws.Column(1).Width = 22;
            ws.Column(2).Width = 10;
            ws.Column(3).Width = 12;
        }

        private void BuildByMonthSheet(ExcelPackage package, List<Tuple<string, int, int, string>> rows)
        {
            var ws = package.Workbook.Worksheets.Add("By Month");
            string[] headers = { "Month", "Actual Check-ins", "Planned Seat-Days", "Locations" };
            for (int i = 0; i < headers.Length; i++)
            {
                StyleHeaderCell(ws.Cells[1, i + 1], BrandNavy);
                ws.Cells[1, i + 1].Value = headers[i];
            }

            for (int i = 0; i < rows.Count; i++)
            {
                ws.Cells[i + 2, 1].Value = rows[i].Item1;
                ws.Cells[i + 2, 2].Value = rows[i].Item2;
                ws.Cells[i + 2, 3].Value = rows[i].Item3;
                ws.Cells[i + 2, 4].Value = rows[i].Item4;
            }

            if (rows.Count > 0)
            {
                var chart = ws.Drawings.AddChart("chartByMonth", eChartType.ColumnClustered);
                chart.Title.Text = "Actual vs. Planned by Month";
                var actualSeries = chart.Series.Add(ws.Cells[2, 2, rows.Count + 1, 2], ws.Cells[2, 1, rows.Count + 1, 1]);
                actualSeries.Header = "Actual Check-ins";
                var plannedSeries = chart.Series.Add(ws.Cells[2, 3, rows.Count + 1, 3], ws.Cells[2, 1, rows.Count + 1, 1]);
                plannedSeries.Header = "Planned Seat-Days";
                chart.SetPosition(0, 0, 5, 0);
                chart.SetSize(480, 280);
            }

            ws.Column(1).Width = 14;
            ws.Column(2).Width = 16;
            ws.Column(3).Width = 18;
            ws.Column(4).Width = 40;
        }

        private void BuildByLocationSheet(ExcelPackage package, List<CheckInReportRow> rows, List<DateTime> allWeekStarts, List<SiteInfo> sites)
        {
            var ws = package.Workbook.Worksheets.Add("By Location");

            var distinctLocations = rows
                .Select(r => ResolvePlaceName(RoundLocation(r.GeoLocation)))
                .Distinct()
                .OrderBy(x => x)
                .ToList();

            var statusByLocation = rows
                .GroupBy(r => ResolvePlaceName(RoundLocation(r.GeoLocation)))
                .ToDictionary(g => g.Key, g => ClassifyLocation(RoundLocation(g.First().GeoLocation), sites));

            var countsByLocationWeek = rows
                .GroupBy(r => new { Loc = ResolvePlaceName(RoundLocation(r.GeoLocation)), Week = StartOfWeek(r.DateCreated) })
                .ToDictionary(g => g.Key, g => g.Count());

            int totalCol = 1 + allWeekStarts.Count + 1;
            int statusCol = totalCol + 1;

            ws.Cells[1, 1].Value = "Address";
            StyleHeaderCell(ws.Cells[1, 1], BrandNavy);
            for (int w = 0; w < allWeekStarts.Count; w++)
            {
                ws.Cells[1, 2 + w].Value = "Week of " + allWeekStarts[w].ToString("yyyy-MM-dd");
                StyleHeaderCell(ws.Cells[1, 2 + w], BrandNavy);
            }
            ws.Cells[1, totalCol].Value = "Total";
            StyleHeaderCell(ws.Cells[1, totalCol], BrandNavy);
            ws.Cells[1, statusCol].Value = "Status";
            StyleHeaderCell(ws.Cells[1, statusCol], BrandNavy);

            for (int i = 0; i < distinctLocations.Count; i++)
            {
                int r = i + 2;
                string loc = distinctLocations[i];
                ws.Cells[r, 1].Value = loc;

                int rowTotal = 0;
                for (int w = 0; w < allWeekStarts.Count; w++)
                {
                    var key = new { Loc = loc, Week = allWeekStarts[w] };
                    int count = countsByLocationWeek.ContainsKey(key) ? countsByLocationWeek[key] : 0;
                    ws.Cells[r, 2 + w].Value = count;
                    rowTotal += count;
                }
                ws.Cells[r, totalCol].Value = rowTotal;
                ws.Cells[r, totalCol].Style.Font.Bold = true;
                ws.Cells[r, statusCol].Value = statusByLocation.ContainsKey(loc) ? statusByLocation[loc] : "Unknown";
            }

            if (distinctLocations.Count > 0)
            {
                ws.Tables.Add(ws.Cells[1, 1, distinctLocations.Count + 1, statusCol], "ByLocationTable").TableStyle = TableStyles.Medium9;
            }

            ws.Column(1).Width = 45;
            for (int w = 0; w < allWeekStarts.Count; w++) ws.Column(2 + w).Width = 18;
            ws.Column(totalCol).Width = 10;
            ws.Column(statusCol).Width = 40;
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
        // Returns just the nearest configured site's name (regardless of
        // whether the check-in was actually within its radius) - used to
        // match a check-in's GPS location to the same "site" naming space
        // that Schedules.HostLocation uses, so Actual (GPS-derived) and
        // Expected (schedule-derived) counts can be compared per site.
        private static string GetNearestSiteName(string roundedCoordinateKey, List<SiteInfo> sites)
        {
            if (roundedCoordinateKey == "Unknown / location denied" || sites == null || sites.Count == 0)
            {
                return null;
            }

            var parts = roundedCoordinateKey.Split(',');
            double lat, lng;
            if (parts.Length != 2 ||
                !double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out lat) ||
                !double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out lng))
            {
                return null;
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

            return nearestSite?.SiteName;
        }

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
