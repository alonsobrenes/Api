
using System.ComponentModel.DataAnnotations;

namespace EPApi.Models
{
    public  class PatientListItem
    {
        public Guid Id { get; set; }
        public string IdentificationType { get; set; } = default!; // 'cedula' | 'dimex' | 'pasaporte'
        public string IdentificationNumber { get; set; } = default!;
        public string FirstName { get; set; } = default!;
        public string LastName1 { get; set; } = default!;
        public string? LastName2 { get; set; }

        public DateTime? DateOfBirth { get; set; }
        public string? Sex { get; set; } // 'M','F','X' (si lo usas)
        public string? ContactEmail { get; set; }
        public string? ContactPhone { get; set; }
        public bool IsActive { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime UpdatedAt { get; set; }

        public string FullName =>
          string.Join(" ", new[] { FirstName, LastName1, LastName2 }.Where(s => !string.IsNullOrWhiteSpace(s)));
    }

    public sealed class PatientDetail : PatientListItem
    {
        public string? Description { get; set; }
        public DateTime? DateOfBirth { get; set; }
        public string? Sex { get; set; }
        public string? ContactEmail { get; set; }
        public string? ContactPhone { get; set; }
    }

    public sealed class PatientCreateDto
    {
        public string IdentificationType { get; set; } = default!; // 'cedula','dimex','pasaporte'
        public string IdentificationNumber { get; set; } = default!;
        public string FirstName { get; set; } = default!;
        public string LastName1 { get; set; } = default!;
        public string? LastName2 { get; set; }

        public DateTime? DateOfBirth { get; set; }
        public string? Sex { get; set; }
        public string? ContactEmail { get; set; }
        public string? ContactPhone { get; set; }
        public bool IsActive { get; set; } = true;
    }

    public sealed class PatientUpdateDto
    {
        public string IdentificationType { get; set; } = default!;
        public string IdentificationNumber { get; set; } = default!;
        public string FirstName { get; set; } = default!;
        public string LastName1 { get; set; } = default!;
        public string? LastName2 { get; set; }

        public DateTime? DateOfBirth { get; set; }
        public string? Sex { get; set; }
        public string? ContactEmail { get; set; }
        public string? ContactPhone { get; set; }
        public bool IsActive { get; set; }
    }
}
