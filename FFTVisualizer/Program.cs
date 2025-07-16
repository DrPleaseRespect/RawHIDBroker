using System;
using System.Linq;
using System.Numerics;
using System.Threading;
using NAudio.Wave;
using FftSharp.Windows;
using FftSharp;
using RawHIDBroker.EventLoop;
using RawHIDBroker.Messaging;

class Program
{
    static AutoResetEvent dataReadyEvent = new AutoResetEvent(false);
    static WasapiLoopbackCapture capture;
    static BufferedWaveProvider buffer;
    static int fftSize = 4096;
    static int barCount = 128 / 4;
    static double[] window;
    static object lockObj = new object();
    static DeviceLoop deviceLoop = new DeviceLoop(0xFEED, 0x0000);
    static Complex[] ComplexBuf;
    static float[] SamplesBuf = new float[fftSize];
    static byte[] AudioBytes = new byte[fftSize * sizeof(float)];
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

    static void Main()
    {
        deviceLoop.Start();
        deviceLoop.Write(new Message(100, new byte[1]));
        capture = new WasapiLoopbackCapture();
        buffer = new BufferedWaveProvider(capture.WaveFormat)
        {
            BufferLength = fftSize * sizeof(float),
            DiscardOnBufferOverflow = true
        };

        window = new Hanning().Create(fftSize);

        capture.DataAvailable += OnDataAvailable;
        capture.StartRecording();

        Console.Clear();
        Console.CursorVisible = false;
        Task.Run(() =>
        {
            var samples = SamplesBuf.AsSpan();
            var bytes = AudioBytes;
            while (true)
            {
                if (buffer.BufferedBytes < fftSize * sizeof(float))
                {
                    dataReadyEvent.WaitOne();
                    continue;
                }
                lock (lockObj)
                {

                    buffer.Read(bytes, 0, bytes.Length);

                    int channels = capture.WaveFormat.Channels;
                    int totalFrames = fftSize;

                    if (channels > 1)
                    {
                        // Stereo or multichannel — average channels
                        for (int i = 0; i < totalFrames; i++)
                        {
                            float sum = 0;
                            for (int ch = 0; ch < channels; ch++)
                            {
                                int sampleIndex = (i * channels + ch) * 4;
                                if (sampleIndex + 4 <= bytes.Length)
                                    sum += BitConverter.ToSingle(bytes, sampleIndex);
                            }
                            samples[i] = sum / channels;
                        }
                    }
                    else
                    {
                        // Mono
                        for (int i = 0; i < totalFrames; i++)
                            samples[i] = BitConverter.ToSingle(bytes, i * 4);
                    }

                }

                // Apply window
                if (ComplexBuf == null)
                {
                    ComplexBuf = new Complex[fftSize];
                }
                else if (ComplexBuf.Length != fftSize)
                {
                    Array.Resize(ref ComplexBuf, fftSize);
                }

                for (int i = 0; i < fftSize; i++)
                    ComplexBuf[i] = samples[i] * window[i];

                // Perform FFT
                //Complex[] fftResult = FFT.Forward(windowed);
                FFT.Forward(ComplexBuf.AsSpan());

                // Calculate magnitude spectrum
                
                double[] magnitudes = ComplexBuf
                    .Take(ComplexBuf.Length / 2)
                    .Select(c => c.Magnitude)
                    .ToArray();

                // Calculate Power Spectrum
                //double[] powers = FFT.Power(fftResult.Take(fftResult.Length / 2).ToArray());

                DrawBars(magnitudes);
            }
        });
        Console.WriteLine("Press any key to exit...");
        Console.ReadKey();
    }

    static void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        lock (lockObj)
        {
            buffer.AddSamples(e.Buffer, 0, e.BytesRecorded);
            if (buffer.BufferedBytes >= fftSize * sizeof(float))
            {
                dataReadyEvent.Set();
            }
        }
    }

    static byte[] ConvertBarsToSSD1306(int[] barHeights)
    {
        const int displayWidth = 128;
        const int displayHeight = 64;
        const int pages = displayHeight / 8;
        const int barWidth = 4;
        const int barCount = displayWidth / barWidth;

        byte[] buffer = new byte[displayWidth * pages];

        for (int barIndex = 0; barIndex < Math.Min(barHeights.Length, barCount); barIndex++)
        {
            int height = Math.Clamp(barHeights[barIndex], 0, displayHeight);

            for (int y = 0; y < height; y++)
            {
                int pixelY = displayHeight - 1 - y; // OLED origin is top-left, so we draw bottom-up
                int page = pixelY / 8;
                int bitInByte = pixelY % 8;

                // Write 4 horizontal pixels for this bar (makes bar wider)
                for (int dx = 0; dx < barWidth; dx++)
                {
                    int x = barIndex * barWidth + dx;
                    if (x >= displayWidth)
                        continue;

                    int bufferIndex = page * displayWidth + x;
                    buffer[bufferIndex] |= (byte)(1 << bitInByte);
                }
            }
        }

        return buffer;
    }


    static void DrawBars(double[] spectrum)
    {
        int maxRow = 32;
        int[] barHeights = new int[barCount];

        // FFT settings
        double sampleRate = capture.WaveFormat.SampleRate;
        int fftBins = spectrum.Length;

        // Frequency range
        double minFreq = 60;
        double maxFreq = sampleRate / 2; // Nyquist

        // Create log-spaced frequency bands
        double logMin = Math.Log10(minFreq);
        double logMax = Math.Log10(maxFreq);

        for (int i = 0; i < barCount; i++)
        {
            // Get frequency range for this bar
            double logStart = logMin + (logMax - logMin) * i / barCount;
            double logEnd = logMin + (logMax - logMin) * (i + 1) / barCount;

            double freqStart = Math.Pow(10, logStart);
            double freqEnd = Math.Pow(10, logEnd);

            // Convert frequencies to bin indices
            int binStart = (int)(freqStart / (sampleRate / 2) * fftBins);
            int binEnd = (int)(freqEnd / (sampleRate / 2) * fftBins);
            int maxBinWidth = 12;
            if (binEnd - binStart > maxBinWidth)
                binEnd = binStart + maxBinWidth;
            binStart = Math.Clamp(binStart, 0, fftBins - 1);
            binEnd = Math.Clamp(binEnd, binStart + 1, fftBins);

            // Average bins in range
            double avg = 0;
            for (int b = binStart; b < binEnd; b++)
                avg += spectrum[b];

            avg /= (binEnd - binStart);

            // Apply log scaling for height
            int height = (int)(Math.Log10(avg + 1) * 8);
            barHeights[i] = Math.Clamp(height, 0, maxRow);
            barHeights[i] *= 3;
        }

        // Render to console
        //Console.SetCursorPosition(0, 0);
        //for (int row = maxRow; row >= 0; row--)
        //{
        //    for (int i = 0; i < barCount; i++)
        //        Console.Write(barHeights[i] >= row ? '█' : ' ');
        //    Console.WriteLine();
        //}
        byte[] frame = ConvertBarsToSSD1306(barHeights);
        SendFrame(deviceLoop,frame); // or store/send somewhere

    }

}
