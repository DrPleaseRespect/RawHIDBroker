using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;


namespace RawHIDBroker.EventLoop
{
    public class DeviceInformation
    {
        public ushort VID { get { return _vid; } set { _vid = value; } } // Vendor ID
        public ushort PID { get { return _pid; } set { _pid = value; } } // Product ID

        public string? ProductName { get => _productName; } // Product Name
        public string? ManufacturerName { get => _manufacturerName; } // Manufacturer Name

        private string _ProtocolVersion = "1.0"; // Protocol Version

        public string VIDStr
        {
            get
            {
                return $"0x{VID:X4}";
            }
        }
        public string PIDStr
        {
            get
            {
                return $"0x{PID:X4}";
            }
        }

        private ushort _vid;
        private ushort _pid;
        private string? _productName = null;
        private string? _manufacturerName = null;


        public string DeviceIDStr
        {
            get { 
            
                return $"0x{VID:X4}:0x{PID:X4}";
            }
        }

        [SetsRequiredMembers]
        public DeviceInformation(ushort vid, ushort pid)
        {
            _vid = vid;
            _pid = pid;
        }

        [SetsRequiredMembers]
        public DeviceInformation(string deviceID)
        {
            deviceID = deviceID.ToUpper();
            var parts = Regex.Match(deviceID, Globals.VIDPIDPattern);
            try
            {
                _vid = Convert.ToUInt16(parts.Groups[1].ToString(), 16);
                _pid = Convert.ToUInt16(parts.Groups[2].ToString(), 16);
            } catch (RegexParseException ex)
            {
                throw new InvalidDeviceIDFormatException("Invalid Device ID Format");
            }
        }

        [SetsRequiredMembers]
        public DeviceInformation(ushort vid, ushort pid, string? productName, string? manufacturerName) : this(vid, pid)
        {
            _productName = productName;
            _manufacturerName = manufacturerName;
        }

        public void SetDeviceInformation(string? productName, string? manufacturerName)
        {
            _productName = productName;
            _manufacturerName = manufacturerName;
        }

        public static implicit operator string(DeviceInformation deviceID)
        {
            return deviceID.DeviceIDStr;
        }


        public override string ToString()
        {
            return DeviceIDStr;
        }


        public override bool Equals(object? obj)
        {
            if (obj is DeviceInformation deviceID)
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
}
