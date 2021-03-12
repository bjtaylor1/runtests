using System;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using Xunit.Abstractions;

namespace RunTests
{
    public class OutputCollector : ITestOutputHelper, IDisposable
    {
        private readonly Type type;
        private readonly MethodInfo method;
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

        public OutputCollector(Type type, MethodInfo method)
        {
            this.type = type;
            this.method = method;
        }

        public async Task Collect(Stream outputStream)
        {
            if (hasWritten)
            {
                using var collectStream = new MemoryStream();
                using var collectStreamWriter = new StreamWriter(collectStream);
                await collectStreamWriter.WriteLineAsync($"{type.FullName}.{method.Name}:");
                await collectStreamWriter.WriteLineAsync();
                await collectStreamWriter.WriteLineAsync();
                await collectStreamWriter.WriteLineAsync(stringWriter.ToString());
                await collectStreamWriter.FlushAsync();
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