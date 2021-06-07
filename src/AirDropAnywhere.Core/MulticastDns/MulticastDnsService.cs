using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Net;
using Makaretu.Dns;

namespace AirDropAnywhere.Core.MulticastDns
{
    public class MulticastDnsService
    {
        public static readonly DomainName Root = new("local");
        public static readonly DomainName Discovery = new("_services._dns-sd._udp.local");
        public static readonly TimeSpan DefaultTTL = TimeSpan.FromMinutes(5);
        
        private MulticastDnsService(
            DomainName serviceName, 
            DomainName instanceName,
            DomainName hostName,
            ImmutableArray<IPEndPoint> endpoints,
            ImmutableDictionary<string, string> properties
        )
        {
            if (endpoints.IsDefaultOrEmpty)
            {
                throw new ArgumentException("Endpoints are required", nameof(endpoints));
            }

            ServiceName = serviceName ?? throw new ArgumentNullException(nameof(serviceName));
            InstanceName = instanceName ?? throw new ArgumentNullException(nameof(instanceName));
            HostName = hostName ?? throw new ArgumentNullException(nameof(hostName));
            EndPoints = endpoints;
            Properties = properties ?? throw new ArgumentNullException(nameof(properties));
            QualifiedServiceName = DomainName.Join(ServiceName, Root);
            QualifiedInstanceName = DomainName.Join(InstanceName, QualifiedServiceName);
        }
        
        public DomainName ServiceName { get; }
        public DomainName InstanceName { get; }
        public DomainName HostName { get; }
        public DomainName QualifiedServiceName { get; }
        public DomainName QualifiedInstanceName { get; }
        public ImmutableArray<IPEndPoint> EndPoints { get; }
        public ImmutableDictionary<string, string> Properties { get; }

        private Message? _message;
        
        public Message ToMessage()
        {
            Message Create()
            {
                var hostName = DomainName.Join(HostName, Root);
                var message = new Message
                {
                    QR = true
                };
                
                // grab distinct ports
                var ports = EndPoints.Select(x => x.Port).Distinct();
                foreach (var port in ports)
                {
                    message.Answers.Add(
                        new SRVRecord
                        {
                            Name = QualifiedInstanceName,
                            Target = hostName,
                            TTL = DefaultTTL,
                            Port = (ushort) port
                        });
                }

                foreach (var endpoint in EndPoints)
                {
                    var addressRecord = AddressRecord.Create(hostName, endpoint.Address);
                    addressRecord.TTL = DefaultTTL;
                    message.Answers.Add(addressRecord);
                }

                message.Answers.Add(
                    new TXTRecord
                    {
                        Name = QualifiedInstanceName,
                        Strings = Properties.Select(kv => $"{kv.Key}={kv.Value}").ToList(),
                        TTL = DefaultTTL
                    }
                );
                return message;
            }
            

            return _message ??= Create();
        }
        
        internal class Builder
        {
#pragma warning disable 8618
            private DomainName _serviceName;
            private DomainName _instanceName;
            private DomainName _hostName;
#pragma warning restore 8618
            
            private readonly ImmutableArray<IPEndPoint>.Builder _endpoints = ImmutableArray.CreateBuilder<IPEndPoint>();
            private readonly ImmutableDictionary<string, string>.Builder _properties = ImmutableDictionary.CreateBuilder<string, string>();

            public Builder SetNames(DomainName serviceName, DomainName instanceName, DomainName hostName)
            {
                _serviceName = serviceName ?? throw new ArgumentNullException(nameof(serviceName));
                _instanceName = instanceName ?? throw new ArgumentNullException(nameof(instanceName));
                _hostName = hostName ?? throw new ArgumentNullException(nameof(hostName));
                return this;
            }

            public Builder AddEndpoint(IPEndPoint ipEndPoint)
            {
                _endpoints.Add(ipEndPoint);
                return this;
            }

            public Builder AddEndpoints(IEnumerable<IPEndPoint> ipEndPoints)
            {
                _endpoints.AddRange(ipEndPoints);
                return this;
            }

            public Builder AddProperty(string key, string value)
            {
                _properties.Add(key, value);
                return this;
            }
            
            public MulticastDnsService Build() => new MulticastDnsService(
                _serviceName, 
                _instanceName, 
                _hostName, 
                _endpoints.ToImmutable(), 
                _properties.ToImmutable()
            );
        }
    }
}