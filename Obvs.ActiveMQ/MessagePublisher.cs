using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reactive.Concurrency;
using System.Reactive.Disposables;
using System.Threading.Tasks;
using Apache.NMS;
using Apache.NMS.ActiveMQ.Commands;
using Obvs.ActiveMQ.Extensions;
using Obvs.MessageProperties;
using Obvs.Serialization;

namespace Obvs.ActiveMQ
{
    public class MessagePublisher<TMessage> : IMessagePublisher<TMessage>
        where TMessage : class
    {
        private const int DrainTimeoutSecs = 2;

        private readonly Lazy<IConnection> _connection;
        private readonly IDestination _destination;
        private readonly IMessageSerializer _serializer;
        private readonly Func<TMessage, Dictionary<string, object>> _propertyProvider;
        private readonly Func<TMessage, MsgDeliveryMode> _deliveryMode;
        private readonly Func<TMessage, MsgPriority> _priority;
        private readonly Func<TMessage, TimeSpan> _timeToLive;
        private readonly Func<TMessage, Task> _publishMethod;
        private readonly Func<Task> _drainMethod;
        private readonly object _gate = new object();

        private ISession _session;
        private IMessageProducer _producer;
        private volatile IDisposable _disposable;
        private volatile bool _disposed;

        protected MessagePublisher(
            Lazy<IConnection> lazyConnection,
            IDestination destination,
            IMessageSerializer serializer,
            Func<TMessage, Dictionary<string, object>> propertyProvider,
            Func<TMessage, MsgDeliveryMode> deliveryMode = null,
            Func<TMessage, MsgPriority> priority = null,
            Func<TMessage, TimeSpan> timeToLive = null)
        {
            _connection = lazyConnection;
            _destination = destination;
            _serializer = serializer;
            _propertyProvider = propertyProvider ?? (message => new Dictionary<string, object>()) ;
            _deliveryMode = deliveryMode ?? (message => MsgDeliveryMode.NonPersistent);
            _priority = priority ?? (message => MsgPriority.Normal);
            _timeToLive = timeToLive ?? (message => TimeSpan.Zero);
        }

        public MessagePublisher(Lazy<IConnection> lazyConnection,
            IDestination destination,
            IMessageSerializer serializer,
            Func<TMessage, Dictionary<string, object>> propertyProvider,
            IScheduler scheduler,
            Func<TMessage, MsgDeliveryMode> deliveryMode = null,
            Func<TMessage, MsgPriority> priority = null,
            Func<TMessage, TimeSpan> timeToLive = null)
            : this(lazyConnection, destination, serializer, propertyProvider, deliveryMode, priority, timeToLive)
        {
            _publishMethod = message => scheduler.ScheduleAsync(() => Publish(message));
            _drainMethod = () => scheduler.ScheduleAsync(() => { });
        }

        public MessagePublisher(
            Lazy<IConnection> lazyConnection,
            IDestination destination,
            IMessageSerializer serializer,
            Func<TMessage, Dictionary<string, object>> propertyProvider,
            TaskScheduler taskScheduler,
            Func<TMessage, MsgDeliveryMode> deliveryMode = null,
            Func<TMessage, MsgPriority> priority = null,
            Func<TMessage, TimeSpan> timeToLive = null)
            : this(lazyConnection, destination, serializer, propertyProvider, deliveryMode, priority, timeToLive)
        {
            var taskFactory = new TaskFactory(taskScheduler);
            _publishMethod = message => taskFactory.StartNew(() => Publish(message));
            _drainMethod = () => taskFactory.StartNew(() => { });
        }

        public Task PublishAsync(TMessage message)
        {
            if (_disposed)
            {
                throw new InvalidOperationException("MessagePublisher has been disposed already.");
            }

            return _publishMethod(message);
        }

        private void Publish(TMessage message)
        {
            Publish(message, _propertyProvider(message) ?? new Dictionary<string, object>());
        }

        private void Publish(TMessage message, Dictionary<string, object> properties)
        {
            if (_disposed)
            {
                return;
            }

            Connect();

            AppendTypeNameProperty(message, properties);

            var msg = GenerateMessage(message, _producer, _serializer);

            msg.Properties.AddProperties(properties);

            _producer.Send(msg, _deliveryMode(message), _priority(message), _timeToLive(message));
        }
        
        protected virtual IMessage GenerateMessage(TMessage message, IMessageProducer producer, IMessageSerializer serializer)
        {
            // Instead of creating a bytes message, it seems more efficient to create a ActiveMQMessage and edit directly.
            var activeMQMessage = (ActiveMQMessage)producer.CreateMessage();

            using (var stream = StreamManager.Instance.GetStream())
            {
                var offset = stream.Position;

                serializer.Serialize(stream, message);

                var count = (int) (stream.Position - offset);
                var content = new byte[count];

                Array.Copy(stream.GetBuffer(), offset, content, 0L, count);

                activeMQMessage.Content = content;
            }

            return activeMQMessage;
        }

        private static void AppendTypeNameProperty(TMessage message, Dictionary<string, object> properties)
        {
            try
            {
                properties.Add(MessagePropertyNames.TypeName, message.GetType().Name);
            }
            catch (ArgumentException exception)
            {
                throw new Exception(string.Format("Failed to add '{0}' property to message property dictionary. Please ensure the property dictionary provided has not been used before, and doesn't contain a property called '{0}' already.", MessagePropertyNames.TypeName), exception);
            }
        }

        private void Connect()
        {
            if (_disposable == null)
            {
                lock (_gate)
                {
                    if (_disposable == null)
                    {
                        _session = _connection.Value.CreateSession(Apache.NMS.AcknowledgementMode.AutoAcknowledge);
                        _producer = _session.CreateProducer(_destination);

                        _disposable = Disposable.Create(() =>
                        {
                            _disposed = true;

                            DrainScheduler();

                            _producer.Close();
                            _producer.Dispose();
                            _session.Close();
                            _session.Dispose();
                        });
                    }
                }
            }
        }

        private void DrainScheduler()
        {
            // Await end of queue (if _taskFactory is a queue/orderedtaskscheduler)
            _drainMethod().Wait(TimeSpan.FromSeconds(DrainTimeoutSecs));
        }

        public void Dispose()
        {
            var disposable = _disposable;
            if (disposable != null)
            {
                disposable.Dispose();
            }
        }
    }
}