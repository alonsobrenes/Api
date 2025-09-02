using System.ComponentModel.DataAnnotations;

namespace EPApi.Models
{
    public class Discipline
    {
        public int Id { get; set; }

        [Required, StringLength(32, MinimumLength = 2)]
        public string Code { get; set; } = default!;

        [Required, StringLength(150, MinimumLength = 2)]
        public string Name { get; set; } = default!;

        [StringLength(500)]
        public string? Description { get; set; }

        public bool IsActive { get; set; } = true;

        public DateTime CreatedAt { get; set; }
        public DateTime UpdatedAt { get; set; }
    }

    // DTOs para la API (separados del entity si lo prefieres así)
    public sealed class DisciplineCreateDto
    {
        [Required, StringLength(32, MinimumLength = 2)]
        public string Code { get; set; } = default!;

        [Required, StringLength(150, MinimumLength = 2)]
        public string Name { get; set; } = default!;

        [StringLength(500)]
        public string? Description { get; set; }

        public bool IsActive { get; set; } = true;
    }

    public sealed class DisciplineUpdateDto
    {
        [Required, StringLength(150, MinimumLength = 2)]
        public string Name { get; set; } = default!;

        [StringLength(500)]
        public string? Description { get; set; }

        public bool IsActive { get; set; } = true;
    }
}

