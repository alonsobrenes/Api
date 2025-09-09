using System;
using System.Linq;
using System.Security.Claims;
using Microsoft.AspNetCore.Http;

namespace EPApi.Controllers.Shared
{
    public static class OrgResolver
    {
        public static Guid GetOrgIdOrThrow(HttpRequest req, ClaimsPrincipal user)
        {
            // 1) Header (case-insensitive)
            if (req.Headers.TryGetValue("X-Org-Id", out var hv))
            {
                var raw = hv.FirstOrDefault();
                if (!string.IsNullOrWhiteSpace(raw) && Guid.TryParse(raw, out var g))
                {
                    return g;
                }
            }

            // 2) Claims comunes
            var claimNames = new[] { "org_id", "orgid", "orgId", "org" };
            foreach (var name in claimNames)
            {
                var val = user.FindFirstValue(name);
                if (!string.IsNullOrWhiteSpace(val) && Guid.TryParse(val, out var g))
                {
                    return g;
                }
            }

            // 3) Error claro
            throw new InvalidOperationException("No se pudo resolver la organización. Envíe el encabezado X-Org-Id o agregue el claim org_id.");
        }
    }
}
