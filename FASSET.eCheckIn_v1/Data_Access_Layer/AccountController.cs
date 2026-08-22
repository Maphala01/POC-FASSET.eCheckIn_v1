using System.Web.Mvc;
using System.Web.Security;
using FASSET.eCheckIn_v1.Data_Access_Layer;
using FASSET.eCheckIn_v1.Services;

namespace FASSET.eCheckIn_v1.Controllers
{
    public class AccountController : Controller
    {
        private readonly dal _dal = new dal();

        // GET: Account/Login — the page ASP.NET redirects to when someone
        // hits a protected URL directly without being logged in.
        public ActionResult Login(string returnUrl)
        {
            ViewBag.ReturnUrl = returnUrl;
            return View();
        }

        // POST: Account/Login — handles BOTH the full-page form submit and
        // the AJAX call from the Calendar page's login modal.
        [HttpPost]
        [ValidateAntiForgeryToken]
        public ActionResult Login(string username, string password, string returnUrl)
        {
            bool valid = false;
            string message = "Invalid username or password.";

            if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
            {
                message = "Username and password are required.";
            }
            else
            {
                var storedHash = _dal.GetAdminPasswordHash(username);
                if (storedHash != null && PasswordHasher.VerifyPassword(password, storedHash))
                {
                    valid = true;
                    FormsAuthentication.SetAuthCookie(username, false);
                }
            }

            string redirectUrl = Url.IsLocalUrl(returnUrl) ? returnUrl : Url.Action("Edit", "Schedule");

            if (Request.IsAjaxRequest())
            {
                return Json(new { success = valid, message = valid ? null : message, redirectUrl = valid ? redirectUrl : null });
            }

            if (valid)
                return Redirect(redirectUrl);

            ViewBag.ErrorMessage = message;
            ViewBag.ReturnUrl = returnUrl;
            return View();
        }

        [HttpPost]
        public ActionResult Logout()
        {
            FormsAuthentication.SignOut();
            return RedirectToAction("Calendar", "Schedule");
        }


        // GET: Account/CreateAdmin
        [Authorize]
        public ActionResult CreateAdmin()
        {
            return View();
        }

        // POST: Account/CreateAdmin
        [Authorize]
        [HttpPost]
        [ValidateAntiForgeryToken]
        public ActionResult CreateAdmin(string username, string password, string confirmPassword)
        {
            if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
            {
                ViewBag.ErrorMessage = "Username and password are required.";
                return View();
            }

            if (password != confirmPassword)
            {
                ViewBag.ErrorMessage = "Passwords do not match.";
                return View();
            }

            if (_dal.AdminUsernameExists(username))
            {
                ViewBag.ErrorMessage = "That username is already taken.";
                return View();
            }

            var hash = PasswordHasher.HashPassword(password);
            _dal.InsertAdmin(username, hash);

            ViewBag.SuccessMessage = "Admin '" + username + "' created successfully.";
            return View();
        }
    }
}