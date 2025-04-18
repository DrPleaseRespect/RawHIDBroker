
using HidApi;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RawHIDBroker.Messaging;
using System.Collections.Concurrent;
using System.Diagnostics;

namespace RawHIDBroker.EventLoop
{
    public class DeviceLoopException : Exception { };

    public static partial class LoggerExtensions
    {
        [LoggerMessage(Level = LogLevel.Debug, Message = "{Message}")]
        public static partial void WriteDebug(this ILogger logger, string message);

        [LoggerMessage(Level = LogLevel.Error, Message = "{Message}")]
        public static partial void WriteError(this ILogger logger, string message, Exception exc);

    }

    public sealed class DeviceLoop
    {
        private Device _device;
        private readonly Dictionary<int, ConcurrentQueue<Message>> _subsystem_queue = new();
        private readonly ConcurrentQueue<Message> _message_queue = new();
        private readonly object _subsystemqueue_lock = new();
        private bool _running = false;
        private Thread _messageloop_thread = default!;
        public ILoggerFactory _loggerfactory = NullLoggerFactory.Instance;
        private ILogger Logger = NullLogger.Instance;
        private int Retries = 10;

        public DeviceLoop(Device device)
        {
            _device = device;

            Init();
        }

        public DeviceLoop(ushort vid, ushort pid)
        {
            var usage_page = 0xFF60;
            var usageid = 0x61;
            foreach (var item in Hid.Enumerate(vid, pid))
            {
                if (item.UsagePage == usage_page && item.Usage == usageid)
                {
                    _device = item.ConnectToDevice();
                }
            }
            if (_device == null)
            {
                throw new Exception("Failed to connect to device!");
            }
            Init();
        }

        public DeviceLoop(ushort vid, ushort pid, ushort usage_page, ushort usageid)
        {
            foreach (var item in Hid.Enumerate(vid, pid))
            {
                if (item.UsagePage == usage_page && item.Usage == usageid)
                {
                    _device = item.ConnectToDevice();
                }
            }
            if (_device == null)
            {
                throw new Exception("Failed to connect to device!");
            }
            Init();

        }

        public void SetLogger(ILoggerFactory loggerfactory)
        {
            _loggerfactory = loggerfactory;
            // Initialize the logger
            var device_info = this._device.GetDeviceInfo();
            Logger = _loggerfactory.CreateLogger($"DeviceLoop ({device_info.VendorId:X4}:{device_info.ProductId:X4})");
            Logger.WriteDebug("Logger Connected!");
        }

        private void Init()
        {

            // Initialize the device
            if (_device == null)
            {
                throw new Exception("Failed to connect to device!");
            }
            _messageloop_thread = new Thread(MessageLoop);
            _messageloop_thread.IsBackground = true;
        }

        private void ReinitializeDevice()
        {
            // Reinitialize the device
            if (_device == null)
            {
                throw new Exception("Failed to connect to device!");
            }
            Device? new_device = null;
            while (true)
            {
                try
                {
                    new_device = _device.GetDeviceInfo().ConnectToDevice();
                    Logger.WriteDebug("Reinitialized device: " + new_device.GetDeviceInfo().ToString());
                    break;
                }
                catch (HidException e)
                {
                    Logger.WriteDebug("Failed to reinitialize device: " + e.Message);
                    Logger.WriteDebug("Retrying in 5 second...");
                    Thread.Sleep(5000);
                }
            }

            _device.Dispose();
            _device = new_device!;
        }

        public void Start()
        {
            _running = true;
            _messageloop_thread.Start();
        }

        public void Stop()
        {
            Logger.WriteDebug("Stopping Thread!");
            _running = false;

        }

        public void Join()
        {
            _messageloop_thread.Join();
        }

        /// <summary>
        /// Reads a message from the device
        /// </summary>
        public Message? Read(int subsystem)
        {
            lock (_subsystemqueue_lock)
            {
                if (_subsystem_queue.ContainsKey(subsystem))
                {
                    if (_subsystem_queue[subsystem].TryDequeue(out Message? message))
                    {
                        return message;
                    }
                }
            }
            return null;
        }

        /// <summary>
        /// Queues a message to be sent to the device
        /// </summary>
        public void Write(Message message)
        {
            _message_queue.Enqueue(message);
        }

        /// <summary>
        /// Queues a message to the device and wait for a response
        /// </summary>
        public Message WriteWait(Message message)

        {
            _message_queue.Enqueue(message);
            Logger.WriteDebug("Waiting for queue");
            while (_message_queue.Contains(message))
            {

                //Thread.Sleep(10);
            }
            Message? result = null;
            Logger.WriteDebug("Waiting for response");

            while (result == null)
            {
                result = Read(message.Subsystem);
                //Thread.Sleep(10);
            }
            return result;
        }

        private Packet? GetPacket(int timeout_ms = 0)
        {
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

        private void AddToQueue(Message message)
        {
            if (!_subsystem_queue.ContainsKey(message.Subsystem))
            {
                _subsystem_queue.Add(message.Subsystem, new ConcurrentQueue<Message>());
            }
            Logger.WriteDebug("Message Response Sent to Queue! " + message.ToString());
            _subsystem_queue[message.Subsystem].Enqueue(message);
        }

        private void MessageLoop()
        {
            while (_running)
            {
                // Check Queue for messages to send
                if (!_message_queue.IsEmpty)
                {
                    var queue_success = _message_queue.TryDequeue(out Message? message);
                    if (queue_success && message != null)
                    {
                        Packet[] packets = message.ToPackets();
                        foreach (Packet packet_outgoing in packets)
                        {
                            int number_of_tries = 0;
                            while (true)
                            {
                                try
                                {
                                    Logger.WriteDebug("Sending Packet: " + packet_outgoing.ToString());
                                    _device.Write(packet_outgoing.ToSpan());
                                }
                                catch (HidException)
                                {
                                    Logger.WriteDebug("Failed to send packet!");
                                    number_of_tries++;
                                    if (number_of_tries > Retries)
                                    {
                                        Logger.WriteDebug("Failed to send packet after " + number_of_tries + " tries!");
                                        Logger.WriteDebug("Reinitializing device...");
                                        ReinitializeDevice();
                                        break;
                                    }
                                    continue;
                                }
                                break;
                            }
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
                    Logger.WriteError("Failed to read packet!", e);
                    ReinitializeDevice();
                }
                if (packet == null)
                {
                    continue;
                }
                if (!packet.IsMultiPart)
                {
                    Message msg = Message.FromPacket(packet);
                    lock (_subsystemqueue_lock)
                    {

                        AddToQueue(msg);
                    }
                }
                else
                {
                    try
                    {
                        // Multi-part message
                        lock (_subsystemqueue_lock)
                        {
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
                            AddToQueue(msg);
                        }
                    }
                    catch (Exception e)
                    {
                        Logger.WriteError("Failed to read multi-part message!", e);
                        Logger.WriteDebug("Reinitializing device...");
                        ReinitializeDevice();
                    }
                }
            }
        }
    }
}

