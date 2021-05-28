using System.Threading.Tasks;
using AirDropAnywhere.Core.Protocol;

namespace AirDropAnywhere.Core
{
    /// <summary>
    /// Exposes a way for the AirDrop HTTP API to communicate with an arbitrary peer that does
    /// not directly support the AirDrop protocol.  
    /// </summary>
    public abstract class AirDropPeer
    {
        protected AirDropPeer()
        {
            Id = Utils.GetRandomString();
            Name = Id;
        }
        
        /// <summary>
        /// Gets the unique identifier of this peer.
        /// </summary>
        public string Id { get; }

        /// <summary>
        /// Gets the (display) name of this peer.
        /// </summary>
        public string Name { get; protected set; }

        /// <summary>
        /// Determines whether the peer wants to receive files from a sender. 
        /// </summary>
        /// <param name="request">
        /// An <see cref="AskRequest"/> object representing information about the sender
        /// and the files that they wish to send.
        /// sender
        /// </param>
        /// <returns>
        /// <c>true</c> if the receiver wants to accept the file transfer, <c>false</c> otherwise.
        /// </returns>
        public abstract ValueTask<bool> CanAcceptFilesAsync(AskRequest request);
    }
}