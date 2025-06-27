using FFMpegCore;
using FFMpegCore.Enums;
using FFMpegCore.Pipes;
using Microsoft.Extensions.Logging;
using NAudio.Wave;
using RawHIDBroker.EventLoop;
using RawHIDBroker.Messaging;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text;
namespace BadApple
{

    internal class Program
    {
        class SSD1106Page
        {
            public byte[] Data { get; set; }
            public int PageNumber { get; set; }
            public SSD1106Page(byte[] data, int pageNumber)
            {
                Data = data;
                PageNumber = pageNumber;
            }
        }

        static void SendFrame(DeviceLoop deviceLoop, byte[] frameData)
        {
            if (frameData.Length != 1024)
            {
                throw new ArgumentException("Frame data must be exactly 1024 bytes long.");
            }
            // Create a message with the frame data
            Span<byte> bytes = new Span<byte>(frameData);
            for (int i = 0; i < bytes.Length; i += 255)
            {
                int length = Math.Min(255, bytes.Length - i);
                Message message = new Message(101, bytes.Slice(i, length).ToArray());
                //Console.WriteLine($"Wrote {i/255} frame");
                deviceLoop.WriteWait(message);
            }
        }

        static byte[] ConvertMonobToSSD1106(byte[] monob, int width = 128, int height = 64)
        {
            int rows = height;
            int cols = width;
            int pages = height / 8;
            byte[] ssd = new byte[cols * pages]; // 128 × 8 = 1024

            int bytesPerRow = cols / 8;

            for (int page = 0; page < pages; page++)
            {
                for (int x = 0; x < cols; x++)
                {
                    byte columnByte = 0;
                    for (int bit = 0; bit < 8; bit++)
                    {
                        int y = page * 8 + bit;
                        int rowByteIndex = y * bytesPerRow + (x / 8);
                        int bitInByte = 7 - (x % 8);
                        int bitValue = (monob[rowByteIndex] >> bitInByte) & 1;

                        columnByte |= (byte)(bitValue << bit);
                    }

                    ssd[page * cols + x] = columnByte;
                }
            }

            return ssd;
        }


        static async Task Main(string[] args)
        {
            int width = 128;
            int height = 64;
            int frameSize = width / 8;
            byte[] buffer = new byte[frameSize * height];
            string videopath = @"F:\Videos\badapple.webm";


            DeviceLoop deviceLoop = new DeviceLoop(0xFEED, 0x0000);
            // Create Logger
            var loggerFactory = LoggerFactory.Create(builder => builder.AddDebug().AddConsole());
            deviceLoop.SetLogger(loggerFactory.CreateLogger<DeviceLoop>());
            deviceLoop.Start();

            Stream vidstream = new MemoryStream();
            Stream audiostream = new MemoryStream();

            double framerate = FFProbe.Analyse(videopath).PrimaryVideoStream.FrameRate;
            Console.WriteLine($"Framerate: {framerate} fps");

            Console.WriteLine("Starting FFMpeg processing...");
            bool vid = await FFMpegArguments.FromFileInput(videopath)
                .OutputToPipe(new StreamPipeSink(vidstream), options =>
                    options.WithVideoFilters(vf => vf.Scale(width, height))
                           .WithCustomArgument("-pix_fmt monob")
                           .WithCustomArgument("-f rawvideo")
                           .WithFramerate(framerate)
                )
                .ProcessAsynchronously();
            bool aud = await FFMpegArguments
                .FromFileInput(videopath)
                .OutputToPipe(new StreamPipeSink(audiostream), options =>
                    options.WithCustomArgument("-f s16le")
                            .WithCustomArgument("-acodec pcm_s16le")     // optional, safe default
                            .WithCustomArgument("-ar 44100")             // 44.1kHz
                            .WithCustomArgument("-ac 2")                 // stereo
                )
                .ProcessAsynchronously();

            while (!vid || !aud)
            {
                await Task.Delay(100);
            }


            Console.WriteLine("FFMpeg processing completed.");

            Console.WriteLine("Video Stream Length: " + vidstream.Length);
            Console.WriteLine("Audio Stream Length: " + audiostream.Length);

            Console.WriteLine("Converting video stream to SSD1106 format...");
            // Convert the video stream to SSD1106 format

            vidstream.Position = 0;
            audiostream.Position = 0;

            var waveFormat = new WaveFormat(44100, 16, 2);
            var reader = new RawSourceWaveStream(audiostream, waveFormat);
            var waveOut = new WaveOutEvent();
            waveOut.Init(reader);


            List<byte[]> ssdFrames = new List<byte[]>();
            while (vidstream.Position < vidstream.Length)
            {
                int bytesRead = vidstream.Read(buffer, 0, buffer.Length);
                if (bytesRead < buffer.Length)
                {
                    Console.WriteLine("End of video stream reached.");
                    break;
                }
                byte[] ssdData = ConvertMonobToSSD1106(buffer, width, height);
                // Write the SSD data to the stream
                ssdFrames.Add(ssdData);
            }


            Console.WriteLine("Starting to read frames...");



            Console.WriteLine("Stream Length: " + vidstream.Length);
            
            deviceLoop.Write(new Message(100, new byte[1]));

            TimeSpan frameDuration = TimeSpan.FromMilliseconds(1000.0 / framerate);
            waveOut.Play();

            int currentFrame = 0;
            int frameskips = 0;
            while (currentFrame < ssdFrames.Count)
            {
                TimeSpan expectedTime = frameDuration * currentFrame;

                if (reader.CurrentTime >= expectedTime) {
                    SendFrame(deviceLoop, ssdFrames[currentFrame]);
                    currentFrame++;
                }
                else
                {
                    await Task.Delay(1); // Wait for the next frame time
                }

                while (currentFrame < ssdFrames.Count &&
               (reader.CurrentTime - (frameDuration * currentFrame)).TotalMilliseconds > frameDuration.TotalMilliseconds * 2)
                {
                    frameskips++;
                    Console.Write($"Frame Skipped: {frameskips}  ");
                    Console.CursorLeft = 0;
                    
                    currentFrame++; // Skip to catch up
                }


            }
            deviceLoop.Write(new Message(102, new byte[1]));


        }

    }
}
