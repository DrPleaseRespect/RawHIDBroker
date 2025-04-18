using HidApi;
using RawHIDBroker.EventLoop;
using RawHIDBroker.Messaging;

namespace RawHIDBroker.ConsoleTest
{
    internal class Program
    {
        static void Main(string[] args)
        {
            Device? device = null;
            
            foreach (var item in Hid.Enumerate(0x3434, 0x0321))
            {
                if (item.UsagePage == 0xFF60 && item.Usage == 0x61)
                {
                    device = item.ConnectToDevice();
                }
            }
            if (device == null)
            {
                return;
            }
            DeviceLoop loop = new DeviceLoop(device);
            loop.Start();

            while (Console.ReadKey(true).Key != ConsoleKey.Escape) { };
            byte[] data = new byte[34];
            var returned_data = loop.WriteWait(new Message(1, data, 32));
            Console.WriteLine(returned_data.ToString());
            loop.Stop();
            loop.Join();
            Hid.Exit();
        }
    }
}