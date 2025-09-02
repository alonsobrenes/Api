namespace EPApi.Services
{
    public interface IAuthService
    {
        Task<string?> LoginAsync(string userName, string password, CancellationToken ct = default);
    }
}