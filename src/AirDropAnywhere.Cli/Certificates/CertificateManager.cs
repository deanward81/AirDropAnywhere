using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Microsoft.Extensions.Logging;

namespace AirDropAnywhere.Cli.Certificates
{
    /// <summary>
    /// Manages self-signed certificates for the AirDrop HTTPS endpoint.
    /// </summary>
    internal class CertificateManager
    {
        private readonly ILogger<CertificateManager> _logger;

        public CertificateManager(ILogger<CertificateManager> logger)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }
        
        /// <summary>
        /// This OID is in the badly-documented "private" range based upon this ServerFault
        /// answer: https://serverfault.com/a/861475. We should be fine to use this
        /// as it's only used for our own ephemeral self-signed certificate.
        /// </summary>
        private const string AirDropHttpsOid = "1.3.9999.1.1";
        private const string AirDropHttpsOidFriendlyName = "AirDrop Anywhere HTTPS certificate";

        private const string ServerAuthenticationEnhancedKeyUsageOid = "1.3.6.1.5.5.7.3.1";
        private const string ServerAuthenticationEnhancedKeyUsageOidFriendlyName = "Server Authentication";
        private const string AirDropHttpsDnsName = "airdrop.local";
        private const string AirDropHttpsDistinguishedName = "CN=" + AirDropHttpsDnsName;

        /// <summary>
        /// Gets or creates a self-signed certificate suitable for serving requests over
        /// AirDrop's HTTPS endpoint
        /// </summary>
        /// <returns>
        /// An <see cref="X509Certificate2"/> representing the self-signed certificate. 
        /// </returns>
        public X509Certificate2 GetOrCreate()
        {
            const StoreName storeName = StoreName.My;
            const StoreLocation storeLocation = StoreLocation.CurrentUser;
            
            List<X509Certificate2> validCertificates;
            using (var store = new X509Store(storeName, storeLocation))
            {
                store.Open(OpenFlags.ReadOnly);
                validCertificates = store.Certificates
                        .Where(c => HasOid(c, AirDropHttpsOid) && IsValidCertificate(c, DateTimeOffset.Now))
                        .ToList();
            }

            if (validCertificates.Count > 0)
            {
                _logger.LogInformation(
                    "Found valid self-signed certificate for AirDrop HTTP services..."
                );

                return validCertificates[0];
            }
            
            _logger.LogInformation(
                "Generating self-signed certificate for AirDrop HTTP services..."
            );
            
            // TODO: make this CreateOrGet so we don't keep generating a new certificate 
            // everytime the service starts. We can store the created certificate in the 
            // user's private X509 store
            var cert = CreateCertificate(DateTimeOffset.Now, DateTimeOffset.Now.AddYears(1));
            using (var store = new X509Store(storeName, storeLocation))
            {
                store.Open(OpenFlags.ReadWrite);
                store.Add(cert);
                store.Close();
            }
            
            _logger.LogInformation(
                "Generated self-signed certificate for AirDrop HTTP services 🎉"
            );
            return cert;
        }
        
        /// <summary>
        /// This method is largely lifted from the code used to generate self-signed 
        /// X509 certificates in the .NET SDK. See https://github.com/dotnet/aspnetcore/blob/main/src/Shared/CertificateGeneration/CertificateManager.cs
        /// </summary>
        private static X509Certificate2 CreateCertificate(DateTimeOffset notBefore, DateTimeOffset notAfter)
        {
            var subject = new X500DistinguishedName(AirDropHttpsDistinguishedName);
            var extensions = new List<X509Extension>();
            var sanBuilder = new SubjectAlternativeNameBuilder();
            sanBuilder.AddDnsName(AirDropHttpsDnsName);

            var keyUsage = new X509KeyUsageExtension(X509KeyUsageFlags.KeyEncipherment | X509KeyUsageFlags.DigitalSignature, critical: true);
            var enhancedKeyUsage = new X509EnhancedKeyUsageExtension(
                new OidCollection
                {
                    new(
                        ServerAuthenticationEnhancedKeyUsageOid,
                        ServerAuthenticationEnhancedKeyUsageOidFriendlyName
                    )
                },
                critical: true);

            var basicConstraints = new X509BasicConstraintsExtension(
                certificateAuthority: false,
                hasPathLengthConstraint: false,
                pathLengthConstraint: 0,
                critical: true
            );

            var bytePayload = Encoding.ASCII.GetBytes(AirDropHttpsOidFriendlyName);
            var aspNetHttpsExtension = new X509Extension(
                new AsnEncodedData(
                    new Oid(AirDropHttpsOid, AirDropHttpsOidFriendlyName),
                    bytePayload),
                critical: false);

            extensions.Add(basicConstraints);
            extensions.Add(keyUsage);
            extensions.Add(enhancedKeyUsage);
            extensions.Add(sanBuilder.Build(critical: true));
            extensions.Add(aspNetHttpsExtension);

            var certificate = CreateSelfSignedCertificate(subject, extensions, notBefore, notAfter);
            return certificate;
        }

        private static X509Certificate2 CreateSelfSignedCertificate(
            X500DistinguishedName subject,
            IEnumerable<X509Extension> extensions,
            DateTimeOffset notBefore,
            DateTimeOffset notAfter
        )
        {
            using var key = CreateKeyMaterial(4096);

            var request = new CertificateRequest(subject, key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            foreach (var extension in extensions)
            {
                request.CertificateExtensions.Add(extension);
            }

            var result = request.CreateSelfSigned(notBefore, notAfter);
            return result;

            RSA CreateKeyMaterial(int minimumKeySize)
            {
                var rsa = RSA.Create(minimumKeySize);
                if (rsa.KeySize < minimumKeySize)
                {
                    throw new InvalidOperationException($"Failed to create a key with a size of {minimumKeySize} bits");
                }

                return rsa;
            }
        }
        
        private static bool IsValidCertificate(X509Certificate2 certificate, DateTimeOffset currentDate) =>
            certificate.NotBefore <= currentDate &&
            currentDate <= certificate.NotAfter;
        
        private static bool HasOid(X509Certificate2 certificate, string oid) =>
            certificate.Extensions
                .Any(e => string.Equals(oid, e.Oid?.Value, StringComparison.Ordinal));

    }
}