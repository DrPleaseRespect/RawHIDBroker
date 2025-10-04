using System.Collections;

namespace RawHIDBroker.Messaging
{

    public enum PrivateSubsystems : byte
    {
        // Private Subsystems for HIDBroker use only
        ERROR_STATE = 0,
        GET_PROTOCOL_INFO,
        GET_CAPABILITIES,

        GET_RGB_SETTINGS,
        GET_RGB_MODES,
        SET_RGB_SETTINGS,
        PER_KEY_RGB,
        VOLUME,
        BROADCAST = 99,
    }

    public enum DeviceCapabilities : byte
    {
        BACKLIGHT = 0,
        LED_MATRIX,
        RGBLIGHT,
        RGB_MATRIX,
        OLED,
        ST7565,
        HD44780,
        QUANTUM_PAINTER,
        AUDIO
        
    }

    public class MalformedPacket : Exception
    {
        public MalformedPacket() { }
        public MalformedPacket(string message) : base(message) { }
        public MalformedPacket(string message, Exception inner) : base(message, inner) { }
    }

    public class MalformedMessage : Exception
    {
        public MalformedMessage() { }
        public MalformedMessage(string message) : base(message) { }
        public MalformedMessage(string message, Exception inner) : base(message, inner) { }
    }


    public class Packet
    {
        public byte subsystem;
        public byte id;
        public byte length;
        public byte[] data = new byte[MessageParameters.DATA_SIZE];

        public bool IsMultiPart
        {
            get
            {
                return length > MessageParameters.DATA_SIZE;
            }
        }
        public int NumberOfPackets
        {
            get
            {
                return (int)Math.Ceiling((double)length / MessageParameters.DATA_SIZE);
            }
        }

        public Packet(byte subsystem, byte id, byte length)
        {
            this.subsystem = subsystem;
            this.id = id;
            this.length = length;
        }

        public Packet(byte subsystem, byte id, byte length, byte[] data)
        {
            this.subsystem = subsystem;
            this.id = id;
            this.length = length;
            if (data.Length > MessageParameters.DATA_SIZE)
            {
                throw new MalformedPacket($"Data is too large (Max Size = {MessageParameters.DATA_SIZE})");
            }
            Array.Copy(data, 0, this.data, 0, MessageParameters.DATA_SIZE);
        }

        internal Packet(byte[] bytes)
        {

            subsystem = bytes[0];
            id = bytes[1];
            length = bytes[2];

            Array.Copy(bytes, 3, this.data, 0, MessageParameters.DATA_SIZE);

        }

        public static Packet FromBytes(IList<byte> bytes)
        {

            return new Packet(bytes.ToArray());
        }

        public static Packet FromBytes(ReadOnlySpan<byte> bytes)
        {
            return new Packet(bytes.ToArray());
        }

        public byte[] ToBytes()
        {
            byte[] bytes = new byte[MessageParameters.PACKET_SIZE + 1];
            bytes[0] = 0x00;
            bytes[1] = subsystem;
            bytes[2] = id;
            bytes[3] = length;
            data.CopyTo(bytes, 4);
            return bytes;
        }

        public Span<byte> ToSpan()
        {
            return ToBytes().AsSpan();
        }

        public override string ToString()
        {
            return $"Packet: [{String.Join(',', ToBytes())}]";
        }
    }

    public interface IMessage : IList<byte>
    {
        public byte Subsystem { get; }
        public byte Length { get; }
        public int PacketLength { get; }
        public byte[] Data { get; }
        public Packet[] ToPackets();
    }

    // Abstraction of Packets
    public class Message : IMessage
    {
        private byte subsystem;
        private static byte id;
        // Length is the Data Length (not including metadata)
        private byte length;
        private byte[] _data;

        public byte Subsystem
        {
            get
            {
                return subsystem;
            }
        }

        public byte Length
        {
            get
            {
                return length;
            }
        }

        public int PacketLength
        {
            get
            {
                return ToPackets().Length;
            }
        }

        public byte[] Data
        {
            get
            { 
                return _data;
            } 
        }

        public int Count => ((ICollection<byte>)_data).Count;

        public bool IsReadOnly => ((ICollection<byte>)_data).IsReadOnly;

        public byte this[int index] { get => ((IList<byte>)_data)[index]; set => ((IList<byte>)_data)[index] = value; }

        private Packet[] CreatePacketsFromData(byte[] data, byte length)
        {
            List<Packet> packet_list = new List<Packet>(); // Initialize packet_list
            byte total_length = length;
            byte remaining_data = total_length;
            id++;

            while (remaining_data > 0)
            {
                Packet packet = new Packet(subsystem, id, total_length);
                byte data_offset = (byte)(total_length - remaining_data);
                byte bytes_to_copy = remaining_data < MessageParameters.DATA_SIZE ? remaining_data : MessageParameters.DATA_SIZE;
                Array.Copy(data, data_offset, packet.data, 0, bytes_to_copy);
                remaining_data -= bytes_to_copy;
                packet_list.Add(packet);
            }

            return packet_list.ToArray(); // Return the array of packets
        }

        public Message(byte subsystem, byte[] data, byte length)
        {
            if (length > MessageParameters.MAX_DATA_SIZE)
            {
                throw new MalformedMessage($"Data is too large (Max Size = {MessageParameters.MAX_DATA_SIZE})");
            }
            _data = new byte[length];
            this.length = length;
            this.subsystem = subsystem;
            Array.Copy(data, _data, length);
        }

        public Message(byte subsystem, IEnumerable<byte> data)
        {
            if (length > MessageParameters.MAX_DATA_SIZE)
            {
                throw new MalformedMessage($"Data is too large (Max Size = {MessageParameters.MAX_DATA_SIZE})");
            }
            this.length = (byte)data.Count();
            this._data = data.ToArray<byte>();
            this.subsystem = subsystem;
            
        }

        public override string ToString()
        {
            // Create Data Array
            return $"Message: Subsystem={subsystem}, Length={length}, Data=[{String.Join(',', _data)}]";
        }

        public static Message FromPacket(Packet packet)
        {
            return new Message(packet.subsystem, packet.data, packet.length);
        }

        public static Message FromPackets(Packet[] packets)
        {
            // Verify that all packets have the same id
            // Verify that all packets have the same subsystem
            // Verify that all packets have the same length
            foreach (Packet packet in packets)
            {
                if (packet.id != packets[0].id)
                {
                    throw new MalformedMessage("Packets have different IDs");
                }
                if (packet.subsystem != packets[0].subsystem)
                {
                    throw new MalformedMessage("Packets have different subsystems");
                }
                if (packet.length != packets[0].length)
                {
                    throw new MalformedMessage("Packets have different lengths");
                }
            }
            // Combine the data from all packets
            byte[] data = new byte[packets[0].length];
            byte remaining_data = packets[0].length;
            byte data_offset = 0;
            foreach (Packet packet in packets)
            {
                data_offset = (byte)(packets[0].length - remaining_data);
                Array.Copy(packet.data, 0, data, data_offset, data.Length);
                //packet.data.CopyTo(data, data_offset);
                remaining_data -= remaining_data < MessageParameters.DATA_SIZE ? remaining_data : MessageParameters.DATA_SIZE;
            }
            if (remaining_data != 0)
            {
                throw new MalformedMessage("Data is missing");
            }

            return new Message(packets[0].subsystem, data, packets[0].length);

        }


        public Packet[] ToPackets()
        {
            return CreatePacketsFromData(_data, length);
        }


        public IEnumerator GetEnumerator()
        {
            return _data.GetEnumerator();
        }

        public int IndexOf(byte item)
        {
            return ((IList<byte>)_data).IndexOf(item);
        }

        public void Insert(int index, byte item)
        {
            ((IList<byte>)_data).Insert(index, item);
        }

        public void RemoveAt(int index)
        {
            ((IList<byte>)_data).RemoveAt(index);
        }

        public void Add(byte item)
        {
            ((ICollection<byte>)_data).Add(item);
        }

        public void Clear()
        {
            ((ICollection<byte>)_data).Clear();
        }

        public bool Contains(byte item)
        {
            return ((ICollection<byte>)_data).Contains(item);
        }

        public void CopyTo(byte[] array, int arrayIndex)
        {
            ((ICollection<byte>)_data).CopyTo(array, arrayIndex);
        }

        public bool Remove(byte item)
        {
            return ((ICollection<byte>)_data).Remove(item);
        }

        IEnumerator<byte> IEnumerable<byte>.GetEnumerator()
        {
            return ((IEnumerable<byte>)_data).GetEnumerator();
        }
    }
}
