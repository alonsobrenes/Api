using EPApi.Models;

namespace EPApi.DataAccess
{
    public interface IUserRepository
    {
        Task<User?> FindByEmailAsync(string email, CancellationToken ct = default);
        Task<bool> ExistsByEmailAsync(string userName, CancellationToken ct = default);
        Task<int> CreateAsync(User user, CancellationToken ct = default);
        Task<User?> GetByIdAsync(int id, CancellationToken ct = default);
        Task<bool> UpdateAvatarUrlAsync(int id, string? avatarUrl, CancellationToken ct = default);
    }
}