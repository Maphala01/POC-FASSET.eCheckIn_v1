using System;
using System.Collections.Generic;
using System.Linq;
using System.Web.Mvc;
using FASSET.eCheckIn_v1.Data_Access_Layer;
using FASSET.eCheckIn_v1.Models;
using FASSET.eCheckIn_v1.Services;

namespace FASSET.eCheckIn_v1.Controllers
{
    // Everything here needs a login. Employee management (create, edit,
    // activate) is ICT (Admin) only; the directory and an employee's
    // history are available to every report role, limited to the
    // departments that user may see.
    [RoleAuthorize(Roles = AppRoles.AnyReportRole)]
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
            var access = AdminAccessService.Current(HttpContext);
            var all = _dbAccess.GetAllEmployeesDetailed();
            var visible = all.Where(e => e.IsActive);
            if (!access.SeesAllDepartments)
            {
                var departments = _dbAccess.GetEmployeeReportingDepartments();
                visible = visible.Where(e => departments.ContainsKey(e.Id) && access.CanSeeDepartment(departments[e.Id]));
            }
            ViewBag.Employees = visible.OrderBy(e => e.Name).ToList();
            return View();
        }

        // GET: Employees/Index
        // Admin CRUD grid - "Employee Management" in the sidebar. Includes
        // inactive employees too, since managing that status is the point.
        [RoleAuthorize(Roles = AppRoles.Admin)]
        public ActionResult Index()
        {
            ViewBag.Employees = _dbAccess.GetAllEmployeesDetailed();
            ViewBag.Departments = _dbAccess.GetDepartmentNamesForReporting();
            return View();
        }

        // POST: Employees/Create
        [RoleAuthorize(Roles = AppRoles.Admin)]
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
        [RoleAuthorize(Roles = AppRoles.Admin)]
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
        [RoleAuthorize(Roles = AppRoles.Admin)]
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

            // Role scoping (A1): a Manager/LLC user may only open the
            // history of someone in a department they can see. Names can be
            // duplicated in Employees, so any matching record in scope counts.
            var access = AdminAccessService.Current(HttpContext);
            if (!access.SeesAllDepartments)
            {
                var departments = _dbAccess.GetEmployeeReportingDepartments();
                bool inScope = _dbAccess.GetAllEmployeesDetailed()
                    .Where(e => e.Name == name)
                    .Any(e => departments.ContainsKey(e.Id) && access.CanSeeDepartment(departments[e.Id]));
                if (!inScope)
                {
                    Response.StatusCode = 403;
                    return View("~/Views/Shared/AccessDenied.cshtml");
                }
            }

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

            // A3: missed days come from actual weekly presence, not exact
            // dates, so a swapped day isn't counted as missed. See
            // AttendanceRules.Calculate.
            var weekly = AttendanceRules.Calculate(monthScheduledDates, monthCheckIns.Select(r => r.DateCreated));

            // A4: configured late cut-off (Web.config LateCutoff).
            TimeSpan lateThreshold = AttendanceRules.LateCutoff;
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
            ViewBag.DaysMissed = weekly.MissedDays;
            ViewBag.OnTimeCount = onTimeCount;
            ViewBag.LateCount = lateCount;
            ViewBag.NextScheduled = upcomingScheduled != null
                ? upcomingScheduled.ScheduleDate.ToString("dddd, d MMMM yyyy")
                : "None scheduled in the next 90 days";
            ViewBag.CheckIns = monthCheckIns;
            ViewBag.LateCutoff = AttendanceRules.LateCutoffLabel;
            // One line per week they came in fewer days than scheduled.
            ViewBag.MissedDates = weekly.WeeksShort
                .Select(w => w.Label + ": in " + w.PresentDays + " of " + w.RequiredDays + " scheduled day" + (w.RequiredDays == 1 ? "" : "s"))
                .ToList();

            return View();
        }
    }
}
