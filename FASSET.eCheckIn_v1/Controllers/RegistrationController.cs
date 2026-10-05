using System;
using System.Collections.Generic;
using System.Linq;
using System.Web;
using System.Web.Mvc;
using FASSET.eCheckIn_v1.Models;
using FASSET.eCheckIn_v1.Services;

namespace FASSET.eCheckIn_v1.Controllers
{
    public class RegistrationController : Controller
    {
        Data_Access_Layer.dal _dbAccess = new Data_Access_Layer.dal();

        public RegistrationController()
        {
            _dbAccess = new Data_Access_Layer.dal();
        }

        // GET: Registration
        public ActionResult Index()
        {
            // Loads from the database; if the database is unreachable, falls
            // back to the last good snapshot so the form still opens and a
            // check-in can still be captured to the offline queue.
            var departments = FromDbOrCache(
                () => _dbAccess.GetDepartments(),
                () =>
                {
                    List<Department> cached;
                    return ReferenceDataCache.TryGetDepartments(out cached) ? cached : null;
                },
                null);

            if (departments == null)
            {
                // Database down AND no saved snapshot yet - show the form with
                // a clear message instead of an error screen.
                ViewBag.Departments = new List<Department>();
                ViewBag.Message = "Check-in is temporarily unavailable. Please try again in a few minutes - if this keeps happening, let ICT know.";
                ViewBag.MessageType = "error";
            }
            else
            {
                ViewBag.Departments = departments;
            }

            ReferenceDataCache.RefreshInBackgroundIfStale();
            return View();
        }

        [HttpGet]
        public JsonResult GetDepartments(string term)
        {
            var departments = FromDbOrCache(
                () => _dbAccess.GetDepartmentsByTerm(term),
                () => ReferenceDataCache.SearchDepartments(term),
                new List<Department>());
            return Json(departments, JsonRequestBehavior.AllowGet);
        }

        [HttpGet]
        public JsonResult GetEmployees(string term, string department)
        {
            var employees = FromDbOrCache(
                () => _dbAccess.GetEmployeesByTerm(term, department),
                () => ReferenceDataCache.SearchEmployees(term, department),
                new List<Employee>());
            return Json(employees, JsonRequestBehavior.AllowGet);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public ActionResult SubmitRegistration(RegistrationModel model)
        {
            model.GeoLocation = Request.Form["GeoLocation"];
            model.QRCodeTotp = Request.Form["QRCodeTotp"];
            model.userTotp = Request.Form["userTotp"];
            model.Employee = Request.Form["Employee"];
            model.WorkLocation = Request.Form["WorkLocation"];

            // The Registration form's "Pin Me" flow disables Submit until
            // geolocation succeeds - but that's a client-side restriction
            // only (HTML/JS), which anything hitting this endpoint
            // directly (a stale cached page, a replayed request, etc.)
            // simply doesn't have to go through. This is the actual
            // enforcement: reject here, server-side, if GeoLocation is
            // missing or the "0,0" placeholder the front-end uses when the
            // browser's geolocation prompt was denied/unavailable - rather
            // than saving a check-in that will show up in Reports as
            // "Unknown / location denied" with no way to tell whether that
            // was a genuine denial or a bypassed submission.
            if (string.IsNullOrWhiteSpace(model.GeoLocation) || model.GeoLocation == "0,0")
            {
                ViewBag.Message = "Location is required to check in. Please tap 'Pin Me' and try again.";
                ViewBag.MessageType = "error";

                PopulateFormLists(model);
                return View("Index", model);
            }

            QRCodeModel qrCodeModel = new QRCodeModel();
            model.qrCodeImgUrl = qrCodeModel.GetQRCodeContent(model.GeoLocation);

            int res;
            try
            {
                // Returns 99 (written), one of the existing rejection codes, or
                // OfflineQueuedCode when the database was unreachable and the
                // check-in was safely saved to the offline queue instead.
                res = _dbAccess.SaveRegistration(model);
            }
            catch (Exception ex)
            {
                // Anything unexpected (a dropped SQL connection, a data
                // conversion error, etc.) used to bubble all the way up to
                // ASP.NET's raw yellow error page - which exposed internal
                // file paths and stack traces to whoever was standing at
                // the kiosk trying to check in. Now it becomes the same
                // friendly on-form message as every other error case,
                // while the real exception is logged into the ErrorLog
                // table for ICT to actually check later.
                try
                {
                    _dbAccess.LogApplicationError(
                        "Unhandled exception in RegistrationController.SubmitRegistration",
                        ex.ToString(),
                        model.Employee);
                }
                catch
                {
                    // If logging itself fails (e.g. the very same database
                    // outage caused both the original error AND this one),
                    // fall back to Debug output rather than letting a
                    // failed log write mask the real problem or crash the
                    // request a second time.
                    System.Diagnostics.Debug.WriteLine("SubmitRegistration unhandled exception (and logging to ErrorLog also failed): " + ex);
                }

                ViewBag.Message = "Something went wrong while checking you in. Please try again - if this keeps happening, let ICT know.";
                ViewBag.MessageType = "error";

                PopulateFormLists(model);
                return View("Index", model);
            }

            if (res == 99 || res == Data_Access_Layer.dal.OfflineQueuedCode)
            {
                // Success — hand off to the dedicated confirmation page instead
                // of redirecting back to Index (TempData set here was previously
                // never being read back out on Index, so the message just vanished).
                TempData["CheckedInName"] = model.Employee;

                if (res == Data_Access_Layer.dal.OfflineQueuedCode)
                {
                    // Recorded locally; will be written to the database automatically.
                    TempData["CheckedInOffline"] = true;
                }
                else
                {
                    // The database just worked - a good moment to drain anything queued earlier.
                    OfflineSyncService.TriggerIfPending();
                }
                return RedirectToAction("CheckedIn");
            }
            else if (res == 0)
            {
                ViewBag.Message = "You are checked in already!";
                ViewBag.MessageType = "error";
            }
            else if (res == -1)
            {
                // mst_spCheckInEmployee returns -1 for a missing DEPARTMENT and -2 for a
                // missing USER (these two messages used to be the other way round).
                ViewBag.Message = "Department does not exist!";
                ViewBag.MessageType = "error";
            }
            else if (res == -2)
            {
                ViewBag.Message = "User does not exist!";
                ViewBag.MessageType = "error";
            }
            else if (res == -3)
            {
                ViewBag.Message = "Your checkIn was unsuccessful..contact ICT!";
                ViewBag.MessageType = "error";
            }
            else
            {
                // Unexpected result code from mst_spCheckInEmployee — surface it
                // instead of silently falling through to a blank form.
                ViewBag.Message = "Unexpected result from check-in (code: " + res + "). Please tell ICT this code.";
                ViewBag.MessageType = "error";
            }

            PopulateFormLists(model);
            return View("Index", model);
        }

        // GET: Registration/CheckedIn
        public ActionResult CheckedIn()
        {
            var name = TempData["CheckedInName"] as string;
            if (string.IsNullOrEmpty(name))
            {
                // Someone navigated here directly without just checking in —
                // send them back to the form instead of showing a blank confirmation.
                return RedirectToAction("Index");
            }

            ViewBag.CheckedInName = name;
            // True when the check-in was saved to the offline queue rather than the database.
            ViewBag.CheckedInOffline = TempData["CheckedInOffline"] != null;
            return View();
        }

        // Reloads the department/employee lists the form needs when it is
        // redisplayed after an error - from the database if possible, else
        // from the offline snapshot, else empty (never throws just because
        // the database is down).
        private void PopulateFormLists(RegistrationModel model)
        {
            try
            {
                model.DepartmentList = _dbAccess.GetDepartments();
                model.EmployeeList = _dbAccess.GetEmployees();
            }
            catch (Exception ex) when (OfflineCheckInQueue.IsConnectivityFailure(ex))
            {
                List<Department> cachedDepartments;
                model.DepartmentList = ReferenceDataCache.TryGetDepartments(out cachedDepartments)
                    ? cachedDepartments
                    : new List<Department>();
                model.EmployeeList = new List<Employee>();
            }

            ViewBag.Departments = model.DepartmentList;
            ViewBag.Employees = model.EmployeeList;
        }

        // Runs a database read; if the database is unreachable, serves the
        // snapshot instead (and remembers the outage briefly so the next
        // requests don't each wait out a connect timeout). If there is no
        // snapshot either, returns whenUnavailable instead of throwing, so
        // the page can show a friendly message rather than an error screen.
        private static T FromDbOrCache<T>(Func<T> fromDb, Func<T> fromCache, T whenUnavailable) where T : class
        {
            if (OfflineCheckInQueue.IsCircuitOpen)
            {
                T early = fromCache();
                if (early != null) { return early; }
            }

            try
            {
                T result = fromDb();
                OfflineCheckInQueue.CloseCircuit();
                return result;
            }
            catch (Exception ex) when (OfflineCheckInQueue.IsConnectivityFailure(ex))
            {
                OfflineCheckInQueue.OpenCircuit();
                T cached = fromCache();
                return cached ?? whenUnavailable;
            }
        }
    }
}
