using System;
using System.Collections.Generic;
using System.Linq;
using System.Web;
using System.Web.Caching;
using System.Web.Mvc;
using FASSET.eCheckIn_v1.Data_Access_Layer;

namespace FASSET.eCheckIn_v1.Services
{
    // Who a logged-in user is for reporting purposes (decision D2):
    //   Admin     - ICT. Everything: reports for all departments, schedule
    //               edits, employee management, settings, creating users.
    //   Executive - CEO's office. Reports for every department, read-only.
    //   LLC       - LLC members. Reports for the departments assigned to
    //               them in dbo.AdminDepartmentScope, read-only.
    //   Manager   - Reports for their own department(s) only, also from
    //               dbo.AdminDepartmentScope (decision D1), read-only.
    //
    // Department scoping is enforced on the SERVER (ReportsController,
    // EmployeesController) - hiding links in the UI is only cosmetic.
    public static class AppRoles
    {
        public const string Admin = "Admin";
        public const string Executive = "Executive";
        public const string Llc = "LLC";
        public const string Manager = "Manager";

        public const string AnyReportRole = Admin + "," + Executive + "," + Llc + "," + Manager;

        public static readonly string[] All = { Admin, Executive, Llc, Manager };

        public static bool IsValid(string role)
        {
            return All.Contains(role, StringComparer.OrdinalIgnoreCase);
        }

        public static string Normalize(string role)
        {
            return All.FirstOrDefault(r => string.Equals(r, role, StringComparison.OrdinalIgnoreCase));
        }
    }

    public class AdminAccess
    {
        public string Username { get; set; }
        public string Role { get; set; }

        // Department names as reporting sees them (Departments.DepartmentName).
        public HashSet<string> Departments { get; set; }

        public bool IsAdmin { get { return Role == AppRoles.Admin; } }

        public bool SeesAllDepartments
        {
            get { return Role == AppRoles.Admin || Role == AppRoles.Executive; }
        }

        public bool CanSeeDepartment(string departmentName)
        {
            if (SeesAllDepartments) return true;
            if (string.IsNullOrWhiteSpace(departmentName)) return false;
            return Departments.Contains(departmentName.Trim());
        }

        // Restricts a department list (e.g. a filter dropdown) to the ones
        // this user may see.
        public List<string> FilterDepartments(IEnumerable<string> departmentNames)
        {
            return departmentNames.Where(CanSeeDepartment).ToList();
        }
    }

    public static class AdminAccessService
    {
        private const string CachePrefix = "AdminAccess:";
        private static readonly TimeSpan CacheFor = TimeSpan.FromMinutes(5);

        // Returns null when the username isn't in dbo.Admins. Cached for a
        // few minutes so every request doesn't hit the database; call
        // Invalidate after changing someone's role or departments.
        public static AdminAccess Get(string username)
        {
            if (string.IsNullOrWhiteSpace(username)) return null;

            string key = CachePrefix + username.ToLowerInvariant();
            var cached = HttpRuntime.Cache[key] as AdminAccess;
            if (cached != null) return cached;

            var access = new dal().GetAdminAccess(username);
            if (access != null)
            {
                HttpRuntime.Cache.Insert(key, access, null,
                    DateTime.UtcNow.Add(CacheFor), Cache.NoSlidingExpiration);
            }
            return access;
        }

        public static void Invalidate(string username)
        {
            if (string.IsNullOrWhiteSpace(username)) return;
            HttpRuntime.Cache.Remove(CachePrefix + username.ToLowerInvariant());
        }

        // Access for whoever is making the current request. Never null: an
        // unknown or unreadable user gets a "sees nothing" access object, so
        // callers can't accidentally fall through to unscoped data.
        public static AdminAccess Current(HttpContextBase context)
        {
            AdminAccess access = null;
            if (context != null && context.User != null && context.User.Identity.IsAuthenticated)
            {
                try { access = Get(context.User.Identity.Name); }
                catch { access = null; }
            }

            return access ?? new AdminAccess
            {
                Username = context != null && context.User != null ? context.User.Identity.Name : null,
                Role = null,
                Departments = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            };
        }
    }

    // [Authorize(Roles=...)] that shows an "Access denied" page to someone
    // who IS logged in but lacks the role, instead of MVC's default of
    // bouncing them back to the login page in a loop.
    public class RoleAuthorizeAttribute : AuthorizeAttribute
    {
        protected override void HandleUnauthorizedRequest(AuthorizationContext filterContext)
        {
            var user = filterContext.HttpContext.User;
            if (user != null && user.Identity.IsAuthenticated)
            {
                if (filterContext.HttpContext.Request.IsAjaxRequest())
                {
                    filterContext.HttpContext.Response.StatusCode = 403;
                    filterContext.Result = new JsonResult
                    {
                        Data = new { success = false, message = "You don't have access to this." },
                        JsonRequestBehavior = JsonRequestBehavior.AllowGet
                    };
                    return;
                }

                filterContext.HttpContext.Response.StatusCode = 403;
                filterContext.HttpContext.Response.TrySkipIisCustomErrors = true;
                filterContext.Result = new ViewResult { ViewName = "~/Views/Shared/AccessDenied.cshtml" };
                return;
            }

            base.HandleUnauthorizedRequest(filterContext);
        }
    }
}
