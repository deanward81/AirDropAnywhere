#nullable disable
using System;
using System.Threading.Tasks;

namespace AirDropAnywhere.Cli.Hubs
{
    [PolymorphicJsonInclude("connect", typeof(ConnectMessage))]
    [PolymorphicJsonInclude("askRequest", typeof(CanAcceptFilesRequestMessage))]
    [PolymorphicJsonInclude("askResponse", typeof(CanAcceptFilesResponseMessage))]
    [PolymorphicJsonInclude("fileUploadRequest", typeof(OnFileUploadedRequestMessage))]
    [PolymorphicJsonInclude("fileUploadResponse", typeof(OnFileUploadedResponseMessage))]
    internal abstract class AirDropHubMessage
    {
        public string Id { get; set; }
        public string ReplyTo { get; set; }

        public static async ValueTask<TMessage> CreateAsync<TMessage, TState>(Func<TMessage, TState, ValueTask> modifier, TState state) where TMessage : AirDropHubMessage, new()
        {
            if (modifier == null)
            {
                throw new ArgumentNullException(nameof(modifier));
            }
            
            var message = Create<TMessage>();
            await modifier(message, state);
            return message;
        }
        
        public static async ValueTask<TMessage> CreateAsync<TMessage>(Func<TMessage, ValueTask> modifier) where TMessage : AirDropHubMessage, new()
        {
            if (modifier == null)
            {
                throw new ArgumentNullException(nameof(modifier));
            }
            
            var message = Create<TMessage>();
            await modifier(message);
            return message;
        }

        private static TMessage Create<TMessage>() where TMessage : AirDropHubMessage, new() =>
            new()
            {
                Id = Guid.NewGuid().ToString("N"),
            };
    }
}