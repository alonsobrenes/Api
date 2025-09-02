using EPApi.Models;

namespace EPApi.DataAccess
{
    public interface IDisciplineRepository
    {
        Task<(IEnumerable<Discipline> Items, int Total)> GetPagedAsync(
            int page, int pageSize, string? search, bool? active, CancellationToken ct = default);

        Task<Discipline?> GetByIdAsync(int id, CancellationToken ct = default);

        Task<int> CreateAsync(Discipline item, CancellationToken ct = default);

        Task<bool> UpdateAsync(int id, Discipline item, CancellationToken ct = default);

        Task<bool> DeleteAsync(int id, CancellationToken ct = default);
    }
}
