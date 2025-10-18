using System;
using System.Threading;
using System.Threading.Tasks;
using static EPApi.Controllers.AuthController;

namespace EPApi.Services
{
    public interface IRegistrationService
    {
                
        Task<Guid> RegisterAsync(RegisterRequest registerRequest, int userId, CancellationToken ct = default);
    }
}
