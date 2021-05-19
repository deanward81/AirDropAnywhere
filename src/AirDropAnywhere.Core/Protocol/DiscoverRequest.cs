// ReSharper disable AutoPropertyCanBeMadeGetOnly.Local

using System;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;
using AirDropAnywhere.Core.Resources;
using AirDropAnywhere.Core.Serialization;

namespace AirDropAnywhere.Core.Protocol
{
    /// <summary>
    /// Body of a request to the /Discover endpoint in the AirDrop HTTP API.
    /// </summary>
    internal class DiscoverRequest
    {
        /// <summary>
        /// Gets a binary blob representing a PKCS7 signed plist containing
        /// sender email and phone hashes. This is validated and deserialized into a <see cref="RecordData"/>
        /// object by <see cref="TryGetSenderRecordData"/>.
        /// </summary>
        public byte[] SenderRecordData { get; private set; } = Array.Empty<byte>();

        public bool TryGetSenderRecordData(out RecordData? recordData)
        {
            if (SenderRecordData == null || SenderRecordData.Length == 0)
            {
                recordData = default;
                return false;
            }
            
            // validate that the signature is valid
            var signedCms = new SignedCms();
            try
            {
                signedCms.Decode(SenderRecordData);
                signedCms.CheckSignature(
                    new X509Certificate2Collection(ResourceLoader.AppleRootCA), true
                );
            }
            catch
            {
                recordData = default;
                return false;
            }

            recordData = PropertyListSerializer.Deserialize<RecordData>(signedCms.ContentInfo.Content);
            return true;
        }
    }
}