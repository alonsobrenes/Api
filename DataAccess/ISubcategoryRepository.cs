using EPApi.Models;

namespace EPApi.DataAccess
{
    public interface ISubcategoryRepository
    {
        Task<(IEnumerable<Subcategory> Items, int Total)> GetPagedAsync(
            int page, int pageSize, string? search, bool? active, int? categoryId, int? disciplineId, CancellationToken ct = default);

        Task<Subcategory?> GetByIdAsync(int id, CancellationToken ct = default);

        Task<int> CreateAsync(Subcategory item, CancellationToken ct = default);

        Task<bool> UpdateAsync(int id, Subcategory item, CancellationToken ct = default);

        Task<bool> DeleteAsync(int id, CancellationToken ct = default);
    }
}
