using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Principal;
using System.Web;
using System.Web.Mvc;
using System.Web.Optimization;
using System.Web.Routing;
using System.Web.Security;
using FASSET.eCheckIn_v1.Services;

namespace FASSET.eCheckIn_v1
{
    public class MvcApplication : System.Web.HttpApplication
    {
        protected void Application_Start()
        {
            AreaRegistration.RegisterAllAreas();
            FilterConfig.RegisterGlobalFilters(GlobalFilters.Filters);
            RouteConfig.RegisterRoutes(RouteTable.Routes);
            BundleConfig.RegisterBundles(BundleTable.Bundles);
            FASSET.eCheckIn_v1.Services.OfflineSyncService.EnsureRunning();
        }

        // Attaches the logged-in user's role (Admin / Executive / LLC /
        // Manager, from dbo.Admins) to the request, so [RoleAuthorize] and
        // User.IsInRole(...) work. Looked up per request but cached for a
        // few minutes in AdminAccessService. If the lookup fails (e.g. the
        // database is down) the user simply has no role for that request:
        // they can't reach protected pages, and the public check-in kiosk,
        // which never logs in, is unaffected.
        protected void Application_PostAuthenticateRequest(object sender, EventArgs e)
        {
            var context = HttpContext.Current;
            if (context == null || context.User == null) return;

            var identity = context.User.Identity as FormsIdentity;
            if (identity == null || !identity.IsAuthenticated) return;

            string[] roles = new string[0];
            try
            {
                var access = AdminAccessService.Get(identity.Name);
                if (access != null && access.Role != null)
                    roles = new[] { access.Role };
            }
            catch
            {
                roles = new string[0];
            }

            context.User = new GenericPrincipal(identity, roles);
        }
    }
}
