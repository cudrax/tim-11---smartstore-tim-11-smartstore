﻿using System;
using System.Collections.Concurrent;
using System.IO;
using System.Reflection;
using System.Runtime.Loader;
using Smartstore.IO;

namespace Smartstore.Engine.Modularity
{
    /// <summary>
    /// Resolves private module references
    /// </summary>
    internal class ModuleReferenceResolver
    {
        private readonly ConcurrentDictionary<Assembly, IModuleDescriptor> _assemblyModuleMap = new();
        private readonly IApplicationContext _appContext;

        public ModuleReferenceResolver(IApplicationContext appContext)
        {
            _appContext = appContext;
        }

        /// <summary>
        /// Tries to resolve and load a module reference assembly.
        /// </summary>
        /// <param name="requestingAssembly">The requesting assembly. May be the module main assembly or any dependency of it.</param>
        /// <param name="name">Name of assembly to resolve.</param>
        /// <returns></returns>
        public Assembly ResolveAssembly(Assembly requestingAssembly, string name)
        {
            if (_appContext.ModuleCatalog == null)
            {
                return null;
            }
            
            Assembly assembly = null;

            if (!_assemblyModuleMap.TryGetValue(requestingAssembly, out var module))
            {
                module = _appContext.ModuleCatalog.GetModuleByAssembly(requestingAssembly);
            }

            if (module != null)
            {
                var requestedAssemblyName = name.Split(',', StringSplitOptions.RemoveEmptyEntries)[0] + ".dll";
                var fullPath = PathUtility.Combine(module.PhysicalPath, requestedAssemblyName);
                if (File.Exists(fullPath))
                {
                    // TODO: (core) ModuleReferenceResolver ErrHandling
                    assembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(fullPath);
                    _assemblyModuleMap[assembly] = module;
                }
            }

            return assembly;
        }
    }
}
