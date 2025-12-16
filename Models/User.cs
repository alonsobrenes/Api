namespace EPApi.Models
{
    public class User
    {
        public int Id { get; set; }
        public string Email { get; set; } = string.Empty;
        public string PasswordHash { get; set; } = string.Empty;
        public string? Role { get; set; }
        public DateTime CreatedAt { get; set; }
        public string? AvatarUrl { get; set; }
        public string? FirstName { get; set; }
        public string? LastName1 { get; set; }
        public string? LastName2 { get; set; }
        public string? Phone { get; set; }
        public string? TitlePrefix { get; set; }
        public string? LicenseNumber { get; set; }
        public string? SignatureImageUrl { get; set; }
    }

    //public sealed class UserProfileDto
    //{
    //    public int Id { get; set; }
    //    public string Email { get; set; } = null!;
    //    public string Role { get; set; } = null!;
    //    public DateTime CreatedAt { get; set; }
    //    public string? AvatarUrl { get; set; }
    //    public string? FirstName { get; set; }
    //    public string? LastName1 { get; set; }
    //    public string? LastName2 { get; set; }
    //    public string? Phone { get; set; }
    //    public string? TitlePrefix { get; set; }
    //    public string? LicenseNumber { get; set; }
    //    public string? SignatureImageUrl { get; set; }
    //}
}