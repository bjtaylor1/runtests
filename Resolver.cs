using System;
using System.Collections.Generic;
using System.Configuration;
using System.Linq;
using System.Reflection;

namespace RunTests
{
    public class Resolver
    {
        private readonly Dictionary<Type, Func<object>> factory;

        public Resolver(Dictionary<Type, Func<object>> factory)
        {
            this.factory = factory;
        }

        public object GetObject(Type type)
        {
            try
            {
                if (factory.TryGetValue(type, out var s)) return s();
                var constructors = type.GetConstructors(BindingFlags.Public | BindingFlags.Instance);
                var constructor = constructors.OrderBy(c => c.GetParameters().Length).FirstOrDefault();
                if (constructor == null)
                {
                    throw new InvalidOperationException($"Could not construct {type.FullName} as it does not have an appropriate constructor");
                }

                var parameters = constructor.GetParameters();
                var parameterValues = parameters.Select(GetParameterValue).ToArray();
                return constructor.Invoke(parameterValues);
            }
            catch (Exception e)
            {
                throw new InvalidOperationException($"Could not construct {type.FullName} (see inner exception)", e);
            }
        }

        private object GetParameterValue(ParameterInfo parameterInfo)
        {
            if (factory.TryGetValue(parameterInfo.ParameterType, out var s)) return s();
            
            if (parameterInfo.HasDefaultValue)
            {
                return parameterInfo.DefaultValue;
            }

            return GetObject(parameterInfo.ParameterType);
        }
    }
}