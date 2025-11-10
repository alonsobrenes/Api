namespace EPApi.DataAccess
{
    public interface IOrgRepository
    {
        Task<int> CountActiveMembersAsync(Guid orgId, CancellationToken ct);
    }
}
