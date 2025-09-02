namespace EPApi.DataAccess
{
    public interface IUserDisciplineRepository
    {
        Task<IReadOnlyList<(int Id, string Code, string Name)>> GetMineAsync(int userId, CancellationToken ct = default);
        Task ReplaceMineAsync(int userId, int[] disciplineIds, CancellationToken ct = default);
    }
}
