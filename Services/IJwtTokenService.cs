using EPApi.Models;

namespace EPApi.Services
{
    public interface IJwtTokenService
    {
        string GenerateToken(User user);
    }
}