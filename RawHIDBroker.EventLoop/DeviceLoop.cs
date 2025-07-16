
using HidApi;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RawHIDBroker.Messaging;
using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;

namespace RawHIDBroker.EventLoop
{
    public class DeviceLoopException : Exception { };

    public static partial class LoggerExtensions
    {
        public static void DeviceDebug(this ILogger logger, DeviceLoop device, string message)
        {
            logger.LogDebug("[DeviceLoop ({0})]: {1}", device.DeviceID.ToString(), message);
        }

        public static void DeviceError(this ILogger logger, DeviceLoop device, string message, Exception exc)
        {
            logger.LogError(exc, "[DeviceLoop ({0}) (ERROR!)]: {1}", device.DeviceID.ToString(), message);
        }

    }

    public sealed class MessageEnvelope : IDisposable
    {
        private bool disposedValue;

        public Message Message { get; }
        public DateTime Timestamp { get; }
        public AutoResetEvent WaitHandle { get; } = new AutoResetEvent(false);
        public MessageEnvelope(Message message)
        {
            Message = message;
            Timestamp = DateTime.UtcNow;
        }
        public override string ToString()
        {
            return $"{Message.ToString()} @ {Timestamp.ToString("o")}";
        }

        private void Dispose(bool disposing)
        {
            if (!disposedValue)
            {
                if (disposing)
                {
                    WaitHandle.Dispose();
                }
                disposedValue = true;
            }
        }

        public void Dispose()
        {
            Dispose(disposing: true);
            GC.SuppressFinalize(this);
        }
    }

    public sealed class DeviceQueueManager : IDisposable
    {
        private ILogger _logger;
        private readonly ConcurrentDictionary<int, BlockingCollection<Message>> _responseQueues = new();
        private readonly BlockingCollection<MessageEnvelope> _messageQueue;

        public int MaxQueueCount { get; } = 1000;

        public int MessageQueueCount => _messageQueue.Count;
        public int ResponseQueueCount(int subsystem)
        {
            return _responseQueues.TryGetValue(subsystem, out var queue) ? queue.Count : 0;
        }


        public DeviceQueueManager(ILogger? logger = null)
        {
            if (logger == null)
            {
                logger = NullLogger<DeviceQueueManager>.Instance;
            }
            _logger = logger;
            _messageQueue = new BlockingCollection<MessageEnvelope>(new ConcurrentQueue<MessageEnvelope>(), MaxQueueCount);
            _logger.LogDebug("DeviceQueueManager Initialized with MaxQueueCount: {MaxQueueCount}", MaxQueueCount);
        }

        public void SetLogger(ILogger logger)
        {
            _logger = logger;
            _logger.LogDebug("Logger Set for DeviceQueueManager");
        }

        public MessageEnvelope EnqueueMessage(Message message)
        {
            var envelope = new MessageEnvelope(message);
            _messageQueue.Add(envelope);
            _logger.LogDebug("Message Enqueued: {Message}", message.ToString());
            return envelope;
        }

        public Message? DequeueMessage(int timeout = 500)
        {
            if (_messageQueue.TryTake(out var envelope, timeout))
            {
                _logger.LogDebug("Message Dequeued: {Message}", envelope.ToString());
                envelope.WaitHandle.Set(); // Signal that the message has been dequeued
                envelope.Dispose(); // Dispose of the envelope after use
                return envelope.Message;
            }
            return null;
        }

        public void EnqueueResponse(Message message)
        {
            var subsystem = message.Subsystem;
            var queue = _responseQueues.GetOrAdd(subsystem, _ => new BlockingCollection<Message>(new ConcurrentQueue<Message>(), MaxQueueCount));
            var add_response = queue.TryAdd(message);
            if (!add_response)
            {
                _logger.LogWarning("Response Queue for Subsystem {Subsystem} is full. Message not added: {Message}", subsystem, message.ToString());
            } else
            {
                _logger.LogDebug("Response Enqueued: {Message}", message.ToString());
            }
        }

        public Message? DequeueResponse(int subsystem, int timeout = 500)
        {
            if (_responseQueues.TryGetValue(subsystem, out var queue) && queue.TryTake(out var message, timeout))
            {
                _logger.LogDebug("Response Dequeued: {Message}", message.ToString());
                return message;
            }
            return null;
        }

        public void Dispose()
        {
            // Dispose of all response queues
            foreach (var message in _messageQueue.GetConsumingEnumerable())
            {
                message.Dispose(); // Dispose of each message envelope
            }
            _messageQueue.Dispose();
            foreach (var queue in _responseQueues.Values)
            {
                queue.Dispose(); // Dispose of each response queue
            }
            _responseQueues.Clear();
        }
    }

    public sealed class DeviceLoop : IDisposable
    {


        public bool Active { get { return _active; } }

        private Device? _device = null;
        private DeviceQueueManager _queueManager = default!;
        private bool _running = false;
        private Thread _messageloop_thread = default!;
        private ILogger Logger = NullLogger.Instance;
        private int Retries = 10;
        private readonly DeviceInformation _deviceID;
        private bool _active = false;
        private bool disposedValue;

        public DeviceInformation DeviceID
        {
            get { return _deviceID; }
        }

        public DeviceLoop(Device device)
        {
            _deviceID = new DeviceInformation(
                device.GetDeviceInfo().VendorId,
                device.GetDeviceInfo().ProductId,
                device.GetProduct(256),
                device.GetManufacturer(256));
            _device = device;
            Init();
        }

        public DeviceLoop(ushort vid, ushort pid)
        {
            _deviceID = new DeviceInformation(vid, pid);
            _device = GetDevice();
            Init();
        }

        public DeviceLoop(ushort vid, ushort pid, ushort usage_page, ushort usageid)
        {
            _deviceID = new DeviceInformation(vid, pid);
            _device = GetDevice(usage_page, usageid);
            Init();

        }


        public void SetLogger(ILogger<DeviceLoop> logger)
        {
            Logger = logger;
            logger.DeviceDebug(this, "Logger Active!");
        }

        private Device? GetDevice()
        {
            ushort usage_page = 0xFF60;
            ushort usageid = 0x61;
            return GetDevice(usage_page, usageid);
        }

        private Device? GetDevice(ushort usage_page, ushort usageid)
        {
            foreach (var item in Hid.Enumerate(_deviceID.VID, _deviceID.PID))
            {
                if (item.UsagePage == usage_page &&
                    item.Usage == usageid &&
                    item.VendorId == _deviceID.VID &&
                    item.ProductId == _deviceID.PID)
                {
                    Logger.DeviceDebug(this, "Obtaining Device Information...");
                    _deviceID.SetDeviceInformation(item.ProductString, item.ManufacturerString);
                    Logger.DeviceDebug(this, "Device Information Obtained.");
                    return item.ConnectToDevice();
                }
            }
            return null;
        }


        private void Init()
        {
            _queueManager = new DeviceQueueManager(Logger);
            // Initialize the device
            Write(new Message(1, [0])); // Protocol Version Request
            _messageloop_thread = new Thread(MessageLoop);
            _messageloop_thread.IsBackground = true;
        }

        private void ReinitializeDevice()
        {
            _active = false;
            // Reinitialize the device
            Device? new_device = null;
            while (true)
            {
                try
                {
                    new_device = GetDevice();
                    if (new_device == null)
                    {
                        throw new HIDDeviceNotFoundException("Device was Never Found");
                    }
                    Logger.DeviceDebug(this, "Reinitialized device: " + new_device.GetDeviceInfo().ToString());
                    break;
                }
                catch (HidException e)
                {
                    Logger.DeviceDebug(this, "Failed to reinitialize device: " + e.Message);
                    Logger.DeviceDebug(this, "Retrying in 5 second...");
                    Thread.Sleep(5000);
                }
                catch (HIDDeviceNotFoundException e)
                {
                    Logger.DeviceDebug(this, "Failed to reinitialize device: " + e.Message);
                    Logger.DeviceDebug(this, "Retrying in 5 second...");
                    Thread.Sleep(5000);
                }
            }
            if (_device != null)
            {
                _device.Dispose();
            }
            _device = new_device;
            _active = true;
        }

        public void Start()
        {
            _running = true;
            _messageloop_thread.Start();
        }

        public void Stop()
        {
            Logger.DeviceDebug(this, "Stopping Thread!");
            _running = false;

        }

        public void Join()
        {
            _messageloop_thread.Join();
        }

        /// <summary>
        /// Queues a message to be sent to the device
        /// </summary>
        public void Write(Message message)
        {
            _queueManager.EnqueueMessage(message);
            Logger.DeviceDebug(this, "Message Queued: " + message.ToString());
        }

        /// <summary>
        /// Queues a message to the device and wait for a response
        /// </summary>
        public Message? WriteWait(Message message, int timeout = 500)

        {
            var envelope = _queueManager.EnqueueMessage(message);
            Logger.DeviceDebug(this, "Message Queued: " + message.ToString());
            envelope.WaitHandle.WaitOne(timeout);
            Message? response = _queueManager.DequeueResponse(message.Subsystem, timeout);
            if (response == null)
            {
                Logger.DeviceDebug(this, "No response received for message: " + message.ToString());
            }
            else
            {
                Logger.DeviceDebug(this, "Response Received: " + response.ToString());
            }
            return response;
        }

        private Packet? GetPacket(int timeout_ms = 0)
        {
            if (_device == null)
            {
                return null;
            }
            ReadOnlySpan<Byte> data;
            if (timeout_ms == 0)
            {
                data = _device.Read(32);

            }
            else
            {
                data = _device.ReadTimeout(32, timeout_ms);
            }
            if (data.IsEmpty)
            {
                return null;
            }
            try
            {
                Packet packet = Packet.FromBytes(data);
                return packet;
            }
            catch (Exception)
            {
                return null;
            }
        }

        private void MessageLoop()
        {
            Logger.DeviceDebug(this, "Message Loop Started!");
            while (_running)
            {
                if (_device == null)
                {
                    Logger.DeviceDebug(this, "Device is null!");
                    Logger.DeviceDebug(this, "Obtaining Device!");
                    _device = GetDevice();
                    if (_device == null)
                    {
                        Logger.DeviceDebug(this, "Failed to obtain device!");
                        Logger.DeviceDebug(this, "Retrying in 5 seconds...");
                        Thread.Sleep(5000);
                        continue;
                    }
                }
                _active = true;

                if (_deviceID.ManufacturerName == null || _deviceID.ProductName == null)
                {
                    Logger.DeviceDebug(this, "Obtaining Device Information...");
                    _deviceID.SetDeviceInformation(_device.GetProduct(), _device.GetManufacturer());
                    Logger.DeviceDebug(this, "Device Information Obtained.");

                }
                var message = _queueManager.DequeueMessage(-1);

                if (message != null)
                {
                    Packet[] packets = message.ToPackets();
                    foreach (Packet packet_outgoing in packets)
                    {
                        int number_of_tries = 0;
                        while (true)
                        {
                            try
                            {
                                Logger.DeviceDebug(this, "Sending Packet: " + packet_outgoing.ToString());
                                _device.Write(packet_outgoing.ToSpan());
                            }
                            catch (HidException)
                            {
                                Logger.DeviceDebug(this, "Failed to send packet!");
                                number_of_tries++;
                                if (number_of_tries > Retries)
                                {
                                    Logger.DeviceDebug(this, "Failed to send packet after " + number_of_tries + " tries!");
                                    Logger.DeviceDebug(this, "Reinitializing device...");
                                    ReinitializeDevice();
                                    break;
                                }
                                continue;
                            }
                            break;
                        }
                    }
                }
                Packet? packet = null;
                try
                {
                    packet = GetPacket(1);
                }
                catch (HidApi.HidException e)
                {
                    Logger.DeviceError(this, "Failed to read packet!", e);
                    ReinitializeDevice();
                }
                catch (NullReferenceException e)
                {
                    Logger.DeviceError(this, "Device was not found!", e);
                    ReinitializeDevice();
                }
                if (packet == null)
                {
                    continue;
                }
                if (!packet.IsMultiPart)
                {
                    Message msg = Message.FromPacket(packet);
                    Logger.DeviceDebug(this, "Received Packet: " + packet.ToString());
                    if (msg.Subsystem == 1)
                    {
                        Logger.DeviceDebug(this, "Received Protocol Version: " + msg.ToString());
                    }
                    else
                    {
                        Logger.DeviceDebug(this, "Received Message: " + msg.ToString());
                    }
                    _queueManager.EnqueueResponse(msg);
                }
                else
                {
                    try
                    {
                        // Multi-part message
                            Packet[] packets = new Packet[packet.NumberOfPackets];
                            packets[0] = packet;
                            for (int i = 1; i < packet.NumberOfPackets; i++)
                            {

                                Packet? p = GetPacket(5000);
                                if (p == null)
                                {
                                    throw new Exception("Failed to get packet!");
                                }
                                packets[i] = p;

                            }
                            Message msg = Message.FromPackets(packets);
                            if (msg.Subsystem == 1)
                            {
                                Logger.DeviceDebug(this, "Received Protocol Version: " + msg.ToString());
                            }
                            else
                            {
                                Logger.DeviceDebug(this, "Received Multi-Part Message: " + msg.ToString());
                            }
                            _queueManager.EnqueueResponse(msg);
                    }
                    catch (Exception e)
                    {
                        Logger.DeviceError(this, "Failed to read multi-part message!", e);
                        Logger.DeviceDebug(this, "Reinitializing device...");
                        ReinitializeDevice();
                    }
                }
            }
        }

        private void Dispose(bool disposing)
        {
            if (!disposedValue)
            {
                if (disposing)
                {
                    // TODO: dispose managed state (managed objects)
                    if (_device != null)
                    {
                        _device.Dispose();
                        _device = null;
                    }
                    if (_messageloop_thread != null)
                    {
                        _running = false;
                    }
                    _queueManager.Dispose();


                }

                // TODO: free unmanaged resources (unmanaged objects) and override finalizer
                // TODO: set large fields to null
                disposedValue = true;
            }
        }

        // // TODO: override finalizer only if 'Dispose(bool disposing)' has code to free unmanaged resources
        // ~DeviceLoop()
        // {
        //     // Do not change this code. Put cleanup code in 'Dispose(bool disposing)' method
        //     Dispose(disposing: false);
        // }

        public void Dispose()
        {
            // Do not change this code. Put cleanup code in 'Dispose(bool disposing)' method
            Dispose(disposing: true);
            GC.SuppressFinalize(this);
        }
    }
}

