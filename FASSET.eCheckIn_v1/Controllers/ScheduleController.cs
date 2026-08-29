using System;
using System.Collections.Generic;
using System.Linq;
using System.Web;
using System.Web.Mvc;
using FASSET.eCheckIn_v1.Data_Access_Layer;
using FASSET.eCheckIn_v1.Models;
using FASSET.eCheckIn_v1.Services;
using System.Web.Script.Serialization;

namespace FASSET.eCheckIn_v1.Controllers
{
    [NoCache]
    public class ScheduleController : Controller
    {
        private readonly dal _dal = new dal();

        // GET: Schedule/Import
        public ActionResult Import()
        {
            return View(new ScheduleImportViewModel());
        }

        // POST: Schedule/Import
        // Expects paired form fields: hostLocations[] and scheduleFiles[]
        [HttpPost]
        [ValidateAntiForgeryToken]
        public ActionResult Import(string[] hostLocations, HttpPostedFileBase[] scheduleFiles)
        {
            var model = new ScheduleImportViewModel();

            if (hostLocations == null || scheduleFiles == null || hostLocations.Length != scheduleFiles.Length)
            {
                ModelState.AddModelError("", "Each file must have a matching site name.");
                return View(model);
            }

            for (int i = 0; i < scheduleFiles.Length; i++)
            {
                var file = scheduleFiles[i];
                var hostLocation = hostLocations[i]?.Trim();

                if (file == null || file.ContentLength == 0 || string.IsNullOrWhiteSpace(hostLocation))
                    continue;

                var result = new ScheduleImportResultRow
                {
                    HostLocation = hostLocation,
                    FileName = file.FileName,
                };

                try
                {
                    var parsedRows = ScheduleCalendarParser.Parse(file.InputStream, hostLocation);

                    var scheduleModels = new List<ScheduleModel>();
                    var unmatched = new HashSet<string>();

                    foreach (var row in parsedRows)
                    {
                        int? employeeId = null;
                        if (!row.IsPublicHoliday && !row.IsVacant && !string.IsNullOrEmpty(row.EmployeeNameRaw))
                        {
                            employeeId = _dal.ResolveEmployeeIdByName(row.EmployeeNameRaw);
                            if (employeeId == null)
                                unmatched.Add(row.EmployeeNameRaw);
                        }

                        scheduleModels.Add(new ScheduleModel
                        {
                            HostLocation = row.HostLocation,
                            EmployeeId = employeeId,
                            EmployeeNameRaw = row.EmployeeNameRaw ?? (row.IsVacant ? "Vacant Seat" : "Public Holiday"),
                            ScheduleDate = row.ScheduleDate,
                            SeatSlot = row.SeatSlot,
                            IsVacant = row.IsVacant,
                            IsPublicHoliday = row.IsPublicHoliday,
                            CreatedBy = User?.Identity?.Name ?? "Import",
                        });
                    }

                    _dal.BulkInsertSchedules(scheduleModels);

                    result.RowsInserted = scheduleModels.Count;
                    result.VacantSeats = scheduleModels.Count(r => r.IsVacant);
                    result.PublicHolidayDays = scheduleModels
                        .Where(r => r.IsPublicHoliday)
                        .Select(r => r.ScheduleDate)
                        .Distinct()
                        .Count();
                    result.UnmatchedNames = unmatched.OrderBy(n => n).ToList();
                }
                catch (Exception ex)
                {
                    result.Error = ex.ToString();
                }

                model.Results.Add(result);
            }

            return View(model);
        }


        // GET: Schedule/Calendar
        public ActionResult Calendar(string hostLocation, int? year, int? month)
        {
            var today = DateTime.Today;
            int y = year ?? today.Year;
            int m = month ?? today.Month;

            var hostLocations = _dal.GetDistinctHostLocations();
            string selectedHost = hostLocation;
            if (string.IsNullOrEmpty(selectedHost) && hostLocations.Count > 0)
                selectedHost = hostLocations[0];

            var entries = _dal.GetScheduleForMonth(y, m, selectedHost);

            var model = new ScheduleCalendarViewModel
            {
                SelectedHostLocation = selectedHost,
                Year = y,
                Month = m,
                HostLocations = hostLocations,
                Employees = _dal.GetEmployeesForDropdown(),
            };

            foreach (var e in entries)
            {
                var dateKey = e.ScheduleDate.Date;
                if (!model.EntriesByDate.ContainsKey(dateKey))
                    model.EntriesByDate[dateKey] = new List<ScheduleModel>();
                model.EntriesByDate[dateKey].Add(e);
            }

            return View(model);
        }

        // GET: Schedule/Edit — the drag-and-drop assignment screen
        [Authorize]
        public ActionResult Edit(string hostLocation)
        {
            var hostLocations = _dal.GetDistinctHostLocations();
            string selectedHost = hostLocation;
            if (string.IsNullOrEmpty(selectedHost) && hostLocations.Count > 0)
                selectedHost = hostLocations[0];

            var model = new ScheduleCalendarViewModel
            {
                SelectedHostLocation = selectedHost,
                HostLocations = hostLocations,
                Employees = _dal.GetEmployeesForDropdown(),
            };

            return View(model);
        }

        // POST: Schedule/Edit
        [Authorize]
        [HttpPost]
        [ValidateAntiForgeryToken]
        public ActionResult Edit(ScheduleEditViewModel vm)
        {
            var entry = vm.Entry;

            // Mirror the same defaulting logic Import uses, so manually
            // added/edited entries behave identically to imported ones.
            if (entry.IsVacant)
            {
                entry.EmployeeNameRaw = "Vacant Seat";
                entry.EmployeeId = null;
            }
            else if (entry.IsPublicHoliday)
            {
                entry.EmployeeNameRaw = "Public Holiday";
                entry.EmployeeId = null;
                entry.SeatSlot = null;
            }
            else if (entry.EmployeeId.HasValue)
            {
                var match = _dal.GetEmployeesForDropdown().FirstOrDefault(e => e.Id == entry.EmployeeId.Value);
                entry.EmployeeNameRaw = match?.Name ?? entry.EmployeeNameRaw;
            }

            entry.CreatedBy = User?.Identity?.Name ?? "Admin";

            if (entry.Id > 0)
                _dal.UpdateSchedule(entry);
            else
                _dal.InsertSchedule(entry);

            return RedirectToAction("Calendar", new { hostLocation = vm.ReturnHostLocation, year = vm.ReturnYear, month = vm.ReturnMonth });
        }

        // POST: Schedule/Delete
        [Authorize]
        [HttpPost]
        [ValidateAntiForgeryToken]
        public ActionResult Delete(int id, string hostLocation, int year, int month)
        {
            _dal.DeleteSchedule(id);
            return RedirectToAction("Calendar", new { hostLocation, year, month });
        }

        // GET: Schedule/CalendarEvents — JSON feed FullCalendar fetches
        // whenever the visible month (or site) changes.
        [HttpGet]
        public JsonResult CalendarEvents(string hostLocation, DateTime start, DateTime end)
        {
            var rows = _dal.GetScheduleForDateRange(start, end, hostLocation);

            var events = rows.Select(r => new
            {
                id = r.Id,
                title = r.IsPublicHoliday
                    ? "Public Holiday"
                    : (r.IsVacant ? ("S" + r.SeatSlot + ": Vacant") : ("S" + r.SeatSlot + ": " + r.EmployeeNameRaw)),
                start = r.ScheduleDate.ToString("yyyy-MM-dd"),
                allDay = true,
                backgroundColor = r.IsPublicHoliday ? "#FEF9C3" : (r.IsVacant ? "#E0F2FE" : "#F1F5F9"),
                borderColor = r.IsPublicHoliday ? "#FEF9C3" : (r.IsVacant ? "#E0F2FE" : "#F1F5F9"),
                textColor = r.IsPublicHoliday ? "#854D0E" : (r.IsVacant ? "#0369A1" : "#1E293B"),
            });

            return Json(events.ToList(), JsonRequestBehavior.AllowGet);
        }

        // POST: Schedule/QuickAssign — called when an employee chip is
        // dragged from the Employees panel and dropped onto a calendar day.
        [Authorize]
        [HttpPost]
        [ValidateAntiForgeryToken]
        public JsonResult QuickAssign(int employeeId, string hostLocation, DateTime date)
        {
            if (string.IsNullOrWhiteSpace(hostLocation))
                return Json(new { success = false, message = "No site selected." });

            var employee = _dal.GetEmployeesForDropdown().FirstOrDefault(e => e.Id == employeeId);
            if (employee == null)
                return Json(new { success = false, message = "Employee not found." });

            int nextSeat = _dal.GetNextAvailableSeatSlot(hostLocation, date.Date);

            var model = new ScheduleModel
            {
                HostLocation = hostLocation,
                EmployeeId = employee.Id,
                EmployeeNameRaw = employee.Name,
                ScheduleDate = date.Date,
                SeatSlot = nextSeat,
                IsVacant = false,
                IsPublicHoliday = false,
                CreatedBy = User?.Identity?.Name ?? "Admin",
            };

            var newId = _dal.InsertSchedule(model);
            return Json(new { success = true, id = newId, title = employee.Name });
        }

        // GET: Schedule/EntryDetails — feeds the click-to-edit popup
        [Authorize]
        [HttpGet]
        public JsonResult EntryDetails(int id)
        {
            var entry = _dal.GetScheduleById(id);
            if (entry == null)
                return Json(new { success = false, message = "Entry not found." }, JsonRequestBehavior.AllowGet);

            return Json(new
            {
                success = true,
                id = entry.Id,
                employeeName = entry.EmployeeNameRaw,
                seatSlot = entry.SeatSlot,
                isVacant = entry.IsVacant,
                isPublicHoliday = entry.IsPublicHoliday,
                hostLocation = entry.HostLocation,
                date = entry.ScheduleDate.ToString("yyyy-MM-dd"),
            }, JsonRequestBehavior.AllowGet);
        }

        // POST: Schedule/UpdateEntryQuick — Save button in the popup
        [Authorize]
        [HttpPost]
        [ValidateAntiForgeryToken]
        public JsonResult UpdateEntryQuick(int id, int? seatSlot, bool isVacant)
        {
            var entry = _dal.GetScheduleById(id);
            if (entry == null)
                return Json(new { success = false, message = "Entry not found." });

            entry.SeatSlot = seatSlot;
            entry.IsVacant = isVacant;
            if (isVacant)
            {
                entry.EmployeeId = null;
                entry.EmployeeNameRaw = "Vacant Seat";
            }

            _dal.UpdateSchedule(entry);
            return Json(new { success = true });
        }

        // POST: Schedule/DeleteEntryQuick — Delete button in the popup
        [Authorize]
        [HttpPost]
        [ValidateAntiForgeryToken]
        public JsonResult DeleteEntryQuick(int id)
        {
            _dal.DeleteSchedule(id);
            return Json(new { success = true });
        }


        // GET: Schedule/DayRoster — feeds the "manage this day" modal
        [Authorize]
        [HttpGet]
        public JsonResult DayRoster(string hostLocation, DateTime date)
        {
            var entries = _dal.GetScheduleForDateRange(date.Date, date.Date.AddDays(1), hostLocation)
                .Where(e => !e.IsPublicHoliday) // Public Holiday rows aren't managed through this editor
                .Select(e => new
                {
                    id = e.Id,
                    employeeId = e.EmployeeId,
                    employeeName = e.EmployeeNameRaw,
                    isVacant = e.IsVacant,
                });

            return Json(new { success = true, entries }, JsonRequestBehavior.AllowGet);
        }

        // POST: Schedule/SaveDayRoster — Update button in the modal.
        // Treats the submitted list as the full target state for that
        // site+day: anything previously there but missing from the list
        // gets deleted, existing rows get updated, id=0 rows get inserted
        // (with an auto-assigned seat number).
        [Authorize]
        [HttpPost]
        [ValidateAntiForgeryToken]
        public JsonResult SaveDayRoster(string hostLocation, DateTime date, string rowsJson)
        {
            if (string.IsNullOrWhiteSpace(hostLocation))
                return Json(new { success = false, message = "No site selected." });

            var serializer = new JavaScriptSerializer();
            var rows = serializer.Deserialize<List<DayRosterRowInput>>(rowsJson) ?? new List<DayRosterRowInput>();

            var existing = _dal.GetScheduleForDateRange(date.Date, date.Date.AddDays(1), hostLocation)
                .Where(e => !e.IsPublicHoliday)
                .ToList();

            var submittedIds = new HashSet<int>(rows.Where(r => r.Id > 0).Select(r => r.Id));

            // Delete anything that existed before but isn't in the submitted list anymore
            foreach (var ex in existing)
            {
                if (!submittedIds.Contains(ex.Id))
                    _dal.DeleteSchedule(ex.Id);
            }

            var employees = _dal.GetEmployeesForDropdown();

            foreach (var row in rows)
            {
                string empName = row.IsVacant
                    ? "Vacant Seat"
                    : (employees.FirstOrDefault(e => e.Id == row.EmployeeId)?.Name ?? "Unknown");

                if (row.Id > 0)
                {
                    var current = existing.FirstOrDefault(e => e.Id == row.Id);
                    if (current != null)
                    {
                        current.EmployeeId = row.IsVacant ? (int?)null : row.EmployeeId;
                        current.EmployeeNameRaw = empName;
                        current.IsVacant = row.IsVacant;
                        _dal.UpdateSchedule(current);
                    }
                }
                else
                {
                    int nextSeat = _dal.GetNextAvailableSeatSlot(hostLocation, date.Date);
                    _dal.InsertSchedule(new ScheduleModel
                    {
                        HostLocation = hostLocation,
                        EmployeeId = row.IsVacant ? (int?)null : row.EmployeeId,
                        EmployeeNameRaw = empName,
                        ScheduleDate = date.Date,
                        SeatSlot = nextSeat,
                        IsVacant = row.IsVacant,
                        IsPublicHoliday = false,
                        CreatedBy = User?.Identity?.Name ?? "Admin",
                    });
                }
            }

            return Json(new { success = true });
        }

        // POST: Schedule/SwapEntries — drag Person X onto Person Y in the
        // two-panel modal. Trades both their date AND seat slot, so neither
        // side can ever collide with what was already sitting in that spot.
        [Authorize]
        [HttpPost]
        [ValidateAntiForgeryToken]
        public JsonResult SwapEntries(int id1, int id2)
        {
            var e1 = _dal.GetScheduleById(id1);
            var e2 = _dal.GetScheduleById(id2);
            if (e1 == null || e2 == null)
                return Json(new { success = false, message = "One of those entries no longer exists." });

            var date1 = e1.ScheduleDate;
            var seat1 = e1.SeatSlot;
            var date2 = e2.ScheduleDate;
            var seat2 = e2.SeatSlot;

            e1.ScheduleDate = date2;
            e1.SeatSlot = seat2;
            e2.ScheduleDate = date1;
            e2.SeatSlot = seat1;

            _dal.UpdateSchedule(e1);
            _dal.UpdateSchedule(e2);

            return Json(new { success = true });
        }

        // GET: Schedule/EmployeesForSite — site-scoped employee list for
        // the "add employee" dropdown in the Edit modal.
        [Authorize]
        [HttpGet]
        public JsonResult EmployeesForSite(string hostLocation)
        {
            var employees = _dal.GetEmployeesForHostLocation(hostLocation)
                .Select(e => new { id = e.Id, name = e.Name });

            return Json(employees.ToList(), JsonRequestBehavior.AllowGet);
        }
       
    }
}