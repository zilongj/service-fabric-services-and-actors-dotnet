// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

namespace Microsoft.ServiceFabric.Services.Remoting.Base.V2.Runtime
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.Globalization;
    using System.Runtime.ExceptionServices;
    using System.Threading;
    using System.Threading.Tasks;
    using Microsoft.ServiceFabric.Services.Remoting.Base.Description;
    using Microsoft.ServiceFabric.Services.Remoting.Base.Diagnostic;
    using Microsoft.ServiceFabric.Services.Remoting.Base.Runtime;
    using Microsoft.ServiceFabric.Services.Remoting.Base.V2.Builder;

    /// <summary>
    /// Provides an implementation of <see cref="IServiceRemotingMessageHandler"/> that can dispatch
    /// messages to the service implementing <see cref="IService"/> interface.
    /// </summary>
    internal class ServiceRemotingMessageDispatcher : IServiceRemotingMessageHandler, IDisposable
    {
        private ServiceRemotingCancellationHelper cancellationHelper;
        private Dictionary<int, MethodDispatcherBase> methodDispatcherMap;
        private Func<string, InterfaceDetails> getKnownTypes;
        private object serviceImplementation;

        private IServiceRemotingMessageBodyFactory serviceRemotingMessageBodyFactory;
        private ServicePerformanceCounterProvider servicePerformanceCounterProvider;

        public ServiceRemotingMessageDispatcher(
            Guid partitionId,
            long replicaId,
            string traceId,
            object serviceImplementation,
            Dictionary<int, MethodDispatcherBase> methodDispatcherMap,
            Func<string, InterfaceDetails> getKnownTypes,
            bool nonserviceInterface = false,
            IServiceRemotingMessageBodyFactory serviceRemotingMessageBodyFactory = null)
        {
            var serviceTypeInformation = ServiceTypeInformation.Get(serviceImplementation.GetType());
            this.methodDispatcherMap = methodDispatcherMap;
            this.getKnownTypes = getKnownTypes;
            this.Initialize(partitionId, replicaId, traceId, serviceImplementation, serviceTypeInformation.InterfaceTypes, nonserviceInterface, serviceRemotingMessageBodyFactory);
        }

        /// <summary>
        /// Handles a message from the client that requires a response from the service. This Api can be used for the short-circuiting where client is in same process as service.
        /// Client can now directly dispatch request to service instead of using ServiceProxy.
        /// </summary>
        /// <param name="requestMessageDispatchHeaders">Request message headers</param>
        /// <param name="requestMessageBody">Request message body</param>
        /// <param name="cancellationToken">Cancellation token. It can be used to cancel the request</param>
        /// <returns>A <see cref="Task"/> representing the asynchronous operation. The result of the task is the response for the received request.</returns>
        public virtual Task<IServiceRemotingResponseMessageBody> HandleRequestResponseAsync(
            ServiceRemotingDispatchHeaders requestMessageDispatchHeaders,
            IServiceRemotingRequestMessageBody requestMessageBody,
            CancellationToken cancellationToken)
        {
            var header = this.CreateServiceRemotingRequestMessageHeader(requestMessageDispatchHeaders);
            return this.HandleRequestResponseAsync(header, requestMessageBody, cancellationToken);
        }

        /// <summary>
        /// Handles a message from the client that requires a response from the service.
        /// </summary>
        /// <param name="requestContext">Request context - contains additional information about the request</param>
        /// <param name="requestMessage">Request message</param>
        /// <returns>A <see cref="Task"/> representing the asynchronous operation. The result of the task is the response for the received request.</returns>
        public virtual async Task<IServiceRemotingResponseMessage> HandleRequestResponseAsync(
            IServiceRemotingRequestContext requestContext,
            IServiceRemotingRequestMessage requestMessage)
        {
            if (Helper.IsCancellationRequest(requestMessage.GetHeader()))
            {
                await
                    this.cancellationHelper.CancelRequestAsync(
                        requestMessage.GetHeader().InterfaceId,
                        requestMessage.GetHeader().MethodId,
                        requestMessage.GetHeader().InvocationId);
                return null;
            }
            else
            {
                var messageHeaders = requestMessage.GetHeader();
                var retval = await this.cancellationHelper.DispatchRequest<IServiceRemotingResponseMessage>(
                        messageHeaders.InterfaceId,
                        messageHeaders.MethodId,
                        messageHeaders.InvocationId,
                        cancellationToken => this.OnDispatch(
                            messageHeaders,
                            requestMessage.GetBody(),
                            cancellationToken));

                return retval;
            }
        }

        /// <summary>
        /// Handles a one way message from the client.
        /// </summary>
        /// <param name="requestMessage">Request message</param>
        public virtual void HandleOneWayMessage(IServiceRemotingRequestMessage requestMessage)
        {
            throw new NotImplementedException(
                string.Format(
                    CultureInfo.CurrentCulture,
                    SR.ErrorMethodNotImplemented,
                    this.GetType().Name,
                    "HandleOneWay"));
        }

        /// <summary>
        /// Gets the factory used for creating the remoting response message bodies.
        /// </summary>
        /// <returns>The factory used by this dispatcher for creating the remoting response message bodies.</returns>
        public IServiceRemotingMessageBodyFactory GetRemotingMessageBodyFactory()
        {
            return this.serviceRemotingMessageBodyFactory;
        }

        /// <inheritdoc />
        public void Dispose()
        {
            if (this.servicePerformanceCounterProvider != null)
            {
                this.servicePerformanceCounterProvider.Dispose();
            }
        }

        internal void Initialize(
        Guid partitionId,
        long replicaId,
        string traceId,
        object serviceImplementation,
        IEnumerable<Type> remotedInterfaces,
        bool nonServiceInterface,
        IServiceRemotingMessageBodyFactory serviceRemotingMessageBodyFactory)
        {
            this.serviceRemotingMessageBodyFactory = serviceRemotingMessageBodyFactory ?? new DataContractRemotingMessageFactory();

            this.cancellationHelper = new ServiceRemotingCancellationHelper(traceId);

            this.serviceImplementation = serviceImplementation;

            if (serviceImplementation != null)
            {
                var interfaceDescriptions = new List<ServiceInterfaceDescription>();
                foreach (var interfaceType in remotedInterfaces)
                {
                    if (nonServiceInterface)
                    {
                        interfaceDescriptions.Add(ServiceInterfaceDescription.CreateUsingCRCId(interfaceType, false));
                    }
                    else
                    {
                        interfaceDescriptions.Add(ServiceInterfaceDescription.CreateUsingCRCId(interfaceType, true));
                    }
                }

                this.servicePerformanceCounterProvider =
                    new ServicePerformanceCounterProvider(
                        partitionId,
                        replicaId,
                        interfaceDescriptions,
                        false);
            }
        }

        private Task<IServiceRemotingResponseMessage> OnDispatch(
            IServiceRemotingRequestMessageHeader requestMessageHeaders,
            IServiceRemotingRequestMessageBody requestBody,
            CancellationToken cancellationToken)
        {
            if (!this.methodDispatcherMap.TryGetValue(requestMessageHeaders.InterfaceId, out var methodDispatcher))
            {
                throw new NotImplementedException(string.Format(
                    CultureInfo.CurrentCulture,
                    SR.ErrorInterfaceNotImplemented,
                    requestMessageHeaders.InterfaceId,
                    this.serviceImplementation));
            }

            Task<IServiceRemotingResponseMessageBody> dispatchTask = null;
            var stopwatch = Stopwatch.StartNew();

            var requestMessage = new ServiceRemotingRequestMessage(requestMessageHeaders, requestBody);
            ServiceRemotingServiceEvents.RaiseReceiveRequest(requestMessage, methodDispatcher.GetMethodName(requestMessageHeaders.MethodId));

            try
            {
                dispatchTask = methodDispatcher.DispatchAsync(
                    this.serviceImplementation,
                    requestMessageHeaders.MethodId,
                    requestBody,
                    this.GetRemotingMessageBodyFactory(),
                    cancellationToken);
            }
            catch (Exception e)
            {
                // Suggestion:
                // In future, we should consider consolodating how service remoting handles exceptions (failed requests) and normal responses (successful requests)
                // My contention is that a request that fails also generates a response (albeit a special kind) that encapsulates an exception.
                // If an IServiceRemotingResponseMessage can encapsulate a response in either case - this allows us to use response headers in either case for communication (think http 4XX / 5XX responses have standard headers)
                // The proxy on the caller side can always deserialize the exception and throw it, so user experience won't have to change due to this suggested architectural change.
                ServiceRemotingServiceEvents.RaiseExceptionResponse(e, requestMessage);

                var info = ExceptionDispatchInfo.Capture(e);
                this.servicePerformanceCounterProvider.OnServiceMethodFinish(
                    requestMessageHeaders.InterfaceId,
                    requestMessageHeaders.MethodId,
                    stopwatch.Elapsed,
                    e);
                info.Throw();
            }

            return dispatchTask.ContinueWith(
                t =>
                {
                    object responseBody = null;
                    try
                    {
                        responseBody = t.GetAwaiter().GetResult();
                    }
                    catch (Exception e)
                    {
                        ServiceRemotingServiceEvents.RaiseExceptionResponse(e, requestMessage);

                        var info = ExceptionDispatchInfo.Capture(e);

                        this.servicePerformanceCounterProvider.OnServiceMethodFinish(
                            requestMessageHeaders.InterfaceId,
                            requestMessageHeaders.MethodId,
                            stopwatch.Elapsed,
                            e);
                        info.Throw();
                    }

                    this.servicePerformanceCounterProvider.OnServiceMethodFinish(
                        requestMessageHeaders.InterfaceId,
                        requestMessageHeaders.MethodId,
                        stopwatch.Elapsed);

                    // We are creating empty response headers so that ServiceRemotingServiceEvents can add headers if they needed.
                    // This wont impact serialization cost since we check if its Empty , then dont serialize.
                    var response = new ServiceRemotingResponseMessage(new ServiceRemotingResponseMessageHeader(), (IServiceRemotingResponseMessageBody)responseBody);
                    ServiceRemotingServiceEvents.RaiseSendResponse(response, requestMessage);

                    return (IServiceRemotingResponseMessage)response;
                },
                TaskContinuationOptions.ExecuteSynchronously);
        }

        private IServiceRemotingRequestMessageHeader CreateServiceRemotingRequestMessageHeader(
            ServiceRemotingDispatchHeaders serviceRemotingDispatchHeaders)
        {
            InterfaceDetails details = this.getKnownTypes(serviceRemotingDispatchHeaders.ServiceInterfaceName);
            if (details != null)
            {
                var headers = new ServiceRemotingRequestMessageHeader
                {
                    InterfaceId = details.Id,
                };

                if (details.MethodNames.TryGetValue(serviceRemotingDispatchHeaders.MethodName, out var headersMethodId))
                {
                    headers.MethodId = headersMethodId;
                }
                else
                {
                    throw new NotSupportedException("This Method is not Supported" +
                                                    serviceRemotingDispatchHeaders.MethodName);
                }

                return headers;
            }

            throw new NotSupportedException("This Interface is not Supported" +
                                            serviceRemotingDispatchHeaders.ServiceInterfaceName);
        }

        private async Task<IServiceRemotingResponseMessageBody> HandleRequestResponseAsync(
            IServiceRemotingRequestMessageHeader remotingRequestMessageHeader,
            IServiceRemotingRequestMessageBody requestMessageBody,
            CancellationToken cancellationToken)
        {
            var retval = await this.OnDispatch(
                remotingRequestMessageHeader,
                requestMessageBody,
                cancellationToken);

            if (retval != null)
            {
                return retval.GetBody();
            }

            return null;
        }
    }
}