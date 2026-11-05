using System.Collections.Generic;
using System.Linq;
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
            AdminAccess access = null;

            if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
            {
                message = "Username and password are required.";
            }
            else
            {
                var storedHash = _dal.GetAdminPasswordHash(username);
                if (storedHash != null && PasswordHasher.VerifyPassword(password, storedHash))
                {
                    AdminAccessService.Invalidate(username);
                    access = AdminAccessService.Get(username);
                    if (access == null || access.Role == null)
                    {
                        // Correct password but no valid role in dbo.Admins:
                        // don't log them in to an account that can't see anything.
                        message = "Your account has no access role yet. Please contact ICT.";
                    }
                    else
                    {
                        valid = true;
                        FormsAuthentication.SetAuthCookie(username, false);
                    }
                }
            }

            // Admins land on schedule editing as before; everyone else on
            // the reports they're allowed to see.
            string defaultUrl = access != null && access.IsAdmin
                ? Url.Action("Edit", "Schedule")
                : Url.Action("Index", "Reports");
            string redirectUrl = Url.IsLocalUrl(returnUrl) ? returnUrl : defaultUrl;

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
            if (User != null && User.Identity.IsAuthenticated)
                AdminAccessService.Invalidate(User.Identity.Name);
            FormsAuthentication.SignOut();
            return RedirectToAction("Calendar", "Schedule");
        }


        // GET: Account/CreateAdmin — Admin (ICT) only.
        [RoleAuthorize(Roles = AppRoles.Admin)]
        public ActionResult CreateAdmin()
        {
            PopulateCreateAdminLists();
            return View();
        }

        // POST: Account/CreateAdmin
        [RoleAuthorize(Roles = AppRoles.Admin)]
        [HttpPost]
        [ValidateAntiForgeryToken]
        public ActionResult CreateAdmin(string username, string password, string confirmPassword, string role, int[] departmentIds)
        {
            PopulateCreateAdminLists();
            username = (username ?? "").Trim();
            string normalizedRole = AppRoles.Normalize(role);
            var departments = (departmentIds ?? new int[0]).Distinct().ToList();

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

            if (normalizedRole == null)
            {
                ViewBag.ErrorMessage = "Choose a role.";
                return View();
            }

            bool needsDepartments = normalizedRole == AppRoles.Manager || normalizedRole == AppRoles.Llc;
            if (needsDepartments && departments.Count == 0)
            {
                ViewBag.ErrorMessage = "Managers and LLC members need at least one department.";
                return View();
            }

            if (_dal.AdminUsernameExists(username))
            {
                ViewBag.ErrorMessage = "That username is already taken.";
                return View();
            }

            var hash = PasswordHasher.HashPassword(password);
            // Admin and Executive see every department, so any ticked
            // departments are ignored for them rather than stored.
            _dal.InsertAdmin(username, hash, normalizedRole, needsDepartments ? departments : new List<int>());
            AdminAccessService.Invalidate(username);

            ViewBag.SuccessMessage = "User '" + username + "' created as " + normalizedRole + ".";
            return View();
        }

        private void PopulateCreateAdminLists()
        {
            ViewBag.Roles = AppRoles.All;
            ViewBag.DepartmentOptions = _dal.GetDepartmentIdNames();
        }
    }
}
