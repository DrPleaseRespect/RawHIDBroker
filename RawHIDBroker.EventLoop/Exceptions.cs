﻿
namespace RawHIDBroker.EventLoop
{
    public class InvalidDeviceIDFormatException : Exception
    {
        public InvalidDeviceIDFormatException(string message) : base(message) { }
    }

    public class HIDDeviceNotFoundException : Exception
    {
        public HIDDeviceNotFoundException(string message) : base(message) { }
    }

    public class HIDDeviceAlreadyExistsException : Exception
    {
        public HIDDeviceAlreadyExistsException(string message) : base(message) { }
    }
}
