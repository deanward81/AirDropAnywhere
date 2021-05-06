using System;
using System.IO;
using System.Runtime.CompilerServices;
using System.Security.Cryptography.X509Certificates;

namespace AirDropAnywhere.Core.Resources
{
    internal static class ResourceLoader
    {
        private static X509Certificate2? _appleRootCA;

        public static X509Certificate2 AppleRootCA =>
            _appleRootCA ??= GetResource(
                "AppleRootCA.crt",
                s =>
                {
                    Span<byte> readBuffer = stackalloc byte[(int) s.Length];
                    s.Read(readBuffer);
                    return new X509Certificate2(readBuffer);
                });

        private static T GetResource<T>(string resourceName, Func<Stream, T> converter)
        {
            using var resourceStream = typeof(ResourceLoader).Assembly.GetManifestResourceStream(
                typeof(ResourceLoader),
                resourceName!
            );

            if (resourceStream == null)
            {
                throw new ArgumentException($"Could not load resource '{resourceName}'");
            }

            return converter(resourceStream);
        }
    }
}