using Microsoft.Data.SqlClient;

namespace EPApi.Infrastructure
{
    public static class SqlErrorHelper
    {
        // 2627 = Violation of PRIMARY KEY/UNIQUE constraint
        // 2601 = Cannot insert duplicate key row in object ... with unique index ...
        public static bool IsUniqueViolation(SqlException ex)
            => ex.Number == 2627 || ex.Number == 2601;

        // (Opcional) Distingue por nombre de índice para dar mensajes más específicos
        public static string GetFriendlyUniqueMessage(SqlException ex)
        {
            var msg = ex.Message ?? string.Empty;

            // Ajusta a tus nombres reales de índices
            if (msg.Contains("UX_Disciplines_Name", StringComparison.OrdinalIgnoreCase))
                return "Discipline name must be unique.";
            if (msg.Contains("UX_Disciplines_Code", StringComparison.OrdinalIgnoreCase))
                return "Discipline code must be unique.";

            if (msg.Contains("UX_Categories_DisciplineId_Name", StringComparison.OrdinalIgnoreCase))
                return "Category name must be unique within the same discipline.";
            if (msg.Contains("UX_Categories_DisciplineId_Code", StringComparison.OrdinalIgnoreCase))
                return "Category code must be unique within the same discipline.";

            if (msg.Contains("UX_Subcategories_CategoryId_Name", StringComparison.OrdinalIgnoreCase))
                return "Subcategory name must be unique within the same category.";
            if (msg.Contains("UX_Subcategories_CategoryId_Code", StringComparison.OrdinalIgnoreCase))
                return "Subcategory code must be unique within the same category.";

            // Fallback genérico
            return "Duplicate key. The value must be unique.";
        }
    }
}
