// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

namespace Microsoft.ServiceFabric.Services.Remoting.Mesh.Builder
{
    using System;
    using Microsoft.ServiceFabric.Services.Remoting.Base.Builder;
    using Microsoft.ServiceFabric.Services.Remoting.Base.V2;
    using Microsoft.ServiceFabric.Services.Remoting.Base.V2.Client;
    using Microsoft.ServiceFabric.Services.Remoting.Mesh.Client;

    internal class ServiceProxyGenerator : ProxyGenerator
    {
        private readonly IProxyActivator proxyActivator;

        public ServiceProxyGenerator(Type type, IProxyActivator createInstance)
            : base(type)
        {
            this.proxyActivator = createInstance;
        }

        public ServiceProxy CreateServiceProxy(
                ServiceRemotingPartitionClient remotingPartitionClient,
                IServiceRemotingMessageBodyFactory remotingMessageBodyFactory)
        {
            var serviceProxy = (ServiceProxy)this.proxyActivator.CreateInstance();
            serviceProxy.Initialize(this, remotingPartitionClient, remotingMessageBodyFactory);
            return serviceProxy;
        }
    }
}