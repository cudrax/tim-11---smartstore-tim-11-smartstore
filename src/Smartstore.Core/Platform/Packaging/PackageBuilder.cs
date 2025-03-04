﻿using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Smartstore.Engine;
using Smartstore.Engine.Modularity;
using Smartstore.Utilities;

namespace Smartstore.Core.Packaging
{
    public partial class PackageBuilder : IPackageBuilder
    {
        private static readonly Wildcard[] _ignoredPaths = new[] 
        {
            "/obj/*", "/ref/*", "/refs/*",
            "*.obj", "*.pdb", "*.exclude", "*.cs", "*.deps.json"
        }.Select(x => new Wildcard(x)).ToArray();

        private readonly IApplicationContext _appContext;
        
        public PackageBuilder(IApplicationContext appContext)
        {
            _appContext = appContext;
        }

        public async Task<ExtensionPackage> BuildPackageAsync(IExtensionDescriptor extension)
        {
            Guard.NotNull(extension, nameof(extension));
            
            if (extension is not IExtensionLocation location)
            {
                throw new InvalidExtensionException();
            }

            var manifest = new MinimalExtensionDescriptor(extension);

            var package = new ExtensionPackage(new MemoryStream(), manifest, true);

            using (var archive = new ZipArchive(package.ArchiveStream, ZipArchiveMode.Create, true))
            {
                // Embed core manifest file
                await EmbedManifest(archive, manifest);

                // Embed all extension files
                await EmbedFiles(archive, extension);
            }

            package.ArchiveStream.Seek(0, SeekOrigin.Begin);

            return package;
        }

        private async Task EmbedManifest(ZipArchive archive, MinimalExtensionDescriptor manifest)
        {
            var json = JsonConvert.SerializeObject(manifest, new JsonSerializerSettings 
            {
                Formatting = Formatting.Indented,
                NullValueHandling = NullValueHandling.Ignore
            });

            var memStream = new MemoryStream();
            using (var streamWriter = new StreamWriter(memStream, leaveOpen: true))
            {
                await streamWriter.WriteAsync(json);
            }

            memStream.Seek(0, SeekOrigin.Begin);
            await CreateArchiveEntry(archive, PackagingUtility.ManifestFileName, memStream);
        }

        private async Task EmbedFiles(ZipArchive archive, IExtensionDescriptor extension)
        {
            var location = extension as IExtensionLocation;

            foreach (var file in _appContext.ContentRoot.EnumerateFiles(location.Path, deep: true))
            {
                // Skip ignores files
                if (IgnoreFile(file.SubPath))
                {
                    continue;
                }

                await CreateArchiveEntry(archive, file.SubPath, file.OpenRead());
            }
        }

        private static async Task CreateArchiveEntry(ZipArchive archive, string name, Stream source)
        {
            var entry = archive.CreateEntry(name, CompressionLevel.Optimal);
            using var entryStream = entry.Open();

            using (source)
            {
                await source.CopyToAsync(entryStream);
            }
        }

        private static bool IgnoreFile(string filePath)
        {
            return string.IsNullOrEmpty(filePath) || _ignoredPaths.Any(x => x.IsMatch(filePath));
        }
    }
}
