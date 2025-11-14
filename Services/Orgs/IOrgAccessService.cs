using System;
using System.Threading;
using System.Threading.Tasks;

namespace EPApi.Services.Orgs
{
    public enum OrgMode
    {
        Solo = 0,
        Multi = 1
    }

    public interface IOrgAccessService
    {
        /// <summary>
        /// Devuelve el modo de la organización (Solo = 1 seat, Multi = >1 seats).
        /// Fuente: subscriptions (plan_code) + billing_plan_entitlements(key='seats').
        /// </summary>
        Task<OrgMode> GetOrgModeAsync(Guid orgId, CancellationToken ct = default);

        /// <summary>
        /// True si el usuario es OWNER en org_members y la org es Multi (seats > 1).
        /// </summary>
        Task<bool> IsOwnerOfMultiSeatOrgAsync(int userId, Guid orgId, CancellationToken ct = default);

        Task<bool> IsOwnerAsync(int userId, CancellationToken ct = default);

        Task<Guid?> GetSupportOrgForUserAsync(int userId, CancellationToken ct = default);

    }
}
