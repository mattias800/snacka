using Snacka.Server.Services;

namespace Snacka.Server.Tests.Services;

[TestClass]
public class SecretValidatorTests
{
    [DataTestMethod]
    [DataRow(null)]
    [DataRow("")]
    [DataRow("short")]
    [DataRow("ChangeThisToASecureSecretKeyAtLeast32CharactersLong!")] // .env.example
    [DataRow("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    [DataRow("abababababababababababababababababab")]
    public void RejectsMissingWeakOrPlaceholderSecrets(string? secret) =>
        Assert.IsNotNull(SecretValidator.ValidateJwtSecret(secret));

    [DataTestMethod]
    [DataRow("q7Vx2LmP9rTz4Kb8Nw1Hc6Jd3Fy5Gs0Ue-Integration")]
    [DataRow("3mJ0e5nq8yQ2c9Vb7tXk1fR4wZ6hL0pD+aG8sN2uE5iO7rT9yB1cM3vK6xW4zQ0j")] // openssl rand -base64 48 style
    public void AcceptsRandomSecrets(string secret) =>
        Assert.IsNull(SecretValidator.ValidateJwtSecret(secret));
}
