﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Smartstore.Engine.Modularity;

namespace Smartstore.Engine
{
    /// <inheritdoc/>
    public class DefaultTypeScanner : ITypeScanner
    {
        private HashSet<Assembly> _activeAssemblies = new();

        public DefaultTypeScanner(params Assembly[] assemblies)
        {
            _activeAssemblies.AddRange(assemblies);

            // No edit allowed from now on
            Assemblies = assemblies.AsReadOnly();
        }

        public DefaultTypeScanner(IEnumerable<Assembly> coreAssemblies, IModuleCatalog moduleCatalog, ILogger logger)
        {
            Guard.NotNull(coreAssemblies, nameof(coreAssemblies));
            Guard.NotNull(moduleCatalog, nameof(moduleCatalog));
            Guard.NotNull(logger, nameof(logger));

            Logger = logger;

            var assemblies = new HashSet<Assembly>(coreAssemblies);

            // Add all module assemblies to assemblies list
            assemblies.AddRange(moduleCatalog.GetInstalledModules().Select(x => x.Module.Assembly));

            // No edit allowed from now on
            Assemblies = assemblies.AsReadOnly();
        }

        public ILogger Logger { get; set; } = NullLogger.Instance;

        /// <inheritdoc/>
        public IEnumerable<Assembly> Assemblies { get; private set; }

        /// <inheritdoc/>
        public IEnumerable<Type> FindTypes(Type baseType, bool concreteTypesOnly = true)
        {
            Guard.NotNull(baseType, nameof(baseType));

            return FindTypes(baseType, Assemblies, concreteTypesOnly);
        }

        /// <inheritdoc/>
        public IEnumerable<Type> FindTypes(Type baseType, IEnumerable<Assembly> assemblies, bool concreteTypesOnly = true)
        {
            Guard.NotNull(baseType, nameof(baseType));

            foreach (var t in assemblies.SelectMany(x => x.GetLoadableTypes()))
            {
                if (t.IsInterface || t.IsDelegate() || t.IsCompilerGenerated())
                    continue;

                if (baseType.IsAssignableFrom(t) || t.IsOpenGenericTypeOf(baseType))
                {
                    if (concreteTypesOnly)
                    {
                        if (t.IsClass && !t.IsAbstract)
                        {
                            yield return t;
                        }
                    }
                    else
                    {
                        yield return t;
                    }
                }
            }
        }
    }
}
