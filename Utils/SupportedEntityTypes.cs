namespace EPApi.Utils
{
    public class SupportedEntityTypes
    {
        public const string Patient = "patient";
        public const string Test = "test";
        public const string Attempt = "attempt";
        public const string Attachment = "attachment";
        public const string Session = "session";
        public const string TestAttempt = "test_attempt";
        public const string Professional = "professional";
        // public const string NewType = "newtype";

        // Un HashSet para validaciones rápidas (O(1) lookup)
        public static readonly HashSet<string> All = new(StringComparer.OrdinalIgnoreCase)
        {
            Patient,
            Test,
            Attempt,
            Attachment,
            Session,
            TestAttempt,
            Professional
            // NewType
        };

        public static bool IsSupported(string type) => !string.IsNullOrEmpty(type) && All.Contains(type.Trim());
    }
}
