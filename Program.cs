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
using Xunit.Abstractions;
using Xunit.Sdk;
using DataAttribute = Xunit.Sdk.DataAttribute;
using TheoryAttribute = Xunit.TheoryAttribute;

namespace RunTests
{
    class Program
    {
        static async Task Main(string[] args)
        {
            Console.CancelKeyPress += (sender, eventArgs) => Console.ResetColor();

            var assemblyFileInfo = new FileInfo(Assembly.GetExecutingAssembly().Location);
            if (args.Length < 1)
            {
                await Console.Out.WriteLineAsync("Usage: runtests targets.json [filter1] [filter2] (filters are ANDed together - to OR, use a pipe in a regex)");
            }
            var testTargets = JsonSerializer.Deserialize<TestTarget[]>(File.ReadAllText(Path.Combine(assemblyFileInfo.DirectoryName, args[0])));
            var filters = args.Skip(1).ToArray();

            if (filters.Any())
            {
                await Console.Out.WriteLineAsync("Using filters:");
                foreach (var filter in filters)
                {
                    await Console.Out.WriteLineAsync(filter);
                }
            }

            AppDomain.CurrentDomain.AssemblyResolve += CurrentDomainOnAssemblyResolve;
            using var logFile = new FileStream("testoutput.log", FileMode.Create, FileAccess.Write);
            int numPassed = 0, numFailed = 0;
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
                    var methods = type.GetMethods(BindingFlags.Instance | BindingFlags.Public).ToList();
                    if (filters.Any())
                    {
                        methods.RemoveAll(m => !filters.All(a => Regex.IsMatch($"{type.FullName}.{m.Name}", a, RegexOptions.IgnoreCase)));
                    }
                    var methodsToRun = methods.Where(IsRunnableTestMethod).ToArray();
                    
                    if(methodsToRun.Any())
                    {
                        if (!loadedConfig)
                        {
                            LoadConfig(asm);
                            loadedConfig = true;
                        }

                        Console.ForegroundColor = ConsoleColor.White;
                        await Console.Out.WriteLineAsync($"{type.Name}:");
                        Console.ResetColor();

                        var resolver = new Resolver();
                        foreach (var method in methodsToRun)
                        {
                            foreach (var paramSet in GetParametersToRunWith(method))
                            {
                                var outputCollector = new OutputCollector(type, method, paramSet.Suffix);
                                resolver.Replace(typeof(ITestOutputHelper), () => outputCollector);
                                resolver.Flush(type);
                                if (RunTest(type, method, paramSet.Parameters.ToArray(), resolver, outputCollector, paramSet.Suffix))
                                    numPassed++;
                                else numFailed++;
                                await outputCollector.Collect(logFile);
                            }
                        }
                        await Console.Out.WriteLineAsync();
                    }
                }
            }

            if (numFailed > 0) Console.ForegroundColor = ConsoleColor.Red; else Console.ForegroundColor = ConsoleColor.Green;
            await Console.Out.WriteLineAsync($"Passed: {numPassed} Failed: {numFailed}");
            Console.ResetColor();
        }

        private static void LoadConfig(Assembly asm)
        {
            var config = ConfigurationManager.OpenExeConfiguration(asm.Location);
            foreach(KeyValueConfigurationElement appSetting in config.AppSettings.Settings)
            {
                ConfigurationManager.AppSettings[appSetting.Key] = appSetting.Value;
            }
        }

        private static IEnumerable<MethodInfo> GetClassSetups(Type type) =>
            type.GetMethods(BindingFlags.Public | BindingFlags.Instance).Where(m => m.GetCustomAttributes().Any(a => new[] {"OneTimeSetUpAttribute"}.Contains(a.GetType().Name))).ToArray();

        private static IEnumerable<MethodInfo> GetMethodSetups(Type type) =>
            type.GetMethods(BindingFlags.Public | BindingFlags.Instance).Where(m => m.GetCustomAttributes().Any(a => new[] {"SetUpAttribute"}.Contains(a.GetType().Name))).ToArray();

        private static IEnumerable<MethodInfo> GetSetups(Type type) => GetClassSetups(type).Concat(GetMethodSetups(type)); // < class ones first

        private static bool RunTest(Type type, MethodInfo methodInfo, object[] args, Resolver resolver, OutputCollector outputCollector, string suffix = "")
        {
            try
            {
                Console.ForegroundColor = ConsoleColor.Cyan;
                Console.Write($"  {methodInfo.Name}{suffix}...");
                var testFixture = resolver.GetObject(type);
                var setups = GetSetups(type).ToArray();
                foreach (var setup in setups)
                {
                    setup.Invoke(testFixture, new object[] { });
                }
                methodInfo.Invoke(testFixture, args);
                Console.ForegroundColor = ConsoleColor.Green;
                Console.Write($"\r\u2713");
                Console.ForegroundColor = ConsoleColor.White;
                Console.WriteLine($" {methodInfo.Name}{suffix}   ");
                return true;
            }
            catch (Exception e)
            {
                if (e is TargetInvocationException targetInvocationException) e = targetInvocationException.InnerException;
                var exceptionConsoleMessage = GetExceptionConsoleMessage(e);
                outputCollector.WriteLine(e.ToString());
                var responseContentProperty = e.GetType().GetProperty("ResponseContent", BindingFlags.Public | BindingFlags.Instance);
                if (responseContentProperty != null && responseContentProperty.PropertyType == typeof(string))
                {
                    var responseContent = (string)responseContentProperty.GetValue(e);
                    outputCollector.WriteLine("======Response follows======");
                    var responseContentLines = Regex.Split(responseContent, @"\\r\\n|\r\n", RegexOptions.Multiline).ToArray();  //could be an actual \r\n, or could be a literal
                    foreach (var line in responseContentLines)
                    {
                        outputCollector.WriteLine(line);
                    }
                    outputCollector.WriteLine("============================");
                }
                Console.ForegroundColor = ConsoleColor.Red;
                Console.Write($"\rX");
                Console.ForegroundColor = ConsoleColor.White;
                Console.Write($" {methodInfo.Name}{suffix}");
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($" {exceptionConsoleMessage}");
                return false;
            }
            finally
            {
                Console.ResetColor();
                outputCollector.WriteLine(string.Empty);
                outputCollector.WriteLine(string.Empty);
            }
        }

        private static object GetExceptionConsoleMessage(Exception e)
        {
            if (e is XunitException || "ShouldAssertException".Equals(e.GetType().Name))
                return Regex.Replace(e.Message, "\\s+", " ")
                    .Replace("Shouldly uses your source code to generate its great error messages, build your test project with full debug information to get better error messages", "")
                    .Trim();
            return e.GetType().Name;
        }

        private static bool IsRunnableTestMethod(MemberInfo method)
        {
            var customAttributes = method.GetCustomAttributes().ToArray();
            var attributeNames = customAttributes.Select(a => a.GetType().Name);
            var retval = attributeNames.Any(n => new[]
            {
                "FactAttribute", "RetrySkippableFactAttribute", "TestAttribute", "TestCaseAttribute", "TheoryAttribute"
            }.Contains(n));
            return retval;
        }

        private static IEnumerable<(List<object> Parameters, string Suffix)> GetParametersToRunWith(MethodInfo method)
        {
            var parametersNoDefaults = GetParametersToRunWithNoDefaults(method).ToList();
            var parametersRequired = method.GetParameters();
            foreach (var parameter in parametersNoDefaults)
            {
                for (int i = parameter.Parameters.Count; i < parametersRequired.Length; i++)
                {
                    if (!parametersRequired[i].HasDefaultValue)
                    {
                        throw new InvalidOperationException($"Parameter {i} not specified and has no default value");
                    }

                    parameter.Parameters.Add(parametersRequired[i].DefaultValue);
                }
            }
            return parametersNoDefaults;
        }

        private static IEnumerable<(List<object> Parameters, string Suffix)> GetParametersToRunWithNoDefaults(MethodInfo method)
        {
            DataAttribute[] dataAttributes; // xunit 'InlineData'
            Attribute[] testCaseAttributes;
            var parameterTypes = method.GetParameters().Select(p => p.ParameterType).ToArray();

            if ((dataAttributes = method.GetCustomAttributes<DataAttribute>(inherit: true).ToArray()).Any())
            {
                return dataAttributes.SelectMany(d => { return d.GetData(method).Select(p => (p.Select((pv, i) => ParameterInCorrectType(pv, parameterTypes[i])).ToList(), GetParametersDescription(p))); });
            }
            else if ((testCaseAttributes = method.GetCustomAttributes().Where(t => t.GetType().Name == "TestCaseAttribute").ToArray()).Any())
            {
                var argumentsProperty = testCaseAttributes.First().GetType().GetProperty("Arguments", BindingFlags.Instance | BindingFlags.Public);
                if (argumentsProperty == null) throw new InvalidOperationException("No Arguments property on TestCaseAttribute");
                object[] TestCaseArguments(Attribute a) => (object[]) argumentsProperty.GetValue(a);
                return testCaseAttributes.Select(testCase => (TestCaseArguments(testCase).Select((pv, i) => ParameterInCorrectType(pv, parameterTypes[i])).ToList(), GetParametersDescription(TestCaseArguments(testCase)))).ToArray();
            }
            else
            {
                return new[] {(new List<object>(), string.Empty)};
            }
        }

        private static object ParameterInCorrectType(object specifiedValue, Type parameterType)
        {
            if (specifiedValue == null) return specifiedValue;
            Type typeToConvertTo;
            if (parameterType.IsGenericType && parameterType.GetGenericTypeDefinition() == typeof(Nullable<>))
            {
                typeToConvertTo = parameterType.GetGenericArguments()[0];
            }
            else
            {
                typeToConvertTo = parameterType;
            }
            return Convert.ChangeType(specifiedValue, typeToConvertTo);
        }

        private static string GetParametersDescription(object[] parameters)
        {
            return $" ({string.Join(", ", parameters.Select(GetParameterDescription))})";
        }

        private static string GetParameterDescription(object parameter)
        {
            if (parameter == null) return "null";
            if (parameter is string s) return $"\"{s}\"";
            return parameter.ToString();
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
    }

    public class TestTarget
    {
        public string Assembly { get; set; } 
    }
}