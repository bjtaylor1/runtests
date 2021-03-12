using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;

namespace RunTests
{
    class Program
    {
        static async Task Main(string[] args)
        {
            var testTargets = JsonSerializer.Deserialize<TestTarget[]>(File.ReadAllText("testTargets.json"));
            AppDomain.CurrentDomain.AssemblyResolve += CurrentDomainOnAssemblyResolve;
            foreach (var testTarget in testTargets)
            {
                if (!File.Exists(testTarget.Assembly))
                {
                    throw new FileNotFoundException("Assembly not found", testTarget.Assembly);
                }
                
                
                var asm = Assembly.LoadFile(testTarget.Assembly);
                foreach (var type in asm.GetTypes())
                {
                    await Console.Out.WriteLineAsync(type.Name);
                }
            }            
        }

        private static Assembly CurrentDomainOnAssemblyResolve(object sender, ResolveEventArgs args)
        {
            var assemblyName = new AssemblyName(args.Name);
            var requestingAssemblyFileInfo = new FileInfo(args.RequestingAssembly.Location);
            var resolvedAssemblyFile = $"{requestingAssemblyFileInfo.DirectoryName}\\{assemblyName.Name}.dll";
            if (!File.Exists(resolvedAssemblyFile))
            {
                throw new FileNotFoundException("Dependent assembly not found", resolvedAssemblyFile);
            }
            return Assembly.LoadFile(resolvedAssemblyFile);
        }

        private static async Task BuildTargets(TestTarget[] targets)
        {
        }
    }

    public class TestTarget
    {
        public string Assembly { get; set; } 
    }
}