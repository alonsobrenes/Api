// Utils/HttpUserExtensions.cs
using System.Security.Claims;

namespace EPApi.Utils
{
    public static class HttpUserExtensions
    {
        public static int? TryGetUserId(this ClaimsPrincipal user)
        {
            // Ajusta el orden según tu JWT/Identity
            var s = user.FindFirstValue(ClaimTypes.NameIdentifier)
                    ?? user.FindFirstValue("sub")
                    ?? user.FindFirstValue("uid")
                    ?? user.FindFirstValue("user_id");
            if (int.TryParse(s, out var id)) return id;
            return null;
        }

        public static bool IsAdmin(this ClaimsPrincipal user)
            => user.IsInRole("Admin") || user.IsInRole("SuperAdmin");
    }
}
