using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace AutoBack.Connection;

internal static class HostIdentity
{
    public static X509Certificate2 LoadOrCreate(ProtectedStore store)
    {
        var existing = store.Load<byte[]>("identity.dat");
        if (existing is not null)
        {
            try { return X509CertificateLoader.LoadPkcs12(existing, null, X509KeyStorageFlags.UserKeySet); }
            finally { CryptographicOperations.ZeroMemory(existing); }
        }
        using var key = RSA.Create(3072);
        var request = new CertificateRequest("CN=AutoBack Host", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new("1.3.6.1.5.5.7.3.1") }, false));
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddYears(5));
        var pfx = certificate.Export(X509ContentType.Pfx);
        try
        {
            store.Save("identity.dat", pfx);
            return X509CertificateLoader.LoadPkcs12(pfx, null, X509KeyStorageFlags.UserKeySet);
        }
        finally { CryptographicOperations.ZeroMemory(pfx); }
    }
}
