using NetMQ;
using NetMQ.Sockets;
using RawHIDBroker.Messaging;
using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace RawHIDBroker.EventLoop
{
    internal class Globals
    {
        public const string VIDPIDPattern = @"[[:xdigit:]]+:[[:xdigit:]]+";
    }

    public class DeviceID
    {
        public required ushort VID { get; set; } // Vendor ID
        public required ushort PID { get; set; } // Product ID
        public string DeviceIDStr
        {
            get
            {
                return $"{VID:X4}:{PID:X4}";
            }
            set
            {
                var parts = Regex.Match(value, Globals.VIDPIDPattern);
                VID = Convert.ToUInt16(parts.Groups[1].ToString());
                PID = Convert.ToUInt16(parts.Groups[2].ToString());
            }
        }

        [SetsRequiredMembers]
        public DeviceID(ushort vid, ushort pid)
        {
            VID = vid;
            PID = pid;
        }

        [SetsRequiredMembers]
        public DeviceID(string deviceID)
        {
            deviceID = deviceID.ToUpper();
            var parts = Regex.Match(deviceID, Globals.VIDPIDPattern);
            try
            {
                VID = Convert.ToUInt16(parts.Groups[1].ToString());
                PID = Convert.ToUInt16(parts.Groups[2].ToString());
            } catch (RegexParseException ex)
            {
                throw new InvalidDeviceIDFormatException("Invalid Device ID Format");
            }

        }

        public static implicit operator string(DeviceID deviceID)
        {
            return deviceID.DeviceIDStr;
        }


        public override string ToString()
        {
            return DeviceIDStr;
        }


        public override bool Equals(object? obj)
        {
            if (obj is DeviceID deviceID)
            {
                return VID == deviceID.VID && PID == deviceID.PID;
            }
            return false;
        }
        public override int GetHashCode()
        {
            return HashCode.Combine(VID, PID);
        }

    }

    public class Request
    {
        public required string Type { get; set; } // [List, Write, WriteRead] // Management Only [AddDevice, RemoveDevice]
        public DeviceID? DeviceID { get; set; } // Device ID (Device ID Format: VID:PID (in Hex))
        public byte? Subsystem { get; set; } // Subsystem ID
        public List<byte>? Message { get; set; } // Message Bytes
        public string? ManagementPin { get; set; } // Must be included for Management Requests
    }

    public class Response
    {
        public required string Status { get; set; } // [ERR, ACK]
        public List<byte>? DeviceMessage { get; set; }
        public string? ErrorMessage { get; set; } // Error Message
        public List<DeviceID>? Devices { get; set; }
    }

    public class ServerLoop
    {
        private readonly Dictionary<string, DeviceLoop> _devices = new();
        public readonly string _managementpin = Random.Shared.NextInt64().ToString();
        private bool _running = false;
        private Thread? _thread = null;


        public ServerLoop()
        {
            _thread = new Thread(Loop);
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
                            response.Devices = _devices.Select(x => new DeviceID(x.Key)).ToList();
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
                                    var dev_response = device.WriteWait(device_message);
                                    response.Status = "ACK";
                                    response.DeviceMessage = dev_response.ToList();
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
                    var message = server.ReceiveMultipartMessage();
                    if (message.FrameCount == 3)
                    {
                        server.SendMultipartMessage(ResponseHandler(message));
                    }
                }
            }
        }

        private void AddDevice(DeviceID deviceID)
        {
            string device_id = deviceID.ToString();
            AddDevice(device_id);
        }


        private void AddDevice(ushort VID, ushort PID)
        {
            var device_id = new DeviceID(VID, PID).ToString();
            AddDevice(device_id);
        }

        private void AddDevice(string device_id)
        {
            device_id = device_id.ToUpper();
            if (_devices.TryGetValue(device_id, out var device))
            {
                throw new HIDDeviceAlreadyExistsException("Device already exists");
            }
            else
            {
                // Add device to the list
                // Convert DeviceID to string
                var parts = Regex.Match(device_id, Globals.VIDPIDPattern);
                ushort vid = Convert.ToUInt16(parts.Groups[1].ToString());
                ushort pid = Convert.ToUInt16(parts.Groups[2].ToString());
                var device_loop = new DeviceLoop(vid, pid);
                _devices.Add(device_id, device_loop);
                device_loop.Start();
            }
        }

        private void RemoveDevice(DeviceID deviceID)
        {
            string device_id = deviceID.ToString();
            RemoveDevice(device_id);
        }

        private void RemoveDevice(ushort VID, ushort PID)
        {
            var device_id = new DeviceID(VID, PID).ToString();
            RemoveDevice(device_id);
        }

        private void RemoveDevice(string device_id)
        {
            device_id = device_id.ToUpper();
            if (_devices.TryGetValue(device_id, out var device))
            {
                // Remove device from the list
                device.Stop();
                _devices.Remove(device_id);
            }
            else
            {
                throw new HIDDeviceNotFoundException("Device not found");
            }
        }
    }
}
