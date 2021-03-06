using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Reflection;
using Protobuild.Tasks;
using fastJSON;

namespace Protobuild
{
    public class PackageManager : IPackageManager
    {
        private readonly IPackageCache m_PackageCache;

        private readonly IPackageLookup _packageLookup;

        private readonly IPackageLocator m_PackageLocator;

        private readonly IPackageGlobalTool m_PackageGlobalTool;

        private readonly IPackageRedirector packageRedirector;

        private readonly IFeatureManager _featureManager;

        private readonly IModuleExecution _moduleExecution;

        public const string ARCHIVE_FORMAT_TAR_LZMA = "tar/lzma";

        public const string ARCHIVE_FORMAT_TAR_GZIP = "tar/gzip";

        public const string PACKAGE_TYPE_LIBRARY = "library";

        public const string PACKAGE_TYPE_TEMPLATE = "template";

        public const string PACKAGE_TYPE_GLOBAL_TOOL = "global-tool";

        public PackageManager(
            IPackageCache packageCache,
            IPackageLookup packageLookup,
            IPackageLocator packageLocator,
            IPackageGlobalTool packageGlobalTool,
            IPackageRedirector packageRedirector,
            IFeatureManager featureManager,
            IModuleExecution moduleExecution)
        {
            this.packageRedirector = packageRedirector;
            this.m_PackageCache = packageCache;
            _packageLookup = packageLookup;
            this.m_PackageLocator = packageLocator;
            this.m_PackageGlobalTool = packageGlobalTool;
            _featureManager = featureManager;
            _moduleExecution = moduleExecution;
        }

        public void ResolveAll(ModuleInfo module, string platform)
        {
            if (!_featureManager.IsFeatureEnabled(Feature.PackageManagement))
            {
                return;
            }

            Console.WriteLine("Starting resolution of packages for " + platform + "...");

            if (module.Packages != null && module.Packages.Count > 0)
            {
                foreach (var submodule in module.Packages)
                {
                    if (submodule.IsActiveForPlatform(platform))
                    {
                        Console.WriteLine("Resolving: " + submodule.Uri);
                        this.Resolve(module, submodule, platform, null, null);
                    }
                    else
                    {
                        Console.WriteLine("Skipping resolution for " + submodule.Uri + " because it is not active for this target platform");
                    }
                }
            }

            foreach (var submodule in module.GetSubmodules(platform))
            {
                if (submodule.Packages.Count == 0 && submodule.GetSubmodules(platform).Length == 0)
                {
                    if (_featureManager.IsFeatureEnabledInSubmodule(module, submodule, Feature.OptimizationSkipResolutionOnNoPackagesOrSubmodules))
                    {
                        Console.WriteLine(
                            "Skipping package resolution in submodule for " + submodule.Name + " (there are no submodule or packages)");
                        continue;
                    }
                }

                Console.WriteLine(
                    "Invoking package resolution in submodule for " + submodule.Name);
                _moduleExecution.RunProtobuild(
                    submodule, 
                    _featureManager.GetFeatureArgumentToPassToSubmodule(module, submodule) + 
                    "-resolve " + platform + " " + packageRedirector.GetRedirectionArguments());
                Console.WriteLine(
                    "Finished submodule package resolution for " + submodule.Name);
            }

            Console.WriteLine("Package resolution complete.");
        }

        public void Resolve(ModuleInfo module, PackageRef reference, string platform, string templateName, bool? source, bool forceUpgrade = false)
        {
            if (!_featureManager.IsFeatureEnabled(Feature.PackageManagement))
            {
                return;
            }

            if (module != null && reference.Folder != null)
            {
                var existingPath = this.m_PackageLocator.DiscoverExistingPackagePath(module.Path, reference, platform);
                if (existingPath != null && Directory.Exists(existingPath))
                {
                    Console.WriteLine("Found an existing working copy of this package at " + existingPath);

                    Directory.CreateDirectory(reference.Folder);
                    using (var writer = new StreamWriter(Path.Combine(reference.Folder, ".redirect")))
                    {
                        writer.WriteLine(existingPath);
                    }

                    return;
                }
                else
                {
                    if (File.Exists(Path.Combine(reference.Folder, ".redirect")))
                    {
                        try
                        {
                            File.Delete(Path.Combine(reference.Folder, ".redirect"));
                        }
                        catch
                        {
                        }
                    }
                }
            }

            if (reference.Folder == null) 
            {
                Console.WriteLine("Resolving package with null reference folder; this package must be a global tool.");
            }

            string sourceUri, type;
            Dictionary<string, string> downloadMap, archiveTypeMap, resolvedHash;
            IPackageTransformer transformer;
            _packageLookup.Lookup(
                reference.Uri,
                platform,
                !forceUpgrade && reference.IsCommitReference,
                out sourceUri, 
                out type,
                out downloadMap,
                out archiveTypeMap,
                out resolvedHash,
                out transformer);

            // Resolve Git reference to Git commit hash.
            var refUri = reference.Uri;
            var refFolder = reference.Folder;
            var gitCommit = resolvedHash.ContainsKey(reference.GitRef) ? resolvedHash[reference.GitRef] : reference.GitRef;
            reference = new PackageRef
            {
                Uri = refUri,
                Folder = refFolder,
                GitRef = gitCommit
            };

            string toolFolder = null;
            if (type == PackageManager.PACKAGE_TYPE_TEMPLATE && templateName == null)
            {
                throw new InvalidOperationException(
                    "Template referenced as part of module packages.  Templates can only be used " +
                    "with the --start option.");
            }
            else if (type == PackageManager.PACKAGE_TYPE_LIBRARY)
            {
                Directory.CreateDirectory(reference.Folder);

                if (new DirectoryInfo(reference.Folder).GetFiles().Length > 0 || new DirectoryInfo(reference.Folder).GetDirectories().Length > 0)
                {
                    if (!File.Exists(Path.Combine(reference.Folder, ".git")) && !Directory.Exists(Path.Combine(reference.Folder, ".git")) &&
                        !File.Exists(Path.Combine(reference.Folder, ".pkg")))
                    {
                        Console.Error.WriteLine(
                            "WARNING: The package directory '" + reference.Folder + "' already exists and contains " +
                            "files and/or subdirectories, but neither a .pkg file nor a .git file or subdirectory exists.  " +
                            "This indicates the package directory contains data that is not been instantiated or managed " +
                            "by Protobuild.  Since there is no safe way to initialize the package in this directory " +
                            "without a potential loss of data, Protobuild will not modify the contents of this folder " +
                            "during package resolution.  If the folder does not contains the required package " +
                            "dependencies, the project generation or build may unexpectedly fail.");
                        return;
                    }
                }

                if (source == null)
                {
                    if (File.Exists(Path.Combine(reference.Folder, ".git")) || Directory.Exists(Path.Combine(reference.Folder, ".git")))
                    {
                        Console.WriteLine("Git repository present at " + Path.Combine(reference.Folder, ".git") + "; leaving as source version.");
                        source = true;
                    }
                    else
                    {
                        Console.WriteLine("Package type not specified (and no file at " + Path.Combine(reference.Folder, ".git") + "), requesting binary version.");
                        source = false;
                    }
                }
            }
            else if (type == PackageManager.PACKAGE_TYPE_GLOBAL_TOOL)
            {
                toolFolder = this.m_PackageGlobalTool.GetGlobalToolInstallationPath(reference);
                source = false;
            }

            if (source.Value && !string.IsNullOrWhiteSpace(sourceUri))
            {
                switch (type)
                {
                    case PackageManager.PACKAGE_TYPE_LIBRARY:
                        this.ResolveLibrarySource(reference, sourceUri, forceUpgrade);
                        break;
                    case PackageManager.PACKAGE_TYPE_TEMPLATE:
                        this.ResolveTemplateSource(reference, templateName, sourceUri);
                        break;
                }
            }
            else
            {
                switch (type)
                {
                    case PackageManager.PACKAGE_TYPE_LIBRARY:
                        this.ResolveLibraryBinary(reference, platform, sourceUri, forceUpgrade);
                        break;
                    case PackageManager.PACKAGE_TYPE_TEMPLATE:
                        this.ResolveTemplateBinary(reference, templateName, platform, sourceUri);
                        break;
                    case PackageManager.PACKAGE_TYPE_GLOBAL_TOOL:
                        this.ResolveGlobalToolBinary(reference, toolFolder, platform, forceUpgrade);
                        break;
                }
            }
        }

        private void ResolveLibrarySource(PackageRef reference, string source, bool forceUpgrade)
        {
            if (File.Exists(Path.Combine(reference.Folder, ".git")) || Directory.Exists(Path.Combine(reference.Folder, ".git")))
            {
                if (!forceUpgrade)
                {
                    Console.WriteLine("Git submodule / repository already present at " + reference.Folder);
                    return;
                }
            }

            this.EmptyReferenceFolder(reference.Folder);

            var package = m_PackageCache.GetSourcePackage(source, reference.GitRef);
            package.ExtractTo(reference.Folder);
        }

        private void ResolveTemplateSource(PackageRef reference, string templateName, string source)
        {
            if (reference.Folder != string.Empty)
            {
                throw new InvalidOperationException("Reference folder must be empty for template type.");
            }
            
            if (source.StartsWith("folder:"))
            {
                // The template is a raw folder on-disk.
                ApplyProjectTemplateFromStaging(source.Substring(7), templateName, NormalizeTemplateName(templateName));
            }
            else
            {
                if (Directory.Exists(".staging"))
                {
                    PathUtils.AggressiveDirectoryDelete(".staging");
                }

                var package = m_PackageCache.GetSourcePackage(source, reference.GitRef);
                package.ExtractTo(".staging");

                this.ApplyProjectTemplateFromStaging(".staging", templateName, NormalizeTemplateName(templateName));
            }
        }

        private string NormalizeTemplateName(string name)
        {
            var normalized = string.Empty;
            for (var i = 0; i < name.Length; i++)
            {
                if ((name[i] >= 'a' && name[i] <= 'z') ||
                    (name[i] >= 'A' && name[i] <= 'Z') ||
                    (i >= 1 && (name[i] >= '0' && name[i] <= '9')))
                {
                    normalized += name[i];
                }
            }
            if (string.IsNullOrWhiteSpace(normalized))
            {
                normalized = "Default";
            }
            return normalized;
        }

        private void ResolveLibraryBinary(PackageRef reference, string platform, string source, bool forceUpgrade)
        {
            if (File.Exists(Path.Combine(reference.Folder, platform, ".pkg")))
            {
                if (!forceUpgrade)
                {
                    Console.WriteLine("Protobuild binary package already present at " + Path.Combine(reference.Folder, platform));
                    return;
                }
            }

            var folder = Path.Combine(reference.Folder, platform);

            Console.WriteLine("Creating and emptying " + folder);

            if (File.Exists(Path.Combine(reference.Folder, ".pkg")))
            {
                if (Directory.Exists(folder))
                {
                    // Only clear out the target's folder if the reference folder
                    // already contains binary packages (for other platforms)
                    this.EmptyReferenceFolder(folder);
                }
            }
            else
            {
                // The reference folder is holding source code, so clear it
                // out entirely.
                this.EmptyReferenceFolder(reference.Folder);
            }

            Directory.CreateDirectory(folder);

            Console.WriteLine("Marking " + reference.Folder + " as ignored for Git");
            GitUtils.MarkIgnored(reference.Folder);

            var package = m_PackageCache.GetBinaryPackage(reference.Uri, reference.GitRef, platform);
            if (package == null)
            {
                this.ResolveLibrarySource(reference, source, forceUpgrade);
                return;
            }

            package.ExtractTo(folder);

            // Only copy ourselves to the binary folder if both "Build/Module.xml" and
            // "Build/Projects" exist in the binary package's folder.  This prevents us
            // from triggering the "create new module?" logic if the package hasn't been
            // setup correctly.
            if (Directory.Exists(Path.Combine(folder, "Build", "Projects")) && 
                File.Exists(Path.Combine(folder, "Build", "Module.xml")))
            {
                var sourceProtobuild = Assembly.GetEntryAssembly().Location;
                File.Copy(sourceProtobuild, Path.Combine(folder, "Protobuild.exe"), true);

                try
                {
                    var chmodStartInfo = new ProcessStartInfo
                    {
                        FileName = "chmod",
                        Arguments = "a+x Protobuild.exe",
                        WorkingDirectory = folder,
                        UseShellExecute = false
                    };
                    Process.Start(chmodStartInfo);
                }
                catch (ExecEnvironment.SelfInvokeExitException)
                {
                    throw;
                }
                catch
                {
                }
            }

            var file = File.Create(Path.Combine(folder, ".pkg"));
            file.Close();

            file = File.Create(Path.Combine(reference.Folder, ".pkg"));
            file.Close();

            Console.WriteLine("Binary resolution complete");
        }

        private void ResolveTemplateBinary(PackageRef reference, string templateName, string platform, string sourceUri)
        {
            if (reference.Folder != string.Empty)
            {
                throw new InvalidOperationException("Reference folder must be empty for template type.");
            }
            
            // The template is a reference to a Git repository.
            if (Directory.Exists(".staging"))
            {
                PathUtils.AggressiveDirectoryDelete(".staging");
            }

            Directory.CreateDirectory(".staging");

            var package = m_PackageCache.GetBinaryPackage(reference.Uri, reference.GitRef, platform);
            if (package == null)
            {
                this.ResolveTemplateSource(reference, templateName, sourceUri);
                return;
            }

            package.ExtractTo(".staging");

            ApplyProjectTemplateFromStaging(".staging", templateName, NormalizeTemplateName(templateName));
        }

        private void ResolveGlobalToolBinary(PackageRef reference, string toolFolder, string platform, bool forceUpgrade)
        {
            if (File.Exists(Path.Combine(toolFolder, ".pkg")))
            {
                if (!forceUpgrade)
                {
                    Console.WriteLine("Protobuild binary package already present at " + toolFolder);
                    return;
                }
            }

            Console.WriteLine("Creating and emptying " + toolFolder);
            this.EmptyReferenceFolder(toolFolder);
            Directory.CreateDirectory(toolFolder);

            Console.WriteLine("Installing " + reference.Uri + " at " + reference.GitRef);
            var package = m_PackageCache.GetBinaryPackage(reference.Uri, reference.GitRef, platform);
            if (package == null)
            {
                Console.WriteLine("The specified global tool package is not available for this platform.");
                return;
            }

            package.ExtractTo(toolFolder);

            var file = File.Create(Path.Combine(toolFolder, ".pkg"));
            file.Close();

            this.m_PackageGlobalTool.ScanPackageForToolsAndInstall(toolFolder);

            Console.WriteLine("Binary resolution complete");
        }

        private void ApplyProjectTemplateFromStaging(string folder, string name, string normalizedTemplateName)
        {
            foreach (var pathToFile in GetFilesFromStaging(folder))
            {
                var path = pathToFile.Key;
                var file = pathToFile.Value;

                var replacedPath = path.Replace("{PROJECT_NAME}", name);
                replacedPath = replacedPath.Replace("{PROJECT_SAFE_NAME}", normalizedTemplateName);
                var dirSeperator = replacedPath.LastIndexOfAny(new[] { '/', '\\' });
                if (dirSeperator != -1)
                {
                    var replacedDir = replacedPath.Substring(0, dirSeperator);
                    if (!Directory.Exists(replacedDir))
                    {
                        Directory.CreateDirectory(replacedDir);
                    }
                }

                string contents;
                using (var reader = new StreamReader(file.FullName))
                {
                    contents = reader.ReadToEnd();
                }

                if (contents.Contains("{PROJECT_NAME}") || contents.Contains("{PROJECT_XML_NAME}") || contents.Contains("{PROJECT_SAFE_NAME}") || contents.Contains("{PROJECT_SAFE_XML_NAME}"))
                {
                    contents = contents.Replace("{PROJECT_NAME}", name);
                    contents = contents.Replace("{PROJECT_XML_NAME}", System.Security.SecurityElement.Escape(name));
                    contents = contents.Replace("{PROJECT_SAFE_NAME}", normalizedTemplateName);
                    contents = contents.Replace("{PROJECT_SAFE_XML_NAME}", System.Security.SecurityElement.Escape(normalizedTemplateName));
                    using (var writer = new StreamWriter(replacedPath))
                    {
                        writer.Write(contents);
                    }
                }
                else
                {
                    // If we don't see {PROJECT_NAME} or {PROJECT_XML_NAME}, use a straight
                    // file copy so that we don't break binary files.
                    File.Copy(file.FullName, replacedPath, true);
                }
            }

            PathUtils.AggressiveDirectoryDelete(".staging");
        }

        private IEnumerable<KeyValuePair<string, FileInfo>> GetFilesFromStaging(string currentDirectory, string currentPrefix = null)
        {
            if (currentPrefix == null)
            {
                currentPrefix = string.Empty;
            }

            var dirInfo = new DirectoryInfo(currentDirectory);
            foreach (var subdir in dirInfo.GetDirectories("*"))
            {
                if (subdir.Name == ".git")
                {
                    continue;
                }

                var nextDirectory = Path.Combine(currentDirectory, subdir.Name);
                var nextPrefix = currentPrefix == string.Empty ? subdir.Name : Path.Combine(currentPrefix, subdir.Name);

                foreach (var kv in this.GetFilesFromStaging(nextDirectory, nextPrefix))
                {
                    yield return kv;
                }
            }

            foreach (var file in dirInfo.GetFiles("*"))
            {
                yield return new KeyValuePair<string, FileInfo>(Path.Combine(currentPrefix, file.Name), file);
            }
        }

        private void EmptyReferenceFolder(string folder)
        {
            PathUtils.AggressiveDirectoryDelete(folder);
        }
    }
}

