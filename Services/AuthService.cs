using EPApi.DataAccess;
using EPApi.Models;
using EPApi.Services.Orgs;

namespace EPApi.Services
{
    public class AuthService : IAuthService
    {
        private readonly IUserRepository _users;
        private readonly IPasswordHasher _hasher;
        private readonly IJwtTokenService _jwt;
        private readonly BillingRepository _billing;
        private readonly IOrgAccessService _orgAccess;

        public AuthService(IUserRepository users, IPasswordHasher hasher, IJwtTokenService jwt, BillingRepository billing, IOrgAccessService orgAccess)
        {
            _users = users;
            _hasher = hasher;
            _jwt = jwt;
            _billing = billing;
            _orgAccess = orgAccess;
        }

        public async Task<string?> LoginAsync(string email, string password, CancellationToken ct = default)
        {
            var user = await _users.FindByEmailAsync(email, ct);
            if (user is null) return null;
            if (!_hasher.Verify(password, user.PasswordHash)) return null;
            
            
            var orgId = await _billing.GetOrgIdForUserAsync(user.Id, ct);
            var isOwner = true;
            var orgMode = OrgMode.Solo;

            if (user.Role != "admin") {
                orgMode = await _orgAccess.GetOrgModeAsync(orgId.Value, ct);
                isOwner = await _orgAccess.IsOwnerAsync(user.Id);
            }

            return _jwt.GenerateToken(user, orgId,isOwner, orgMode);
        }
    }
}