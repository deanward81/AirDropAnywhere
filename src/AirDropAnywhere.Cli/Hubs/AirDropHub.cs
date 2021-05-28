using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using System.Threading.Tasks.Sources;
using AirDropAnywhere.Core;
using AirDropAnywhere.Core.Protocol;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.ObjectPool;

namespace AirDropAnywhere.Cli.Hubs
{
    internal class AirDropHub : Hub
    {
        private readonly AirDropService _service;
        private readonly ILogger<AirDropHub> _logger;

        private static readonly ObjectPool<CallbackValueTaskSource> _callbackPool =
            new DefaultObjectPool<CallbackValueTaskSource>(
                new CallbackObjectPoolPolicy(), 5
            );

        public AirDropHub(AirDropService service, ILogger<AirDropHub> logger)
        {
            _service = service ?? throw new ArgumentNullException(nameof(service));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        /// <summary>
        /// Starts a bi-directional stream between the server and the client.
        /// </summary>
        /// <param name="stream">
        /// <see cref="IAsyncEnumerable{T}"/> of <see cref="AirDropHubMessage"/>-derived messages
        /// from the client.
        /// </param>
        /// <param name="cancellationToken">
        /// <see cref="CancellationToken"/> used to cancel the operation.
        /// </param>
        /// <returns>
        /// <see cref="IAsyncEnumerable{T}"/> of <see cref="AirDropHubMessage"/>-derived messages
        /// from the server.
        /// </returns>
        public async IAsyncEnumerable<AirDropHubMessage> StreamAsync(IAsyncEnumerable<AirDropHubMessage> stream, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            var serverChannel = Channel.CreateUnbounded<MessageWithCallback>();
            var callbacks = new Dictionary<string, CallbackValueTaskSource>();
            var peer = new AirDropHubPeer(serverChannel.Writer, _logger);

            // register the peer so that it's advertised as supporting AirDrop
            await _service.RegisterPeerAsync(peer);

            try
            {
                await foreach (var message in FullDuplexStreamAsync(Produce, stream, Consume, cancellationToken))
                {
                    yield return message;
                }
            }
            finally
            {
                await _service.UnregisterPeerAsync(peer);
            }

            // Iterates any message + callback tuples that are queued into
            // our server-side channel, associates the callback with the message's
            // unique identifier so that we can handle request/response scenarios
            // and yields the message itself back to the caller so it is streamed
            // to the client
            async IAsyncEnumerable<AirDropHubMessage> Produce()
            {
                await foreach (var (message, callback) in serverChannel.Reader.ReadAllAsync(cancellationToken))
                {
                    if (callback != null)
                    {
                        callbacks[message.Id] = callback;
                    }
                    
                    yield return message;
                }
            }

            // Iterates any messages streamed from the client and, if the ReplyTo
            // property is set, uses it to find an associated callback. If found the
            // callback is fired, otherwise a warning is logged and the message is ignored.
            //
            // If the ReplyTo property is _not_ set the message is treated as a new, unsolicited,
            // message and is queued to the peer for handling.
            async ValueTask Consume(IAsyncEnumerable<AirDropHubMessage> messages)
            {
                await foreach (var message in messages.WithCancellation(cancellationToken))
                {
                    if (message.ReplyTo != null)
                    {
                        if (callbacks.Remove(message.ReplyTo, out var callback))
                        {
                            // this message is a reply to another message, notify anything
                            // awaiting the result instead of dispatching to the peer
                            // as a new message.
                            callback.SetResult(message);
                        }
                        else
                        {
                            _logger.LogWarning("Unexpected reply to message {MessageId}", message.ReplyTo);
                        }

                        continue;
                    }
                    
                    // this was an unsolicited message from the client, have the peer handle it
                    peer.OnMessage(message);
                }
            }
        }
        
        private async IAsyncEnumerable<TResponse> FullDuplexStreamAsync<TRequest, TResponse>(
            Func<IAsyncEnumerable<TResponse>> producer,
            IAsyncEnumerable<TRequest> source,
            Func<IAsyncEnumerable<TRequest>, ValueTask> consumer,
            [EnumeratorCancellation] CancellationToken cancellationToken
        )
        {
            using var allDone = CancellationTokenSource.CreateLinkedTokenSource(Context.ConnectionAborted, cancellationToken);
            Task? consumed = null;
            try
            {
                consumed = Task.Run(
                    () => consumer(source).AsTask(), 
                    allDone.Token
                ); // note this shares a capture scope

                await foreach (var value in producer().WithCancellation(cancellationToken).ConfigureAwait(false))
                {
                    yield return value;
                }
            }
            finally
            {
                // stop the producer, in any exit scenario
                allDone.Cancel();
                
                if (consumed != null)
                {
                    await consumed.ConfigureAwait(false);
                }
            }
        }

        private readonly struct MessageWithCallback
        {
            public MessageWithCallback(AirDropHubMessage message, CallbackValueTaskSource? callback)
            {
                Message = message ?? throw new ArgumentNullException(nameof(message));
                Callback = callback;
            }
            
            public AirDropHubMessage Message { get; }
            public CallbackValueTaskSource? Callback { get; }
            
            public void Deconstruct(out AirDropHubMessage message, out CallbackValueTaskSource? callback)
            {
                message = Message;
                callback = Callback;
            }
        }
        
        /// <summary>
        /// Implementation of <see cref="IValueTaskSource{T}"/> that enables
        /// a request/response-style conversation to occur over a SignalR full
        /// duplex connection to a client. This is used to enable the hub to
        /// perform a callback.
        /// </summary>
        private class CallbackValueTaskSource : IValueTaskSource<AirDropHubMessage>
        {
            private ManualResetValueTaskSourceCore<AirDropHubMessage> _valueTaskSource; // mutable struct; do not make this readonly

            public void SetResult(AirDropHubMessage message) => _valueTaskSource.SetResult(message);
            
            public AirDropHubMessage GetResult(short token) => _valueTaskSource.GetResult(token);

            public ValueTaskSourceStatus GetStatus(short token) => _valueTaskSource.GetStatus(token);

            public void OnCompleted(
                Action<object?> continuation,
                object? state,
                short token,
                ValueTaskSourceOnCompletedFlags flags
            ) => _valueTaskSource.OnCompleted(continuation, state, token, flags);

            public void Reset() => _valueTaskSource.Reset();

            public async ValueTask<TResponse> TransformAsync<TMessage, TResponse>(Func<TMessage, TResponse> transformer) where TMessage : AirDropHubMessage
            {
                var result = await new ValueTask<AirDropHubMessage>(this, _valueTaskSource.Version);
                if (result is TMessage typedResult)
                {
                    return transformer(typedResult);
                }
                
                throw new InvalidCastException(
                    $"Cannot convert message of type {result.GetType()} to {typeof(TMessage)}"
                );
            }
        }
        
        /// <summary>
        /// Implementation of <see cref="AirDropPeer"/> that translates from our SignalR
        /// message format to the protocol expected by our proxy.
        /// </summary>
        private class AirDropHubPeer : AirDropPeer
        {
            private readonly ChannelWriter<MessageWithCallback> _serverQueue;
            private readonly ILogger _logger;

            public AirDropHubPeer(ChannelWriter<MessageWithCallback> serverQueue, ILogger logger)
            {
                _serverQueue = serverQueue ?? throw new ArgumentNullException(nameof(serverQueue));
                _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            }
            
            /// <summary>
            /// Handles an unsolicited message from the connected client.
            /// </summary>
            internal void OnMessage(AirDropHubMessage message)
            {
                switch (message)
                {
                    case ConnectMessage connectMessage:
                        Name = connectMessage.Name;
                        break;
                    default:
                        _logger.LogWarning("Unable to handle message of type {MessageType}", message.GetType());
                        break;
                }
            }
            
            /// <inheritdoc cref="AirDropPeer"/>.
            public override async ValueTask<bool> CanAcceptFilesAsync(AskRequest request)
            {
                var requestMessage = await AirDropHubMessage.CreateAsync(
                    (CanAcceptFilesRequestMessage m, AskRequest r) =>
                    {
                        m.SenderComputerName = r.SenderComputerName;
                        m.Files = r.Files
                            .Select(
                                f => new CanAcceptFileMetadata
                                {
                                    FileName = f.FileName,
                                    FileType = f.FileType
                                })
                            .ToList();

                        return default;
                    },
                    request
                );

                var callback = _callbackPool.Get();
                try
                {
                    await _serverQueue.WriteAsync(new MessageWithCallback(requestMessage, callback));
                    return await callback.TransformAsync<CanAcceptFilesResponseMessage, bool>(
                        x => x.Accepted
                    );
                }
                finally
                {
                    _callbackPool.Return(callback);
                }
            }
        }
        
        /// <summary>
        /// Pooled object policy that ensures <see cref="CallbackValueTaskSource.Reset"/>
        /// is called when the value is returned to the pool.
        /// </summary>
        private class CallbackObjectPoolPolicy : PooledObjectPolicy<CallbackValueTaskSource>
        {
            public override CallbackValueTaskSource Create() => new();

            public override bool Return(CallbackValueTaskSource obj)
            {
                obj.Reset();
                return true;
            }
        }
    }
}