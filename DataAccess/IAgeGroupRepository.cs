using EPApi.Models;

namespace EPApi.DataAccess
{
    public interface IAgeGroupRepository
    {
        Task<IReadOnlyList<AgeGroupRow>> GetAllAsync(bool includeInactive, CancellationToken ct = default);
        Task<AgeGroupRow?> GetByIdAsync(Guid id, CancellationToken ct = default); // <- NUEVO
    }
}
