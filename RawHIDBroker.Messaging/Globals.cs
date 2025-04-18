
namespace RawHIDBroker.Messaging
{
    internal class Globals
    {
        public const int MAX_DATA_SIZE = 255;
        public const byte PACKET_SIZE = 32;
        public const byte PAYLOAD_SIZE = 3;
        public const byte DATA_SIZE = PACKET_SIZE - PAYLOAD_SIZE;

        public const byte CTRL_VER_MAJOR = 1;
        public const byte CTRL_VER_MINOR = 0;
    }
}
