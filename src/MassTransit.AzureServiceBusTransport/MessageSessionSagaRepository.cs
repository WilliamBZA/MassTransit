﻿// Copyright 2007-2015 Chris Patterson, Dru Sellers, Travis Smith, et. al.
//  
// Licensed under the Apache License, Version 2.0 (the "License"); you may not use
// this file except in compliance with the License. You may obtain a copy of the 
// License at 
// 
//     http://www.apache.org/licenses/LICENSE-2.0 
// 
// Unless required by applicable law or agreed to in writing, software distributed
// under the License is distributed on an "AS IS" BASIS, WITHOUT WARRANTIES OR 
// CONDITIONS OF ANY KIND, either express or implied. See the License for the 
// specific language governing permissions and limitations under the License.
namespace MassTransit.AzureServiceBusTransport
{
    using System;
    using System.IO;
    using System.Threading.Tasks;
    using Context;
    using Logging;
    using MassTransit.Pipeline;
    using Newtonsoft.Json.Bson;
    using Saga;
    using Serialization;
    using Util;


    /// <summary>
    /// A saga repository that uses the message session in Azure Service Bus to store the state 
    /// of the saga.
    /// </summary>
    /// <typeparam name="TSaga">The saga state type</typeparam>
    public class MessageSessionSagaRepository<TSaga> :
        ISagaRepository<TSaga>
        where TSaga : class, ISaga
    {
        static readonly ILog _log = Logger.Get(typeof(MessageSessionSagaRepository<TSaga>));

        public void Probe(ProbeContext context)
        {
            var scope = context.CreateScope("sagaRepository");
            scope.Set(new
            {
                Persistence = "messageSession"
            });
        }

        async Task ISagaRepository<TSaga>.Send<T>(ConsumeContext<T> context, ISagaPolicy<TSaga, T> policy, IPipe<SagaConsumeContext<TSaga, T>> next)
        {
            MessageSessionContext sessionContext;
            if (!context.TryGetPayload(out sessionContext))
            {
                throw new SagaException($"The session-based saga repository requires an active message session: {TypeMetadataCache<TSaga>.ShortName}",
                    typeof(TSaga), typeof(T));
            }

            Guid sessionId;
            if (Guid.TryParse(sessionContext.SessionId, out sessionId))
            {
                context = new CorrelationIdConsumeContextProxy<T>(context, sessionId);
            }

            var saga = await ReadSagaState(sessionContext);
            if (saga == null)
            {
                var missingSagaPipe = new MissingPipe<T>(next, WriteSagaState);

                await policy.Missing(context, missingSagaPipe);
            }
            else
            {
                SagaConsumeContext<TSaga, T> sagaConsumeContext = new MessageSessionSagaConsumeContext<TSaga, T>(context, sessionContext, saga);

                if (_log.IsDebugEnabled)
                {
                    _log.DebugFormat("SAGA:{0}:{1} Existing {2}", TypeMetadataCache<TSaga>.ShortName, sessionContext.SessionId, TypeMetadataCache<T>.ShortName);
                }

                await policy.Existing(sagaConsumeContext, next);

                if (!sagaConsumeContext.IsCompleted)
                {
                    await WriteSagaState(sessionContext, saga);

                    if (_log.IsDebugEnabled)
                    {
                        _log.DebugFormat("SAGA:{0}:{1} Updated {2}", TypeMetadataCache<TSaga>.ShortName, sessionContext.SessionId,
                            TypeMetadataCache<T>.ShortName);
                    }
                }
            }
        }

        Task ISagaRepository<TSaga>.SendQuery<T>(SagaQueryConsumeContext<TSaga, T> context, ISagaPolicy<TSaga, T> policy,
            IPipe<SagaConsumeContext<TSaga, T>> next)
        {
            throw new NotImplementedException(
                $"Query-based saga correlation is not available when using the MessageSession-based saga repository: {TypeMetadataCache<TSaga>.ShortName}");
        }

        /// <summary>
        /// Writes the saga state to the message session
        /// </summary>
        /// <param name="context">The message session context</param>
        /// <param name="saga">The saga state</param>
        /// <returns>An awaitable task, of course</returns>
        async Task WriteSagaState(MessageSessionContext context, TSaga saga)
        {
            using (var serializeStream = new MemoryStream())
            using (var bsonWriter = new BsonWriter(serializeStream))
            {
                BsonMessageSerializer.Serializer.Serialize(bsonWriter, saga);

                bsonWriter.Flush();
                serializeStream.Flush();

                using (var stateStream = new MemoryStream(serializeStream.ToArray(), false))
                {
                    await context.SetStateAsync(stateStream);
                }
            }
        }

        async Task<TSaga> ReadSagaState(MessageSessionContext context)
        {
            try
            {
                using (var stateStream = await context.GetStateAsync())
                {
                    if (stateStream == null || stateStream.Length == 0)
                        return default(TSaga);

                    using (var bsonReader = new BsonReader(stateStream))
                    {
                        return BsonMessageSerializer.Deserializer.Deserialize<TSaga>(bsonReader);
                    }
                }
            }
            catch (NotImplementedException exception)
            {
                throw new ConfigurationException("NetMessaging must be used for session-based sagas", exception);
            }
        }


        /// <summary>
        /// Once the message pipe has processed the saga instance, add it to the saga repository
        /// </summary>
        /// <typeparam name="TMessage"></typeparam>
        class MissingPipe<TMessage> :
            IPipe<SagaConsumeContext<TSaga, TMessage>>
            where TMessage : class
        {
            readonly IPipe<SagaConsumeContext<TSaga, TMessage>> _next;
            readonly Func<MessageSessionContext, TSaga, Task> _writeSagaState;

            public MissingPipe(IPipe<SagaConsumeContext<TSaga, TMessage>> next, Func<MessageSessionContext, TSaga, Task> writeSagaState)
            {
                _next = next;
                _writeSagaState = writeSagaState;
            }

            void IProbeSite.Probe(ProbeContext context)
            {
                _next.Probe(context);
            }

            public async Task Send(SagaConsumeContext<TSaga, TMessage> context)
            {
                var sessionContext = context.GetPayload<MessageSessionContext>();

                var proxy = new MessageSessionSagaConsumeContext<TSaga, TMessage>(context, sessionContext, context.Saga);

                if (_log.IsDebugEnabled)
                {
                    _log.DebugFormat("SAGA:{0}:{1} Created {2}", TypeMetadataCache<TSaga>.ShortName, sessionContext.SessionId,
                        TypeMetadataCache<TMessage>.ShortName);
                }

                try
                {
                    await _next.Send(proxy);

                    if (!proxy.IsCompleted)
                    {
                        await _writeSagaState(sessionContext, proxy.Saga);
                        if (_log.IsDebugEnabled)
                        {
                            _log.DebugFormat("SAGA:{0}:{1} Saved {2}", TypeMetadataCache<TSaga>.ShortName, sessionContext.SessionId,
                                TypeMetadataCache<TMessage>.ShortName);
                        }
                    }
                }
                catch (Exception)
                {
                    if (_log.IsDebugEnabled)
                    {
                        _log.DebugFormat("SAGA:{0}:{1} Unsaved(Fault) {2}", TypeMetadataCache<TSaga>.ShortName, sessionContext.SessionId,
                            TypeMetadataCache<TMessage>.ShortName);
                    }

                    throw;
                }
            }
        }
    }
}