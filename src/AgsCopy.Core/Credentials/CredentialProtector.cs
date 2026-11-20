using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;

namespace AgsCopy.Core.Credentials;

/// <summary>
/// Encrypts network passwords with DPAPI. The service runs as LocalSystem and uses the
/// CurrentUser scope, so only processes running as LocalSystem on this machine can decrypt
/// them; the files are also readable only by SYSTEM and Administrators.
/// </summary>
[SupportedOSPlatform("windows")]
public static class CredentialProtector
{
    private static readonly byte[] Entropy = "AGS-Copy/network-credential/v1"u8.ToArray();

    public static string Protect(string password) =>
        Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(password), Entropy, DataProtectionScope.CurrentUser));

    public static string Unprotect(string protectedPassword) =>
        Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(protectedPassword), Entropy, DataProtectionScope.CurrentUser));
}
