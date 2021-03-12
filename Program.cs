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
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Xunit;
using Xunit.Abstractions;
using Xunit.Sdk;

namespace RunTests
{
    class Program
    {
        static async Task Main(string[] args)
        {
            var assemblyFileInfo = new FileInfo(Assembly.GetExecutingAssembly().Location);
            var testTargets = JsonSerializer.Deserialize<TestTarget[]>(File.ReadAllText(Path.Combine(assemblyFileInfo.DirectoryName, "testTargets.json")));
            AppDomain.CurrentDomain.AssemblyResolve += CurrentDomainOnAssemblyResolve;
            using var logFile = new FileStream("testoutput.log", FileMode.Create, FileAccess.Write);
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
                        methods.RemoveAll(m => !args.Any(a => $"{type.FullName}.{m.Name}".ToLower().Contains(a.ToLower())));
                    }
                    
                    if(methods.Any())
                    {
                        if (!loadedConfig)
                        {
                            LoadConfig(asm);
                            loadedConfig = true;
                        }

                        var resolver = new Resolver();
                        foreach (var method in methods)
                        {
                            var outputCollector = new OutputCollector(type, method);
                            resolver.Replace(typeof(ITestOutputHelper), () => outputCollector);
                            resolver.Flush(type);
                            RunTest(type, method, resolver, outputCollector);
                            await outputCollector.Collect(logFile);
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
            try
            {
                Console.ForegroundColor = ConsoleColor.Gray;
                Console.Write($"{type.Name}.{methodInfo.Name}...");
                var testFixture = resolver.GetObject(type);
                methodInfo.Invoke(testFixture, new object[] { });
                Console.ForegroundColor = ConsoleColor.Green;
                Console.Write($"\r\u2713");
                Console.ForegroundColor = ConsoleColor.White;
                Console.WriteLine($" {type.Name}.{methodInfo.Name}");
            }
            catch (Exception e)
            {
                if (e is TargetInvocationException targetInvocationException) e = targetInvocationException.InnerException;
                var exceptionConsoleMessage = GetExceptionConsoleMessage(e);
                outputCollector.WriteLine(e.ToString());
                Console.ForegroundColor = ConsoleColor.Red;
                Console.Write($"\rX");
                Console.ForegroundColor = ConsoleColor.White;
                Console.Write($" {type.Name}.{methodInfo.Name}");
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($" {exceptionConsoleMessage}");
            }
            finally
            {
                Console.ResetColor();
            }
        }

        private static object GetExceptionConsoleMessage(Exception e)
        {
            if (e is XunitException xe) return Regex.Replace(xe.Message, "\\s+", " ");
            return e.GetType().Name;
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