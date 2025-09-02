using EPApi.Models;
using static EPApi.Models.TestCrudDto;

namespace EPApi.DataAccess
{
    public interface ITestRepository
    {
        Task<(IReadOnlyList<TestListItem> Items, int Total)> GetPagedAsync(
            int page, int pageSize, string? search, CancellationToken ct = default);

        Task<TestDetail?> GetByIdAsync(Guid id, CancellationToken ct = default);
        Task<IReadOnlyList<TestQuestionRow>> GetQuestionsAsync(Guid testId, CancellationToken ct = default);
        Task<IReadOnlyList<TestScaleRow>> GetScalesAsync(Guid testId, CancellationToken ct = default);

        Task<Guid> CreateAsync(TestCreateDto dto, CancellationToken ct = default);
        Task<bool> UpdateAsync(Guid id, TestUpdateDto dto, CancellationToken ct = default);
        Task<bool> DeleteAsync(Guid id, CancellationToken ct = default); // borra en cascada

        Task<Guid> CreateQuestionAsync(Guid testId, TestQuestionCreateDto dto, CancellationToken ct = default);
        Task<bool> UpdateQuestionAsync(Guid testId, Guid questionId, TestQuestionUpdateDto dto, CancellationToken ct = default);
        Task<bool> DeleteQuestionAsync(Guid testId, Guid questionId, CancellationToken ct = default);

        Task<IReadOnlyList<TestQuestionOptionRow>> GetQuestionOptionsByTestAsync(Guid testId, CancellationToken ct = default);

        Task<TestDisciplinesReadDto> GetDisciplinesAsync(Guid testId, CancellationToken ct = default);
        Task ReplaceDisciplinesAsync(Guid testId, int[] disciplineIds, CancellationToken ct = default);

        Task<IReadOnlyList<TestTaxonomyRow>> GetTaxonomyAsync(Guid testId, CancellationToken ct = default);
        Task ReplaceTaxonomyAsync(Guid testId, IEnumerable<TestTaxonomyWriteItem> items, CancellationToken ct = default);

        Task<(IReadOnlyList<TestListItem> Items, int Total)> GetForUserAsync(
            int userId, int page, int pageSize, string? search, CancellationToken ct = default);

        Task<(IReadOnlyList<TestListItemDto> Items, int Total)> GetForUserAsync(
            int userId, int page, int pageSize, string? search, TestsForMeFilters? filters, CancellationToken ct = default);

        Task<IReadOnlyList<TestScaleQuestionRow>> GetScaleQuestionMapAsync(Guid testId, CancellationToken ct = default);

        Task SaveRunAsync(TestRunSave dto, CancellationToken ct = default);
        

    }
}
