
namespace RawHIDBroker.Messaging
{
    internal class MessageParameters
    {
        public const int MAX_DATA_SIZE = 255;
        public const byte PACKET_SIZE = 32;
        public const byte PAYLOAD_SIZE = 3;
        public const byte DATA_SIZE = PACKET_SIZE - PAYLOAD_SIZE;

        public const byte CTRL_VER_MAJOR = 1;
        public const byte CTRL_VER_MINOR = 0;

        public const string VIDPIDPattern = @"((?:0[xX])?[\dA-Fa-f]+):((?:0[xX])?[\dA-Fa-f]+)";

    }
}
