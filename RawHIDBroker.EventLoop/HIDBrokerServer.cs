using Microsoft.Extensions.Logging;
using NetMQ;
using NetMQ.Sockets;
using RawHIDBroker.Messaging;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using RawHIDBroker.Shared;


namespace RawHIDBroker.EventLoop
{
    public class Request
    {
        public required string Type { get; set; } // [List, Write, WriteRead] // Management Only [AddDevice, RemoveDevice]
        public DeviceInformation? DeviceID { get; set; } // Device ID (Device ID Format: VID:PID (in Hex))
        public byte? Subsystem { get; set; } // Subsystem ID
        public List<byte>? Message { get; set; } // Message Bytes
        public string? ManagementPin { get; set; } // Must be included for Management Requests
    }

    public class Response
    {
        public required string Status { get; set; } // [ERR, ACK]
        public List<byte>? DeviceMessage { get; set; }
        public string? ErrorMessage { get; set; } // Error Message
        public List<DeviceInformation>? Devices { get; set; }
    }

    public class HIDBrokerServer: IDisposable
    {
        public HashSet<DeviceInformation> Devices
        {
            get
            {
                return ListDevices().ToHashSet();
            }
            set
            {
                foreach (var device in _devices.Keys)
                {
                    if (!value.Contains(new DeviceInformation(device)))
                    {
                        RemoveDevice(device);
                    }
                }
                foreach (var device in value)
                {
                    if (!_devices.ContainsKey(device.ToString()))
                    {
                        AddDevice(device);
                    }
                }
            }
        }

        private readonly ConcurrentDictionary<string, DeviceLoop> _devices = new();
        public readonly string _managementpin = Random.Shared.NextInt64().ToString();
        private bool _running = false;
        private Thread? _thread = null;
        private bool disposedValue;
        private readonly ILogger<HIDBrokerServer> Logger;
        private readonly ILogger<DeviceLoop> _deviceLogger;

        public HIDBrokerServer(ILogger<HIDBrokerServer> ServerLogger, ILogger<DeviceLoop> DeviceLogger)
        {
            _thread = new Thread(Loop);
            Logger = ServerLogger ?? throw new ArgumentNullException(nameof(ServerLogger));
            _deviceLogger = DeviceLogger ?? throw new ArgumentNullException(nameof(DeviceLogger));
        }

        public HIDBrokerServer() : this(new LoggerFactory().CreateLogger<HIDBrokerServer>(), new LoggerFactory().CreateLogger<DeviceLoop>())
        {
        }


        public void Start()
        {
            _running = true;
            var thread = new Thread(Loop);
            thread.Start();
        }

        public void Stop()
        {
            _running = false;

        }

        public void SendBroadcast(Message message)
        {
            foreach (var device in _devices.Values)
            {
                device.Write(message);
            }
        }

        public void SendMessage(DeviceInformation deviceID, Message message)
        {
            if (_devices.TryGetValue(deviceID.ToString(), out var device))
            {
                device.Write(message);
            }
        }

        private NetMQMessage ResponseHandler(NetMQMessage message)
        {
            // Schema
            // [Identity, Empty, Request]
            var response = new Response() { Status = "ERR" };
            var msg = new NetMQMessage();
            msg.Append(message[0]); // Identity Frame
            msg.AppendEmptyFrame(); // Empty Frame for REQ/REP 
            var json_req = JsonSerializer.Deserialize<Request>(message[2].ConvertToString((Encoding.UTF8)));
            if (json_req != null)
            {
                switch (json_req.Type)
                {
                    case "List":
                        {
                            response.Status = "ACK";
                            response.Devices = ListDevices().ToList();
                            break;
                        }
                    case "Write":
                        {
                            if (json_req.DeviceID != null && json_req.Message != null && json_req.Subsystem != null)
                            {
                                _devices.TryGetValue(json_req.DeviceID.ToString(), out var device);
                                if (device != null)
                                {
                                    var device_message = new Message((byte)json_req.Subsystem, json_req.Message);
                                    device.Write(device_message);
                                    response.Status = "ACK";
                                }
                            }
                            break;
                        }
                    case "WriteRead":
                        {
                            if (json_req.DeviceID != null && json_req.Message != null && json_req.Subsystem != null)
                            {
                                _devices.TryGetValue(json_req.DeviceID.ToString(), out var device);
                                if (device != null)
                                {
                                    var device_message = new Message((byte)json_req.Subsystem, json_req.Message);
                                    var dev_response = device.WriteReceive(device_message);
                                    if (dev_response != null)
                                    {
                                        response.Status = "ACK";
                                        response.DeviceMessage = dev_response.ToList();
                                    }
                                }
                            }
                            break;
                        }
                    case "AddDevice":

                        if (json_req.ManagementPin == _managementpin)
                        {
                            if (json_req.DeviceID != null)
                            {
                                try
                                {
                                    AddDevice(json_req.DeviceID);
                                }
                                catch (HIDDeviceAlreadyExistsException ex)
                                {
                                    // Device already exists
                                    response.Status = "ERR";
                                    response.ErrorMessage = ex.Message;
                                }
                                catch (Exception ex)
                                {
                                    response.Status = "ERR";
                                    response.ErrorMessage = ex.Message;
                                }
                            }
                            else
                            {
                                response.Status = "ERR";
                                response.ErrorMessage = "Device ID is required";
                            }
                        }
                        else
                        {
                            response.Status = "ERR";
                            response.ErrorMessage = "Invalid Management Pin";
                        }
                        break;
                    case "RemoveDevice":
                        {
                            if (json_req.ManagementPin == _managementpin)
                            {
                                if (json_req.DeviceID != null)
                                {
                                    try
                                    {
                                        RemoveDevice(json_req.DeviceID);
                                    }
                                    catch (HIDDeviceNotFoundException ex)
                                    {
                                        // Device not found
                                        response.Status = "ERR";
                                        response.ErrorMessage = ex.Message;
                                    }
                                    catch (Exception ex)
                                    {
                                        response.Status = "ERR";
                                        response.ErrorMessage = ex.Message;
                                    }
                                }
                                else
                                {
                                    response.Status = "ERR";
                                    response.ErrorMessage = "Device ID is required";
                                }
                            }
                            else
                            {
                                response.Status = "ERR";
                                response.ErrorMessage = "Invalid Management Pin";
                            }
                            break;
                        }
                }
            }
            msg.Append(JsonSerializer.Serialize<Response>(response));
            return msg;
        }

        private void Loop()
        {
            using (var server = new RouterSocket("@tcp://127.0.0.1:42060"))
            {
                while (_running)
                {
                    NetMQMessage? message = null;
                    server.TryReceiveMultipartMessage(TimeSpan.FromMilliseconds(100), ref message);
                    if (message == null)
                    {
                        continue;
                    }
                    if (message?.FrameCount == 3)
                    {
                        server.SendMultipartMessage(ResponseHandler(message));
                    }
                }
            }
        }

        public IEnumerable<DeviceInformation> ListDevices()
        {
            Logger.LogDebug("Listing devices...");
            return _devices.Select(_devices => _devices.Value.DeviceID);
        }

        public void AddDevice(DeviceInformation deviceID)
        {
            string device_id = deviceID.ToString();
            AddDevice(device_id);
        }


        public void AddDevice(ushort VID, ushort PID)
        {
            var device_id = new DeviceInformation(VID, PID).ToString();
            AddDevice(device_id);
        }

        public void AddDevice(string device_id)
        {
            Logger.LogDebug($"Adding device: {device_id}...");
            device_id = device_id.ToUpper();
            if (_devices.TryGetValue(device_id, out var device))
            {
                throw new HIDDeviceAlreadyExistsException("The device already exists!");
            }
            else
            {
                // Add device to the list
                // Convert DeviceID to string
                var parts = Regex.Match(device_id, Globals.VIDPIDPattern);
                ushort vid = Convert.ToUInt16(parts.Groups[1].Value, 16);
                ushort pid = Convert.ToUInt16(parts.Groups[2].Value, 16);
                var device_loop = new DeviceLoop(vid, pid);
                device_loop.SetLogger(_deviceLogger);
                while (!_devices.TryAdd(device_id, device_loop)) { } // Block until added
                device_loop.Start();
            }
        }

        public void RemoveDevice(DeviceInformation deviceID)
        {
            string device_id = deviceID.ToString();
            RemoveDevice(device_id);
        }

        public void RemoveDevice(ushort VID, ushort PID)
        {
            var device_id = new DeviceInformation(VID, PID).ToString();
            RemoveDevice(device_id);
        }

        public void RemoveDevice(string device_id)
        {
            Logger.LogDebug($"Removing device: {device_id}...");
            device_id = device_id.ToUpper();
            if (_devices.TryGetValue(device_id, out var device))
            {
                // Remove device from the list
                device.Stop();
                while (!_devices.TryRemove(device_id, out device)){ } // Block until removed
                device.Dispose();
            }
            else
            {
                throw new HIDDeviceNotFoundException("Device not found");
            }
        }

        protected virtual void Dispose(bool disposing)
        {
            if (!disposedValue)
            {
                if (disposing)
                {
                    // TODO: dispose managed state (managed objects)
                    Stop();
                    foreach (var device in _devices.Values)
                    {
                        device.Dispose();
                    }
                    _devices.Clear();
                    _thread = null;
                }

                // TODO: free unmanaged resources (unmanaged objects) and override finalizer
                // TODO: set large fields to null
                disposedValue = true;
            }
        }

        // // TODO: override finalizer only if 'Dispose(bool disposing)' has code to free unmanaged resources
        // ~ServerLoop()
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
