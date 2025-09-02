using EPApi.Models;

namespace EPApi.DataAccess
{
    public interface ICategoryRepository
    {
        Task<(IEnumerable<Category> Items, int Total)> GetPagedAsync(
            int page, int pageSize, string? search, bool? active, int? disciplineId, CancellationToken ct = default);

        Task<Category?> GetByIdAsync(int id, CancellationToken ct = default);

        Task<int> CreateAsync(Category item, CancellationToken ct = default);

        Task<bool> UpdateAsync(int id, Category item, CancellationToken ct = default);

        Task<bool> DeleteAsync(int id, CancellationToken ct = default);
    }
}
