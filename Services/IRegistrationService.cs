using System;
using System.Threading;
using System.Threading.Tasks;

namespace EPApi.Services
{
    /// <summary>
    /// Orquesta el alta de organización, membresía del usuario y el otorgamiento del Trial.
    /// No modifica usuarios: eso sigue en UserRepository.
    /// </summary>
    public interface IRegistrationService
    {
        /// <summary>
        /// Crea una organización, asocia al usuario como miembro/owner y otorga trial.
        /// Devuelve el orgId creado.
        /// </summary>
        Task<Guid> CreateOrgAndMembershipAndTrialAsync(
            int userId,
            string? orgName,
            CancellationToken ct = default
        );
    }
}
