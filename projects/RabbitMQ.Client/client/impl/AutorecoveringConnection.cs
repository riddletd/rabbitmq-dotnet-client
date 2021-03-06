// This source code is dual-licensed under the Apache License, version
// 2.0, and the Mozilla Public License, version 2.0.
//
// The APL v2.0:
//
//---------------------------------------------------------------------------
//   Copyright (c) 2007-2020 VMware, Inc.
//
//   Licensed under the Apache License, Version 2.0 (the "License");
//   you may not use this file except in compliance with the License.
//   You may obtain a copy of the License at
//
//       https://www.apache.org/licenses/LICENSE-2.0
//
//   Unless required by applicable law or agreed to in writing, software
//   distributed under the License is distributed on an "AS IS" BASIS,
//   WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
//   See the License for the specific language governing permissions and
//   limitations under the License.
//---------------------------------------------------------------------------
//
// The MPL v2.0:
//
//---------------------------------------------------------------------------
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.
//
//  Copyright (c) 2007-2020 VMware, Inc.  All rights reserved.
//---------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;

using RabbitMQ.Client.Events;
using RabbitMQ.Client.Exceptions;
using RabbitMQ.Client.Impl;
using RabbitMQ.Client.Logging;

namespace RabbitMQ.Client.Framing.Impl
{
    internal sealed partial class AutorecoveringConnection : IConnection
    {
        private bool _disposed;
        private Connection _delegate;
        private readonly ConnectionFactory _factory;

        // list of endpoints provided on initial connection.
        // on re-connection, the next host in the line is chosen using
        // IHostnameSelector
        private IEndpointResolver _endpoints;

        private readonly object _recordedEntitiesLock = new object();

        private readonly Dictionary<string, RecordedExchange> _recordedExchanges = new Dictionary<string, RecordedExchange>();
        private readonly Dictionary<string, RecordedQueue> _recordedQueues = new Dictionary<string, RecordedQueue>();
        private readonly Dictionary<RecordedBinding, byte> _recordedBindings = new Dictionary<RecordedBinding, byte>();
        private readonly Dictionary<string, RecordedConsumer> _recordedConsumers = new Dictionary<string, RecordedConsumer>();

        private readonly List<AutorecoveringModel> _models = new List<AutorecoveringModel>();

        public AutorecoveringConnection(ConnectionFactory factory, string clientProvidedName = null)
        {
            _factory = factory;
            ClientProvidedName = clientProvidedName;

            Action<Exception, string> onException = (exception, context) => _delegate.OnCallbackException(CallbackExceptionEventArgs.Build(exception, context));
            _recoverySucceededWrapper = new EventingWrapper<EventArgs>("OnConnectionRecovery", onException);
            _connectionRecoveryErrorWrapper = new EventingWrapper<ConnectionRecoveryErrorEventArgs>("OnConnectionRecoveryError", onException);
            _consumerTagChangeAfterRecoveryWrapper = new EventingWrapper<ConsumerTagChangedAfterRecoveryEventArgs>("OnConsumerRecovery", onException);
            _queueNameChangeAfterRecoveryWrapper = new EventingWrapper<QueueNameChangedAfterRecoveryEventArgs>("OnQueueRecovery", onException);
        }

        private Connection Delegate => !_disposed ? _delegate : throw new ObjectDisposedException(GetType().FullName);

        public event EventHandler<EventArgs> RecoverySucceeded
        {
            add => _recoverySucceededWrapper.AddHandler(value);
            remove => _recoverySucceededWrapper.RemoveHandler(value);
        }
        private EventingWrapper<EventArgs> _recoverySucceededWrapper;

        public event EventHandler<ConnectionRecoveryErrorEventArgs> ConnectionRecoveryError
        {
            add => _connectionRecoveryErrorWrapper.AddHandler(value);
            remove => _connectionRecoveryErrorWrapper.RemoveHandler(value);
        }
        private EventingWrapper<ConnectionRecoveryErrorEventArgs> _connectionRecoveryErrorWrapper;

        public event EventHandler<CallbackExceptionEventArgs> CallbackException
        {
            add => Delegate.CallbackException += value;
            remove => Delegate.CallbackException -= value;
        }

        public event EventHandler<ConnectionBlockedEventArgs> ConnectionBlocked
        {
            add => Delegate.ConnectionBlocked += value;
            remove => Delegate.ConnectionBlocked -= value;
        }

        public event EventHandler<ShutdownEventArgs> ConnectionShutdown
        {
            add => Delegate.ConnectionShutdown += value;
            remove => Delegate.ConnectionShutdown -= value;
        }

        public event EventHandler<EventArgs> ConnectionUnblocked
        {
            add => Delegate.ConnectionUnblocked += value;
            remove => Delegate.ConnectionUnblocked -= value;
        }

        public event EventHandler<ConsumerTagChangedAfterRecoveryEventArgs> ConsumerTagChangeAfterRecovery
        {
            add => _consumerTagChangeAfterRecoveryWrapper.AddHandler(value);
            remove => _consumerTagChangeAfterRecoveryWrapper.RemoveHandler(value);
        }
        private EventingWrapper<ConsumerTagChangedAfterRecoveryEventArgs> _consumerTagChangeAfterRecoveryWrapper;

        public event EventHandler<QueueNameChangedAfterRecoveryEventArgs> QueueNameChangeAfterRecovery
        {
            add => _queueNameChangeAfterRecoveryWrapper.AddHandler(value);
            remove => _queueNameChangeAfterRecoveryWrapper.RemoveHandler(value);
        }
        private EventingWrapper<QueueNameChangedAfterRecoveryEventArgs> _queueNameChangeAfterRecoveryWrapper;

        public string ClientProvidedName { get; }

        public ushort ChannelMax => Delegate.ChannelMax;

        public IDictionary<string, object> ClientProperties => Delegate.ClientProperties;

        public ShutdownEventArgs CloseReason => Delegate.CloseReason;

        public AmqpTcpEndpoint Endpoint => Delegate.Endpoint;

        public uint FrameMax => Delegate.FrameMax;

        public TimeSpan Heartbeat => Delegate.Heartbeat;

        public bool IsOpen => _delegate?.IsOpen ?? false;

        public AmqpTcpEndpoint[] KnownHosts
        {
            get => Delegate.KnownHosts;
            set => Delegate.KnownHosts = value;
        }

        public int LocalPort => Delegate.LocalPort;

        public ProtocolBase Protocol => Delegate.Protocol;

        public int RemotePort => Delegate.RemotePort;

        public IDictionary<string, object> ServerProperties => Delegate.ServerProperties;

        public IList<ShutdownReportEntry> ShutdownReport => Delegate.ShutdownReport;

        IProtocol IConnection.Protocol => Endpoint.Protocol;

        private bool TryPerformAutomaticRecovery()
        {
            ESLog.Info("Performing automatic recovery");

            try
            {
                if (TryRecoverConnectionDelegate())
                {
                    RecoverModels();
                    if (_factory.TopologyRecoveryEnabled)
                    {
                        RecoverEntities();
                        RecoverConsumers();
                    }

                    ESLog.Info("Connection recovery completed");
                    ThrowIfDisposed();
                    _recoverySucceededWrapper.Invoke(this, EventArgs.Empty);

                    return true;
                }

                ESLog.Warn("Connection delegate was manually closed. Aborted recovery.");
            }
            catch (Exception e)
            {
                ESLog.Error("Exception when recovering connection. Will try again after retry interval.", e);
            }

            return false;
        }

        public void Close(ShutdownEventArgs reason) => Delegate.Close(reason);

        public RecoveryAwareModel CreateNonRecoveringModel()
        {
            ISession session = Delegate.CreateSession();
            var result = new RecoveryAwareModel(session) { ContinuationTimeout = _factory.ContinuationTimeout };
            result._Private_ChannelOpen("");
            return result;
        }

        public void DeleteRecordedBinding(RecordedBinding rb)
        {
            lock (_recordedEntitiesLock)
            {
                _recordedBindings.Remove(rb);
            }
        }

        public RecordedConsumer DeleteRecordedConsumer(string consumerTag)
        {
            RecordedConsumer rc;
            lock (_recordedEntitiesLock)
            {
                if (_recordedConsumers.TryGetValue(consumerTag, out rc))
                {
                    _recordedConsumers.Remove(consumerTag);
                }
            }

            return rc;
        }

        public void DeleteRecordedExchange(string name)
        {
            lock (_recordedEntitiesLock)
            {
                _recordedExchanges.Remove(name);

                // find bindings that need removal, check if some auto-delete exchanges
                // might need the same
                IEnumerable<RecordedBinding> bs = _recordedBindings.Keys.Where(b => name.Equals(b.Destination)).ToArray();
                foreach (RecordedBinding b in bs)
                {
                    DeleteRecordedBinding(b);
                    MaybeDeleteRecordedAutoDeleteExchange(b.Source);
                }
            }
        }

        public void DeleteRecordedQueue(string name)
        {
            lock (_recordedEntitiesLock)
            {
                _recordedQueues.Remove(name);
                // find bindings that need removal, check if some auto-delete exchanges
                // might need the same
                IEnumerable<RecordedBinding> bs = _recordedBindings.Keys.Where(b => name.Equals(b.Destination)).ToArray();
                foreach (RecordedBinding b in bs)
                {
                    DeleteRecordedBinding(b);
                    MaybeDeleteRecordedAutoDeleteExchange(b.Source);
                }
            }
        }

        public bool HasMoreConsumersOnQueue(ICollection<RecordedConsumer> consumers, string queue)
        {
            var cs = new List<RecordedConsumer>(consumers);
            return cs.Exists(c => c.Queue.Equals(queue));
        }

        public bool HasMoreDestinationsBoundToExchange(ICollection<RecordedBinding> bindings, string exchange)
        {
            var bs = new List<RecordedBinding>(bindings);
            return bs.Exists(b => b.Source.Equals(exchange));
        }

        public void MaybeDeleteRecordedAutoDeleteExchange(string exchange)
        {
            lock (_recordedEntitiesLock)
            {
                if (!HasMoreDestinationsBoundToExchange(_recordedBindings.Keys, exchange))
                {
                    _recordedExchanges.TryGetValue(exchange, out RecordedExchange rx);
                    // last binding where this exchange is the source is gone,
                    // remove recorded exchange
                    // if it is auto-deleted. See bug 26364.
                    if ((rx != null) && rx.IsAutoDelete)
                    {
                        _recordedExchanges.Remove(exchange);
                    }
                }
            }
        }

        public void MaybeDeleteRecordedAutoDeleteQueue(string queue)
        {
            lock (_recordedEntitiesLock)
            {
                if (!HasMoreConsumersOnQueue(_recordedConsumers.Values, queue))
                {
                    _recordedQueues.TryGetValue(queue, out RecordedQueue rq);
                    // last consumer on this connection is gone, remove recorded queue
                    // if it is auto-deleted. See bug 26364.
                    if ((rq != null) && rq.IsAutoDelete)
                    {
                        _recordedQueues.Remove(queue);
                    }
                }
            }
        }

        public void RecordBinding(RecordedBinding rb)
        {
            lock (_recordedEntitiesLock)
            {
                if (!_recordedBindings.ContainsKey(rb))
                {
                    _recordedBindings.Add(rb, 0);
                }
            }
        }

        public void RecordConsumer(string name, RecordedConsumer c)
        {
            lock (_recordedEntitiesLock)
            {
                if (!_recordedConsumers.ContainsKey(name))
                {
                    _recordedConsumers.Add(name, c);
                }
            }
        }

        public void RecordExchange(string name, RecordedExchange x)
        {
            lock (_recordedEntitiesLock)
            {
                _recordedExchanges[name] = x;
            }
        }

        public void RecordQueue(string name, RecordedQueue q)
        {
            lock (_recordedEntitiesLock)
            {
                _recordedQueues[name] = q;
            }
        }

        public override string ToString() => $"AutorecoveringConnection({Delegate.Id},{Endpoint},{GetHashCode()})";

        public void UnregisterModel(AutorecoveringModel model)
        {
            lock (_models)
            {
                _models.Remove(model);
            }
        }

        public void Init() => Init(_factory.EndpointResolverFactory(new List<AmqpTcpEndpoint> { _factory.Endpoint }));

        public void Init(IEndpointResolver endpoints)
        {
            _endpoints = endpoints;
            IFrameHandler fh = endpoints.SelectOne(_factory.CreateFrameHandler);
            Init(fh);
        }

        private void Init(IFrameHandler fh)
        {
            ThrowIfDisposed();
            _delegate = new Connection(_factory, false, fh, ClientProvidedName);
            ConnectionShutdown += HandleConnectionShutdown;
        }

        ///<summary>API-side invocation of updating the secret.</summary>
        public void UpdateSecret(string newSecret, string reason)
        {
            ThrowIfDisposed();
            EnsureIsOpen();
            _delegate.UpdateSecret(newSecret, reason);
            _factory.Password = newSecret;
        }

        ///<summary>API-side invocation of connection abort.</summary>
        public void Abort()
        {
            ThrowIfDisposed();
            StopRecoveryLoop();
            if (_delegate.IsOpen)
            {
                _delegate.Abort();
            }
        }

        ///<summary>API-side invocation of connection abort.</summary>
        public void Abort(ushort reasonCode, string reasonText)
        {
            ThrowIfDisposed();
            StopRecoveryLoop();
            if (_delegate.IsOpen)
            {
                _delegate.Abort(reasonCode, reasonText);
            }
        }

        ///<summary>API-side invocation of connection abort with timeout.</summary>
        public void Abort(TimeSpan timeout)
        {
            ThrowIfDisposed();
            StopRecoveryLoop();
            if (_delegate.IsOpen)
            {
                _delegate.Abort(timeout);
            }
        }

        ///<summary>API-side invocation of connection abort with timeout.</summary>
        public void Abort(ushort reasonCode, string reasonText, TimeSpan timeout)
        {
            ThrowIfDisposed();
            StopRecoveryLoop();
            if (_delegate.IsOpen)
            {
                _delegate.Abort(reasonCode, reasonText, timeout);
            }
        }

        ///<summary>API-side invocation of connection.close.</summary>
        public void Close()
        {
            ThrowIfDisposed();
            StopRecoveryLoop();
            if (_delegate.IsOpen)
            {
                _delegate.Close();
            }
        }

        ///<summary>API-side invocation of connection.close.</summary>
        public void Close(ushort reasonCode, string reasonText)
        {
            ThrowIfDisposed();
            StopRecoveryLoop();
            if (_delegate.IsOpen)
            {
                _delegate.Close(reasonCode, reasonText);
            }
        }

        ///<summary>API-side invocation of connection.close with timeout.</summary>
        public void Close(TimeSpan timeout)
        {
            ThrowIfDisposed();
            StopRecoveryLoop();
            if (_delegate.IsOpen)
            {
                _delegate.Close(timeout);
            }
        }

        ///<summary>API-side invocation of connection.close with timeout.</summary>
        public void Close(ushort reasonCode, string reasonText, TimeSpan timeout)
        {
            ThrowIfDisposed();
            StopRecoveryLoop();
            if (_delegate.IsOpen)
            {
                _delegate.Close(reasonCode, reasonText, timeout);
            }
        }

        public IModel CreateModel()
        {
            EnsureIsOpen();
            AutorecoveringModel m = new AutorecoveringModel(this, CreateNonRecoveringModel());
            lock (_models)
            {
                _models.Add(m);
            }
            return m;
        }

        void IDisposable.Dispose() => Dispose(true);

        public void HandleConnectionBlocked(string reason) => Delegate.HandleConnectionBlocked(reason);

        public void HandleConnectionUnblocked() => Delegate.HandleConnectionUnblocked();

        internal int RecordedExchangesCount
        {
            get
            {
                lock (_recordedEntitiesLock)
                {
                    return _recordedExchanges.Count;
                }
            }
        }

        internal int RecordedQueuesCount
        {
            get
            {
                lock (_recordedEntitiesLock)
                {
                    return _recordedExchanges.Count;
                }
            }
        }

        private void Dispose(bool disposing)
        {
            if (_disposed)
            {
                return;
            }

            if (disposing)
            {
                try
                {
                    Abort();
                }
                catch (Exception)
                {
                    // TODO: log
                }
                finally
                {
                    _models.Clear();
                    _delegate = null;
                    _disposed = true;
                }
            }
        }

        private void EnsureIsOpen() => Delegate.EnsureIsOpen();

        private void HandleTopologyRecoveryException(TopologyRecoveryException e) => ESLog.Error("Topology recovery exception", e);

        private void PropagateQueueNameChangeToBindings(string oldName, string newName)
        {
            lock (_recordedBindings)
            {
                foreach (RecordedBinding b in _recordedBindings.Keys)
                {
                    if (b.Destination.Equals(oldName))
                    {
                        b.Destination = newName;
                    }
                }
            }
        }

        private void PropagateQueueNameChangeToConsumers(string oldName, string newName)
        {
            lock (_recordedConsumers)
            {
                foreach (KeyValuePair<string, RecordedConsumer> c in _recordedConsumers)
                {
                    if (c.Value.Queue.Equals(oldName))
                    {
                        c.Value.Queue = newName;
                    }
                }
            }
        }

        private void RecoverBindings()
        {
            Dictionary<RecordedBinding, byte> recordedBindingsCopy;
            lock (_recordedBindings)
            {
                recordedBindingsCopy = new Dictionary<RecordedBinding, byte>(_recordedBindings);
            }

            foreach (RecordedBinding b in recordedBindingsCopy.Keys)
            {
                try
                {
                    b.Recover();
                }
                catch (Exception cause)
                {
                    string s = string.Format("Caught an exception while recovering binding between {0} and {1}: {2}",
                        b.Source, b.Destination, cause.Message);
                    HandleTopologyRecoveryException(new TopologyRecoveryException(s, cause));
                }
            }
        }

        private bool TryRecoverConnectionDelegate()
        {
            ThrowIfDisposed();
            try
            {
                IFrameHandler fh = _endpoints.SelectOne(_factory.CreateFrameHandler);
                var defunctConnection = _delegate;
                _delegate = new Connection(_factory, false, fh, ClientProvidedName);
                _delegate.TakeOver(defunctConnection);
                return true;
            }
            catch (Exception e)
            {
                ESLog.Error("Connection recovery exception.", e);
                // Trigger recovery error events
                if (!_connectionRecoveryErrorWrapper.IsEmpty)
                {
                    _connectionRecoveryErrorWrapper.Invoke(this, new ConnectionRecoveryErrorEventArgs(e));
                }
            }

            return false;
        }

        private void RecoverConsumers()
        {
            ThrowIfDisposed();
            Dictionary<string, RecordedConsumer> recordedConsumersCopy;
            lock (_recordedConsumers)
            {
                recordedConsumersCopy = new Dictionary<string, RecordedConsumer>(_recordedConsumers);
            }

            foreach (KeyValuePair<string, RecordedConsumer> pair in recordedConsumersCopy)
            {
                string tag = pair.Key;
                RecordedConsumer cons = pair.Value;

                try
                {
                    string newTag = cons.Recover();
                    lock (_recordedConsumers)
                    {
                        // make sure server-generated tags are re-added
                        _recordedConsumers.Remove(tag);
                        _recordedConsumers.Add(newTag, cons);
                    }

                    if (!_consumerTagChangeAfterRecoveryWrapper.IsEmpty)
                    {
                        _consumerTagChangeAfterRecoveryWrapper.Invoke(this, new ConsumerTagChangedAfterRecoveryEventArgs(tag, newTag));
                    }
                }
                catch (Exception cause)
                {
                    string s = string.Format("Caught an exception while recovering consumer {0} on queue {1}: {2}",
                        tag, cons.Queue, cause.Message);
                    HandleTopologyRecoveryException(new TopologyRecoveryException(s, cause));
                }
            }
        }

        private void RecoverEntities()
        {
            // The recovery sequence is the following:
            //
            // 1. Recover exchanges
            // 2. Recover queues
            // 3. Recover bindings
            // 4. Recover consumers
            RecoverExchanges();
            RecoverQueues();
            RecoverBindings();
        }

        private void RecoverExchanges()
        {
            Dictionary<string, RecordedExchange> recordedExchangesCopy;
            lock (_recordedEntitiesLock)
            {
                recordedExchangesCopy = new Dictionary<string, RecordedExchange>(_recordedExchanges);
            }

            foreach (RecordedExchange rx in recordedExchangesCopy.Values)
            {
                try
                {
                    rx.Recover();
                }
                catch (Exception cause)
                {
                    string s = string.Format("Caught an exception while recovering exchange {0}: {1}",
                        rx.Name, cause.Message);
                    HandleTopologyRecoveryException(new TopologyRecoveryException(s, cause));
                }
            }
        }

        private void RecoverModels()
        {
            lock (_models)
            {
                foreach (AutorecoveringModel m in _models)
                {
                    m.AutomaticallyRecover(this);
                }
            }
        }

        private void RecoverQueues()
        {
            Dictionary<string, RecordedQueue> recordedQueuesCopy;
            lock (_recordedEntitiesLock)
            {
                recordedQueuesCopy = new Dictionary<string, RecordedQueue>(_recordedQueues);
            }

            foreach (KeyValuePair<string, RecordedQueue> pair in recordedQueuesCopy)
            {
                string oldName = pair.Key;
                RecordedQueue rq = pair.Value;

                try
                {
                    rq.Recover();
                    string newName = rq.Name;

                    if (!oldName.Equals(newName))
                    {
                        // Make sure server-named queues are re-added with
                        // their new names.
                        // We only remove old name after we've updated the bindings and consumers,
                        // plus only for server-named queues, both to make sure we don't lose
                        // anything to recover. MK.
                        PropagateQueueNameChangeToBindings(oldName, newName);
                        PropagateQueueNameChangeToConsumers(oldName, newName);
                        // see rabbitmq/rabbitmq-dotnet-client#43
                        if (rq.IsServerNamed)
                        {
                            DeleteRecordedQueue(oldName);
                        }
                        RecordQueue(newName, rq);

                        if (!_queueNameChangeAfterRecoveryWrapper.IsEmpty)
                        {
                            _queueNameChangeAfterRecoveryWrapper.Invoke(this, new QueueNameChangedAfterRecoveryEventArgs(oldName, newName));
                        }
                    }
                }
                catch (Exception cause)
                {
                    string s = string.Format("Caught an exception while recovering queue {0}: {1}",
                        oldName, cause.Message);
                    HandleTopologyRecoveryException(new TopologyRecoveryException(s, cause));
                }
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void ThrowIfDisposed()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(GetType().FullName);
            }
        }
    }
}
