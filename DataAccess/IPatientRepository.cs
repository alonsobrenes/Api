using EPApi.Models;
using Microsoft.AspNetCore.Mvc;

namespace EPApi.DataAccess
{
    public interface IPatientRepository
    {
        // ===== Listar (compat: sin propietario) =====
        Task<(IReadOnlyList<PatientListItem> Items, int Total)> GetPagedAsync(
            int page, int pageSize, string? search, bool? active, CancellationToken ct = default);

        // ===== Listar (con propietario) =====
        Task<(IReadOnlyList<PatientListItem> Items, int Total)> GetPagedAsync(
            int page, int pageSize, string? search, bool? active, int? ownerUserId, bool isAdmin, CancellationToken ct = default);

        // ===== GetById (compat: sin propietario) =====
        Task<PatientListItem?> GetByIdAsync(Guid id, CancellationToken ct = default);

        // ===== GetById (con propietario) =====
        Task<PatientListItem?> GetByIdAsync(Guid id, int? ownerUserId, bool isAdmin, CancellationToken ct = default);

        // ===== Crear (compat: sin propietario explícito) =====
        Task<Guid> CreateAsync(PatientCreateDto dto, CancellationToken ct = default);

        // ===== Crear (con propietario) =====
        Task<Guid> CreateAsync(PatientCreateDto dto, int ownerUserId, CancellationToken ct = default);

        // ===== Actualizar (compat: sin propietario) =====
        Task<bool> UpdateAsync(Guid id, PatientUpdateDto dto, CancellationToken ct = default);

        // ===== Actualizar (con propietario) =====
        Task<bool> UpdateAsync(Guid id, PatientUpdateDto dto, int? ownerUserId, bool isAdmin, CancellationToken ct = default);

        // ===== Eliminar (compat: sin propietario) =====
        Task<bool> DeleteAsync(Guid id, CancellationToken ct = default);

        // ===== Eliminar (con propietario) =====
        Task<bool> DeleteAsync(Guid id, int? ownerUserId, bool isAdmin, CancellationToken ct = default);
        
        Task<IReadOnlyList<PatientListItem>> GetPatientsByClinicianAsync(Guid orgId, int clinicianUserId, CancellationToken ct = default);
        

    }
}
