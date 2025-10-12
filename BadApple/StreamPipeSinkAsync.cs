using FFMpegCore.Pipes;
using System.IO.Pipelines;

namespace RawHIDBroker.Demos.BadApple
{
    class StreamPipeSinkAsync : IPipeSink
    {
        private readonly PipeWriter _writer;
        private readonly string _format;

        public StreamPipeSinkAsync(PipeWriter writer, string format = "rawvideo")
        {
            _writer = writer;
            _format = format;
        }

        public string GetFormat() => _format;

        public async Task ReadAsync(Stream inputStream, CancellationToken cancellationToken = default)
        {
            // FFmpeg writes its output here, and we forward it to the PipeWriter
            await inputStream.CopyToAsync(_writer, cancellationToken);
        }
    }
}
