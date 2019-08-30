﻿using DotPulsar.Abstractions;
using DotPulsar.Exceptions;
using DotPulsar.Internal.Abstractions;
using DotPulsar.Internal.PulsarApi;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace DotPulsar.Internal
{
    public sealed class Consumer : IConsumer
    {
        private readonly Executor _executor;
        private readonly CommandAck _cachedCommandAck;
        private readonly IConsumerStreamFactory _streamFactory;
        private readonly IFaultStrategy _faultStrategy;
        private readonly bool _setProxyState;
        private readonly StateManager<ConsumerState> _stateManager;
        private readonly CancellationTokenSource _connectTokenSource;
        private readonly Task _connectTask;
        private Action _throwIfClosedOrFaulted;
        private IConsumerStream Stream { get; set; }

        public Consumer(IConsumerStreamFactory streamFactory, IFaultStrategy faultStrategy, bool setProxyState)
        {
            _executor = new Executor(ExecutorOnException);
            _cachedCommandAck = new CommandAck();
            _stateManager = new StateManager<ConsumerState>(ConsumerState.Disconnected, ConsumerState.Closed, ConsumerState.ReachedEndOfTopic, ConsumerState.Faulted);
            _streamFactory = streamFactory;
            _faultStrategy = faultStrategy;
            _setProxyState = setProxyState;
            _connectTokenSource = new CancellationTokenSource();
            Stream = new NotReadyStream();
            _connectTask = Connect(_connectTokenSource.Token);
            _throwIfClosedOrFaulted = () => { };
        }

        public async Task<ConsumerState> StateChangedTo(ConsumerState state, CancellationToken cancellationToken) => await _stateManager.StateChangedTo(state, cancellationToken);
        public async Task<ConsumerState> StateChangedFrom(ConsumerState state, CancellationToken cancellationToken) => await _stateManager.StateChangedFrom(state, cancellationToken);
        public bool IsFinalState() => _stateManager.IsFinalState();
        public bool IsFinalState(ConsumerState state) => _stateManager.IsFinalState(state);

        public void Dispose()
        {
            _executor.Dispose();
            _connectTokenSource.Cancel();
            _connectTask.Wait();
        }

        public async Task<Message> Receive(CancellationToken cancellationToken) => await _executor.Execute(() => Stream.Receive(cancellationToken), cancellationToken);

        public async Task Acknowledge(Message message, CancellationToken cancellationToken)
            => await Acknowledge(message.MessageId.Data, CommandAck.AckType.Individual, cancellationToken);

        public async Task Acknowledge(MessageId messageId, CancellationToken cancellationToken)
            => await Acknowledge(messageId.Data, CommandAck.AckType.Individual, cancellationToken);

        public async Task AcknowledgeCumulative(Message message, CancellationToken cancellationToken)
            => await Acknowledge(message.MessageId.Data, CommandAck.AckType.Cumulative, cancellationToken);

        public async Task AcknowledgeCumulative(MessageId messageId, CancellationToken cancellationToken)
            => await Acknowledge(messageId.Data, CommandAck.AckType.Cumulative, cancellationToken);

        public async Task Unsubscribe(CancellationToken cancellationToken)
        {
            _ = await _executor.Execute(() => Stream.Send(new CommandUnsubscribe()), cancellationToken);
            HasClosed();
        }

        public async Task Seek(MessageId messageId, CancellationToken cancellationToken)
        {
            var seek = new CommandSeek { MessageId = messageId.Data };
            _ = await _executor.Execute(() => Stream.Send(seek), cancellationToken);
            return;
        }

        public async Task<MessageId> GetLastMessageId(CancellationToken cancellationToken)
        {
            var response = await _executor.Execute(() => Stream.Send(new CommandGetLastMessageId()), cancellationToken);
            return new MessageId(response.LastMessageId);
        }

        private async Task Acknowledge(MessageIdData messageIdData, CommandAck.AckType ackType, CancellationToken cancellationToken)
        {
            await _executor.Execute(() =>
            {
                _cachedCommandAck.Type = ackType;
                _cachedCommandAck.MessageIds.Clear();
                _cachedCommandAck.MessageIds.Add(messageIdData);
                return Stream.Send(_cachedCommandAck);
            }, cancellationToken);
        }

        private async Task ExecutorOnException(Exception exception, CancellationToken cancellationToken)
        {
            _throwIfClosedOrFaulted();

            switch (_faultStrategy.DetermineFaultAction(exception))
            {
                case FaultAction.Retry:
                    await Task.Delay(_faultStrategy.RetryInterval, cancellationToken);
                    break;
                case FaultAction.Relookup:
                    await _stateManager.StateChangedFrom(ConsumerState.Disconnected, cancellationToken);
                    break;
                case FaultAction.Fault:
                    HasFaulted(exception);
                    break;
            }

            _throwIfClosedOrFaulted();
        }

        private void HasFaulted(Exception exception)
        {
            _throwIfClosedOrFaulted = () => throw exception;
            _stateManager.SetState(ConsumerState.Faulted);
        }

        private void HasClosed()
        {
            _throwIfClosedOrFaulted = () => throw new ConsumerClosedException();
            _stateManager.SetState(ConsumerState.Closed);
        }

        private async Task Connect(CancellationToken cancellationToken)
        {
            try
            {
                while (true)
                {
                    using (var proxy = new ConsumerProxy(_stateManager, new AsyncQueue<MessagePackage>()))
                    using (Stream = await _streamFactory.CreateStream(proxy, cancellationToken))
                    {
                        if (_setProxyState)
                            proxy.Active();
                        else
                            await _stateManager.StateChangedFrom(ConsumerState.Disconnected, cancellationToken);

                        await _stateManager.StateChangedTo(ConsumerState.Disconnected, cancellationToken);
                        if (_stateManager.IsFinalState())
                            return;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                HasClosed();
            }
            catch (Exception exception)
            {
                HasFaulted(exception);
            }
        }
    }
}
