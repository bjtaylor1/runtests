using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Configuration;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;
using Xunit.Abstractions;

namespace RunTests
{
    class Program
    {
        static async Task Main(string[] args)
        {
            var testTargets = JsonSerializer.Deserialize<TestTarget[]>(File.ReadAllText("testTargets.json"));
            AppDomain.CurrentDomain.AssemblyResolve += CurrentDomainOnAssemblyResolve;
            var outputCollectors = new ConcurrentBag<OutputCollector>();
            foreach (var testTarget in testTargets)
            {
                if (!File.Exists(testTarget.Assembly))
                {
                    throw new FileNotFoundException("Assembly not found", testTarget.Assembly);
                }
                
                var asm = Assembly.LoadFile(testTarget.Assembly);
                var loadedConfig = false;
                foreach (var type in asm.GetTypes().Where(t => t.IsPublic))
                {
                    var methods = type.GetMethods(BindingFlags.Instance | BindingFlags.Public).Where(IsRunnableTestMethod).ToList();
                    if (args.Any())
                    {
                        methods.RemoveAll(m => !args.Any(a => $"{type.FullName}.{m.Name}".Contains(a)));
                    }
                    
                    if(methods.Any())
                    {
                        if (!loadedConfig)
                        {
                            LoadConfig(asm);
                            loadedConfig = true;
                        }

                        var outputCollector = new OutputCollector(type);
                        outputCollectors.Add(outputCollector);
                        var resolver = new Resolver(new Dictionary<Type, Func<object>>
                        {
                            {typeof(ITestOutputHelper), () => outputCollector}
                        });
                        foreach (var method in methods)
                        {
                            RunTest(type, method, resolver, outputCollector);
                        }
                    }
                }
                
            }            
        }

        private static void LoadConfig(Assembly asm)
        {
            var config = ConfigurationManager.OpenExeConfiguration(asm.Location);
            foreach(KeyValueConfigurationElement appSetting in config.AppSettings.Settings)
            {
                ConfigurationManager.AppSettings[appSetting.Key] = appSetting.Value;
            }
        }

        private static void RunTest(Type type, MethodInfo methodInfo, Resolver resolver, OutputCollector outputCollector)
        {
            var oldColor = Console.ForegroundColor;
            try
            {
                Console.ForegroundColor = ConsoleColor.Gray;
                Console.Write($"{type.Name}.{methodInfo.Name}...");
                var testFixture = resolver.GetObject(type);
                methodInfo.Invoke(testFixture, new object[] { });
                Console.ForegroundColor = ConsoleColor.Green;
                Console.Write($"\r\u2713");
                Console.ForegroundColor = oldColor;
                Console.WriteLine($" {type.Name}.{methodInfo.Name}");
            }
            catch (Exception e)
            {
                if (e is TargetInvocationException targetInvocationException) e = targetInvocationException.InnerException;
                outputCollector.WriteLine(e.ToString());
                Console.ForegroundColor = ConsoleColor.Red;
                Console.Write($"\rX");
                Console.ForegroundColor = oldColor;
                Console.WriteLine($" {type.Name}.{methodInfo.Name} {e.GetType().FullName}");
            }
        }

        private static bool IsRunnableTestMethod(MemberInfo method)
        {
            //might want to add 'Theory'
            var retval = method.GetCustomAttribute<FactAttribute>() != null;
            return retval;
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