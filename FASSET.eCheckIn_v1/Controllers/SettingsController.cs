using System;
using System.Web.Mvc;
using FASSET.eCheckIn_v1.Data_Access_Layer;
using FASSET.eCheckIn_v1.Services;

namespace FASSET.eCheckIn_v1.Controllers
{
    public class SettingsController : Controller
    {
        dal _dbAccess = new dal();

        // GET: Settings
        public ActionResult Index()
        {
            ViewBag.Sites = _dbAccess.GetSites();
            return View();
        }

        // POST: Settings/CreateSite
        [HttpPost]
        [ValidateAntiForgeryToken]
        public JsonResult CreateSite(string siteName, double latitude, double longitude, int radiusMeters)
        {
            if (string.IsNullOrWhiteSpace(siteName))
            {
                return Json(new { success = false, message = "Site name is required." });
            }

            try
            {
                int newId = _dbAccess.InsertSite(siteName, latitude, longitude, radiusMeters);
                return Json(new { success = true, id = newId });
            }
            catch (Exception ex)
            {
                return Json(new { success = false, message = ex.Message });
            }
        }

        // POST: Settings/UpdateSite
        [HttpPost]
        [ValidateAntiForgeryToken]
        public JsonResult UpdateSite(int id, string siteName, double latitude, double longitude, int radiusMeters)
        {
            if (string.IsNullOrWhiteSpace(siteName))
            {
                return Json(new { success = false, message = "Site name is required." });
            }

            try
            {
                bool updated = _dbAccess.UpdateSite(id, siteName, latitude, longitude, radiusMeters);
                return Json(new { success = updated });
            }
            catch (Exception ex)
            {
                return Json(new { success = false, message = ex.Message });
            }
        }

        // POST: Settings/DeleteSite
        [HttpPost]
        [ValidateAntiForgeryToken]
        public JsonResult DeleteSite(int id)
        {
            try
            {
                bool deleted = _dbAccess.DeleteSite(id);
                return Json(new { success = deleted });
            }
            catch (Exception ex)
            {
                return Json(new { success = false, message = ex.Message });
            }
        }

        // GET: Settings/OfflineStatus
        // Counts for the "Offline check-ins" card on the Settings page.
        [HttpGet]
        public JsonResult OfflineStatus()
        {
            return Json(OfflineCheckInQueue.GetStatus(), JsonRequestBehavior.AllowGet);
        }

        // POST: Settings/SyncOffline
        // "Sync now" - writes any queued offline check-ins to the database.
        [HttpPost]
        [ValidateAntiForgeryToken]
        public JsonResult SyncOffline()
        {
            return Json(OfflineSyncService.SyncPending());
        }
    }
}
