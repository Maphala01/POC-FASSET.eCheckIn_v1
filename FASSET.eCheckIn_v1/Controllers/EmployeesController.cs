using System;
using System.Collections.Generic;
using System.Linq;
using System.Web.Mvc;
using FASSET.eCheckIn_v1.Data_Access_Layer;
using FASSET.eCheckIn_v1.Models;

namespace FASSET.eCheckIn_v1.Controllers
{
    public class EmployeesController : Controller
    {
        dal _dbAccess = new dal();

        // GET: Employees/Directory
        // Read-only staff directory - "FASSET Employees" in the sidebar.
        // Active employees only, matching the same convention used
        // elsewhere (e.g. GetEmployeesForDropdown) for "who's currently
        // a real, working employee" lists.
        public ActionResult Directory()
        {
            var all = _dbAccess.GetAllEmployeesDetailed();
            ViewBag.Employees = all.Where(e => e.IsActive).OrderBy(e => e.Name).ToList();
            return View();
        }

        // GET: Employees/Index
        // Admin CRUD grid - "Employee Management" in the sidebar. Includes
        // inactive employees too, since managing that status is the point.
        public ActionResult Index()
        {
            ViewBag.Employees = _dbAccess.GetAllEmployeesDetailed();
            ViewBag.Departments = _dbAccess.GetDepartmentNamesForReporting();
            return View();
        }

        // POST: Employees/Create
        [HttpPost]
        [ValidateAntiForgeryToken]
        public JsonResult Create(string name, string departmentName, string gender, string ethnicity, string occupationalLevel, string position)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return Json(new { success = false, message = "Name is required." });
            }

            try
            {
                int newId = _dbAccess.InsertEmployee(name, departmentName, gender, ethnicity, occupationalLevel, position);
                return Json(new { success = true, id = newId });
            }
            catch (Exception ex)
            {
                return Json(new { success = false, message = ex.Message });
            }
        }

        // POST: Employees/Update
        [HttpPost]
        [ValidateAntiForgeryToken]
        public JsonResult Update(int id, string name, string departmentName, string gender, string ethnicity, string occupationalLevel, string position)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return Json(new { success = false, message = "Name is required." });
            }

            try
            {
                bool updated = _dbAccess.UpdateEmployee(id, name, departmentName, gender, ethnicity, occupationalLevel, position);
                return Json(new { success = updated });
            }
            catch (Exception ex)
            {
                return Json(new { success = false, message = ex.Message });
            }
        }

        // POST: Employees/ToggleActive
        [HttpPost]
        [ValidateAntiForgeryToken]
        public JsonResult ToggleActive(int id, bool isActive)
        {
            try
            {
                bool updated = _dbAccess.SetEmployeeActive(id, isActive);
                return Json(new { success = updated });
            }
            catch (Exception ex)
            {
                return Json(new { success = false, message = ex.Message });
            }
        }

        // GET: Employees/History?name=...&year=...&month=...
        // One employee's monthly check-in history: KPI summary (total
        // check-ins, expected/scheduled days, days missed, on-time vs
        // late), the next upcoming scheduled date, and the month's raw
        // check-in list. Reuses the existing department-scoped DAL
        // methods (GetCheckInReportData / GetScheduledDays) rather than
        // adding new ones, filtering down to this one employee in C# -
        // neither currently supports filtering by a specific employee
        // name, and the data volumes here don't need a dedicated query.
        public ActionResult History(string name, int? year, int? month)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return RedirectToAction("Directory");
            }

            var today = DateTime.Today;
            int y = year ?? today.Year;
            int m = month ?? today.Month;
            var monthStart = new DateTime(y, m, 1);
            var monthEnd = monthStart.AddMonths(1).AddDays(-1);

            var monthCheckIns = _dbAccess.GetCheckInReportData(monthStart, monthEnd, null)
                .Where(r => r.EmployeeName == name)
                .OrderBy(r => r.DateCreated)
                .ToList();

            var monthScheduledDates = _dbAccess.GetScheduledDays(monthStart, monthEnd.AddDays(1), null)
                .Where(s => s.EmployeeName == name)
                .Select(s => s.ScheduleDate.Date)
                .Distinct()
                .OrderBy(d => d)
                .ToList();

            var checkedInDates = new HashSet<DateTime>(monthCheckIns.Select(r => r.DateCreated.Date));
            var missedDates = monthScheduledDates.Where(d => !checkedInDates.Contains(d)).ToList();

            TimeSpan lateThreshold = new TimeSpan(9, 0, 0);
            int onTimeCount = monthCheckIns.Count(r => r.DateCreated.TimeOfDay <= lateThreshold);
            int lateCount = monthCheckIns.Count - onTimeCount;

            // Looks up to 90 days ahead of TODAY (not the selected month)
            // so "next scheduled" still makes sense when browsing a past
            // month - it always answers "when will they next be in",
            // regardless of which month you're currently viewing.
            var upcomingScheduled = _dbAccess.GetScheduledDays(today, today.AddDays(90), null)
                .Where(s => s.EmployeeName == name && s.ScheduleDate.Date >= today)
                .OrderBy(s => s.ScheduleDate)
                .FirstOrDefault();

            ViewBag.EmployeeName = name;
            ViewBag.Year = y;
            ViewBag.Month = m;
            ViewBag.MonthLabel = monthStart.ToString("MMMM yyyy");
            ViewBag.TotalCheckIns = monthCheckIns.Count;
            ViewBag.ExpectedCheckIns = monthScheduledDates.Count;
            ViewBag.DaysMissed = missedDates.Count;
            ViewBag.OnTimeCount = onTimeCount;
            ViewBag.LateCount = lateCount;
            ViewBag.NextScheduled = upcomingScheduled != null
                ? upcomingScheduled.ScheduleDate.ToString("dddd, d MMMM yyyy")
                : "None scheduled in the next 90 days";
            ViewBag.CheckIns = monthCheckIns;
            ViewBag.MissedDates = missedDates.Select(d => d.ToString("dddd, d MMMM")).ToList();

            return View();
        }
    }
}
