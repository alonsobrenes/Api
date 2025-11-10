using EPApi.Models;

namespace EPApi.Services
{
    public interface IJwtTokenService
    {
        string GenerateToken(User user, Guid? orgId = null, bool? isOwner = false);
    }
}