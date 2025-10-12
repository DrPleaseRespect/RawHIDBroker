using FFMpegCore;
using FFMpegCore.Pipes;
using Microsoft.Extensions.Logging;
using NAudio.Wave;
using System.Buffers;
using System.IO.Pipelines;
namespace RawHIDBroker.Demos.BadApple
{

    internal class Program
    {

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

            double framerate = 0;
            double length = 0;

            if (videopath.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            {
                framerate = FFProbe.Analyse(new Uri(videopath)).PrimaryVideoStream.FrameRate;
                length = FFProbe.Analyse(new Uri(videopath)).Duration.TotalSeconds;
            }
            else
            {
                framerate = FFProbe.Analyse(videopath).PrimaryVideoStream.FrameRate;
                length = FFProbe.Analyse(videopath).Duration.TotalSeconds;
            }
            Console.WriteLine($"Framerate: {framerate} fps");
            Console.WriteLine($"Total Seconds: {length}");

            Console.WriteLine("Starting FFmpeg processing...");
            FFMpegArguments? ffmpeg_vid = null;
            FFMpegArguments? ffmpeg_aud = null;
            if (videopath.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            {
                ffmpeg_vid = FFMpegArguments.FromUrlInput(new Uri(videopath));
                ffmpeg_aud = FFMpegArguments.FromUrlInput(new Uri(videopath));
            }
            else
            {
                ffmpeg_vid = FFMpegArguments.FromFileInput(videopath);
                ffmpeg_aud = FFMpegArguments.FromFileInput(videopath);
            }
            Task vid = ffmpeg_vid
                .OutputToPipe(new StreamPipeSinkAsync(vidstream.Writer), options =>
                    options.WithVideoFilters(vf => vf.Scale(width, height))
                           .WithCustomArgument("-pix_fmt monob")
                           .WithCustomArgument("-f rawvideo")
                           .WithFramerate(framerate)
                )
                .ProcessAsynchronously().ContinueWith((a) => vidstream.Writer.Complete());
            bool aud = await ffmpeg_aud
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
                    //byte[] ssdData = ConvertMonobToSSD1106(buffer, width, height);
                    // Write the SSD data to the stream


                    //ssdFrames.Add((byte[])buffer.Clone());
                    // Convert buffer to LSB (least significant bit first) before cloning
                    for (int i = 0; i < buffer.Length; i++)
                    {
                        byte b = buffer[i];
                        // Reverse bits in byte (MSB -> LSB)
                        b = (byte)((b * 0x0802U & 0x22110U | b * 0x8020U & 0x88440U) * 0x10101U >> 16);
                        buffer[i] = b;
                    }
                    ssdFrames.Add((byte[])buffer.Clone());
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
            waveOut.Volume = 0.7f; // Set volume to 50%
            waveOut.Play();

            int currentFrame = 0;
            int frameskips = 0;
            while (currentFrame < ssdFrames.Count && reader.Length != reader.Position)
            {
                TimeSpan expectedTime = frameDuration * currentFrame;

                if (reader.CurrentTime >= expectedTime)
                {
                    SendFrame(deviceLoop, ssdFrames[currentFrame]);
                    currentFrame++;
                }
                else
                {
                    await Task.Delay(1); // Wait for the next frame time
                }

                while (currentFrame < ssdFrames.Count &&
               (reader.CurrentTime - frameDuration * currentFrame).TotalMilliseconds > frameDuration.TotalMilliseconds * 2)
                {
                    frameskips++;
                    Console.Write($"Current Frame: {currentFrame}/{ssdFrames.Count} | Frame Skipped: {frameskips} | Processed Frames: {ssdFrames.Count}/{Math.Ceiling(length * framerate)}");
                    Console.CursorLeft = 0;

                    currentFrame++; // Skip to catch up
                }


            }
            deviceLoop.WriteWait(new Message(102, new byte[1]));


        }

    }
}
