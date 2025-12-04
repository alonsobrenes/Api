using EPApi.Models;
using EPApi.Services.Orgs;

namespace EPApi.Services
{
    public interface IJwtTokenService
    {
        string GenerateToken(User user, Guid? orgId = null, bool? isOwner = false, OrgMode? orgMode = OrgMode.Solo);
    }
}