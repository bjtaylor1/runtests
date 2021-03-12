using System;
using System.IO;
using System.Threading.Tasks;
using Xunit.Abstractions;

namespace RunTests
{
    public class OutputCollector : ITestOutputHelper, IDisposable
    {
        private readonly Type type;
        private readonly StringWriter stringWriter = new StringWriter();
        private bool hasWritten = false;
        
        public void WriteLine(string message)
        {
            stringWriter.WriteLine(message);
            hasWritten = true;
        }

        public void WriteLine(string format, params object[] args)
        {
            stringWriter.WriteLine(format, args);
            hasWritten = true;
        }

        public OutputCollector(Type type)
        {
            this.type = type;
        }

        public async Task Collect(Stream outputStream)
        {
            if (hasWritten)
            {
                using var collectStream = new MemoryStream();
                using (var collectStreamWriter = new StreamWriter(collectStream))
                {
                    await collectStreamWriter.WriteLineAsync($"{type.FullName}:");
                    await collectStreamWriter.WriteLineAsync();
                    await collectStreamWriter.WriteLineAsync();
                    await collectStreamWriter.WriteLineAsync(stringWriter.ToString());
                }

                collectStream.Seek(0, SeekOrigin.Begin);
                await collectStream.CopyToAsync(outputStream);
            }
        }

        public void Dispose()
        {
            stringWriter?.Dispose();
        }
    }
}