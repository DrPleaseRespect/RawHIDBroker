using FFMpegCore;
using FFMpegCore.Enums;
using FFMpegCore.Pipes;
using Microsoft.Extensions.Logging;
using NAudio.Wave;
using RawHIDBroker.EventLoop;
using RawHIDBroker.Messaging;
using System.Buffers;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO.Pipelines;
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
                deviceLoop.Write(message);
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
            var loggerFactory = LoggerFactory.Create(builder => builder.AddDebug());
            deviceLoop.SetLogger(loggerFactory.CreateLogger<DeviceLoop>());
            deviceLoop.Start();

            Pipe vidstream = new Pipe();
            Stream audiostream = new MemoryStream();

            double framerate = FFProbe.Analyse(videopath).PrimaryVideoStream.FrameRate;
            double length = FFProbe.Analyse(videopath).Duration.TotalSeconds;
            Console.WriteLine($"Framerate: {framerate} fps");
            Console.WriteLine($"Total Seconds: {length}");

            Console.WriteLine("Starting FFmpeg processing...");
            Task vid = FFMpegArguments.FromFileInput(videopath)
                .OutputToPipe(new StreamPipeSinkAsync(vidstream.Writer), options =>
                    options.WithVideoFilters(vf => vf.Scale(width, height))
                           .WithCustomArgument("-pix_fmt monob")
                           .WithCustomArgument("-f rawvideo")
                           .WithFramerate(framerate)
                )
                .ProcessAsynchronously().ContinueWith((a) => vidstream.Writer.Complete());
            bool aud = await FFMpegArguments
                .FromFileInput(videopath)
                .OutputToPipe(new StreamPipeSink(audiostream), options =>
                    options.WithCustomArgument("-f s16le")
                            .WithCustomArgument("-acodec pcm_s16le")     // optional, safe default
                            .WithCustomArgument("-ar 44100")             // 44.1kHz
                            .WithCustomArgument("-ac 2")                 // stereo
                )
                .ProcessAsynchronously();

            //while (!aud)
            //{
            //    Console.WriteLine("Waiting for FFMpeg to process audio...");
            //    await Task.Delay(100);
            //  }


            Console.WriteLine("FFmpeg Audio processing completed.");

            Console.WriteLine("Video Stream Length: " + "Unknown");
            Console.WriteLine("Audio Stream Length: " + audiostream.Length);

            Console.WriteLine("Converting video stream to SSD1306 format...");
            // Convert the video stream to SSD1306 format

            audiostream.Position = 0;

            var waveFormat = new WaveFormat(44100, 16, 2);
            var reader = new RawSourceWaveStream(audiostream, waveFormat);
            var waveOut = new WaveOutEvent();
            waveOut.Init(reader);


            List<byte[]> ssdFrames = new List<byte[]>();
            Stream stream = vidstream.Reader.AsStream();
            _ = Task.Run(() =>
            {
                while (ssdFrames.Count < length * framerate)
                {
                    try
                    {
                        stream.ReadExactly(buffer, 0, buffer.Length);
                    }
                    catch (IOException)
                    {
                    }
                    byte[] ssdData = ConvertMonobToSSD1106(buffer, width, height);
                    // Write the SSD data to the stream
                    ssdFrames.Add(ssdData);
                    //vidstream.Reader.AdvanceTo(readResult.Buffer.End); // Advance the reader to the end of the buffer
                }
            });
            


            Console.WriteLine("Starting to read frames...");


            // Wait for 30 Seconds Read Ahead
            //while (ssdFrames.Count <= (length/8) * framerate)
            //{
            //    // Wait for more frames to be processed
            //    Console.CursorLeft = 0;
            //    Console.Write($"Waiting for {length/8} second read ahead: {ssdFrames.Count}/{(length / 8) * framerate}");
            //    await Task.Delay(100);
            //}
            Console.WriteLine();
            //Console.ReadKey(true);
            deviceLoop.Write(new Message(100, new byte[1]));

            TimeSpan frameDuration = TimeSpan.FromMilliseconds(1000.0 / framerate);
            waveOut.Volume = 0.5f; // Set volume to 50%
            waveOut.Play();

            int currentFrame = 0;
            int frameskips = 0;
            while ((currentFrame < ssdFrames.Count) && reader.Length != reader.Position)
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
                    Console.Write($"Current Frame: {currentFrame}/{ssdFrames.Count} | Frame Skipped: {frameskips} | Processed Frames: {ssdFrames.Count}/{Math.Ceiling(length * framerate)}");
                    Console.CursorLeft = 0;
                    
                    currentFrame++; // Skip to catch up
                }


            }
            deviceLoop.Write(new Message(102, new byte[1]));


        }

    }
}
