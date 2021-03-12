using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Reflection;

namespace RunTests
{
    public class Resolver
    {
        private readonly ConcurrentDictionary<Type, Func<object>> store = new ConcurrentDictionary<Type, Func<object>>();

        public void Flush(Type t) => store.TryRemove(t, out _);
        public void Replace(Type t, Func<object> newVal) => store.AddOrUpdate(t, newVal, (t, old) => newVal);

        public object GetObject(Type type)
        {
            return store.GetOrAdd(type, ObjectFactory)();
        }

        private Func<object> ObjectFactory(Type type)
        {
            try
            {
                var constructors = type.GetConstructors(BindingFlags.Public | BindingFlags.Instance);
                var constructor = constructors.OrderBy(c => c.GetParameters().Length).FirstOrDefault();
                if (constructor == null)
                {
                    throw new InvalidOperationException($"Could not construct {type.FullName} as it does not have an appropriate constructor");
                }

                var parameters = constructor.GetParameters();
                var parameterValues = parameters.Select(GetParameterValue).ToArray();
                return () => constructor.Invoke(parameterValues);
            }
            catch (Exception e)
            {
                throw new InvalidOperationException($"Could not construct {type.FullName} (see inner exception)", e);
            }
        }

        private object GetParameterValue(ParameterInfo parameterInfo)
        {
            if (store.TryGetValue(parameterInfo.ParameterType, out var s)) return s();
            
            if (parameterInfo.HasDefaultValue)
            {
                return parameterInfo.DefaultValue;
            }

            return GetObject(parameterInfo.ParameterType);
        }
    }
}