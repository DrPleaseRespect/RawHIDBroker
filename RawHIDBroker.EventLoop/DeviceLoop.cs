
using HidApi;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RawHIDBroker.Messaging;
using System.Collections.Concurrent;

namespace RawHIDBroker.EventLoop
{
    public class DeviceLoopException : Exception { };

    public static partial class LoggerExtensions
    {
        public static void DeviceDebug(this ILogger logger, DeviceLoop device, string message) {
            logger.LogDebug("[DeviceLoop ({0})]: {1}", device.DeviceID.ToString(), message);
        }

        public static void DeviceError(this ILogger logger, DeviceLoop device, string message, Exception exc)
        {
            logger.LogError(exc, "[DeviceLoop ({0}) (ERROR!)]: {1}", device.DeviceID.ToString(), message);
        }

    }

    public sealed class DeviceLoop: IDisposable
    {
        public bool Active { get { return _active; } }

        private Device? _device = null;
        private readonly Dictionary<int, ConcurrentQueue<Message>> _subsystem_queue = new();
        private readonly ConcurrentQueue<Message> _message_queue = new();
        private readonly object _subsystemqueue_lock = new();
        private bool _running = false;
        private Thread _messageloop_thread = default!;
        private ILogger Logger = NullLogger.Instance;
        private int Retries = 10;
        private int MaxQueueCount = 100;
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
        /// Reads a message from the device
        /// </summary>
        private Message? Read(int subsystem)
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
            while (_message_queue.Count > MaxQueueCount)
            {
                Logger.DeviceDebug(this, $"Message Queue has reached {MaxQueueCount} Messages!");
                Thread.Sleep(50);
            }
            _message_queue.Enqueue(message);
            Logger.DeviceDebug(this, "Message Queued: " + message.ToString());
        }

        /// <summary>
        /// Queues a message to the device and wait for a response
        /// </summary>
        public Message WriteWait(Message message, int polling_speed = 5)

        {
            while (_message_queue.Count > MaxQueueCount)
            {
                Logger.DeviceDebug(this, $"Message Queue has reached {MaxQueueCount} Messages!");
                Thread.Sleep(50);
            }
            _message_queue.Enqueue(message);
            Logger.DeviceDebug(this, "Waiting for queue");
            while (_message_queue.Contains(message))
            {

                Thread.Sleep(polling_speed);
            }
            Message? result = null;
            Logger.DeviceDebug(this, "Waiting for response");

            while (result == null)
            {
                result = Read(message.Subsystem);
                //Thread.Sleep(10);
            }
            return result;
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

        private void AddToQueue(Message message)
        {
            if (!_subsystem_queue.ContainsKey(message.Subsystem))
            {
                _subsystem_queue.Add(message.Subsystem, new ConcurrentQueue<Message>());
            }
            if (_subsystem_queue[message.Subsystem].Count < MaxQueueCount)
            {
                Logger.DeviceDebug(this, "Message Response Sent to Queue! " + message.ToString());
                _subsystem_queue[message.Subsystem].Enqueue(message);
            } else
            {
                Logger.DeviceDebug(this, $"Message Response Dropped due to MaxQueueCount ({MaxQueueCount})! Message: " + message.ToString());
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
                // Obtain Device Information
                if (_deviceID.ManufacturerName == null || _deviceID.ProductName == null) {
                    Logger.DeviceDebug(this, "Obtaining Device Information...");
                    _deviceID.SetDeviceInformation(_device.GetProduct(), _device.GetManufacturer());
                    Logger.DeviceDebug(this, "Device Information Obtained.");

                }

                // Check Queue for messages to send
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
                    if (_message_queue.IsEmpty)
                    {
                        packet = GetPacket(1); // Read with a delay if there are no messages in the queue
                    } else
                    {
                        packet = GetPacket(1); // Read with less delay if there are messages in the queue
                    }
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
                    lock (_subsystemqueue_lock)
                    {
                        if (msg.Subsystem == 1)
                        {
                            Logger.DeviceDebug(this, "Received Protocol Version: " + msg.ToString());
                        }
                        else
                        {
                            Logger.DeviceDebug(this, "Received Message: " + msg.ToString());
                        }
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
                            if (msg.Subsystem == 1)
                            {
                                Logger.DeviceDebug(this, "Received Protocol Version: " + msg.ToString());
                            }
                            else
                            {
                                Logger.DeviceDebug(this, "Received Multi-Part Message: " + msg.ToString());
                            }
                            AddToQueue(msg);
                        }
                    }
                    catch (Exception e)
                    {
                        Logger.DeviceError(this,"Failed to read multi-part message!", e);
                        Logger.DeviceDebug(this,"Reinitializing device...");
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
                    _subsystem_queue.Clear();
                    _message_queue.Clear();


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

