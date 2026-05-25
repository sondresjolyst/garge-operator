using System.ComponentModel.DataAnnotations;

namespace garge_operator.Models
{
    /// <summary>
    /// Strongly-typed binding for the <c>Api</c> configuration section. The keys mirror the
    /// historical stringly-typed reads (<c>Api:BaseUrl</c>, <c>Api:Email</c>, <c>Api:Password</c>)
    /// so existing appsettings and environment variables continue to bind unchanged.
    /// </summary>
    public class ApiOptions
    {
        public const string SectionName = "Api";

        [Required(AllowEmptyStrings = false, ErrorMessage = "Api:BaseUrl not set in configuration.")]
        public string BaseUrl { get; set; } = string.Empty;

        public string? Email { get; set; }

        public string? Password { get; set; }
    }
}
