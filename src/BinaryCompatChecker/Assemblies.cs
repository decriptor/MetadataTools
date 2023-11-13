﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using Mono.Cecil;

namespace BinaryCompatChecker;

public partial class Checker
{
    IAssemblyResolver resolver;

    private static Dictionary<string, bool> frameworkAssemblyNames = new(StringComparer.OrdinalIgnoreCase);

    private static HashSet<string> frameworkNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "mscorlib",
        "Accessibility",
        "Microsoft.CSharp",
        "Microsoft.VisualBasic",
        "Microsoft.VisualC",
        "netstandard",
        "PresentationCore",
        "PresentationFramework",
        "ReachFramework",
        "System",
        "UIAutomationClient",
        "UIAutomationProvider",
        "UIAutomationTypes",
        "WindowsBase",
        "WindowsFormsIntegration"
    };

    private List<string> customResolveDirectories = new List<string>
    {
        @"C:\Program Files\Microsoft Visual Studio\2022\Enterprise\MSBuild\Current\Bin",
        @"C:\Program Files\Microsoft Visual Studio\2022\Enterprise\Common7\IDE\CommonExtensions\Microsoft\NuGet",
        @"C:\Program Files\Microsoft Visual Studio\2022\Preview\MSBuild\Current\Bin",
        @"C:\Program Files\Microsoft Visual Studio\2022\Preview\Common7\IDE\CommonExtensions\Microsoft\NuGet",
    };

    private string dotnetRuntimeDirectory = Path.GetDirectoryName(typeof(object).Assembly.Location);

    private static string desktopNetFrameworkDirectory =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Microsoft.NET");

    private List<string> desktopNetFrameworkDirectories = new List<string>
    {
        Path.Combine(desktopNetFrameworkDirectory, "assembly", "GAC_MSIL"),
        Path.Combine(desktopNetFrameworkDirectory, "assembly", "GAC_32"),
        Path.Combine(desktopNetFrameworkDirectory, "assembly", "GAC_64"),
    };

    private static string mscorlibFilePath = Path.Combine(desktopNetFrameworkDirectory, "Framework", "v4.0.30319", "mscorlib.dll");

    private class VersionMismatch
    {
        public AssemblyDefinition Referencer;
        public AssemblyNameReference ExpectedReference;
        public AssemblyDefinition ActualAssembly;
    }

    public class IVTUsage
    {
        public string ExposingAssembly { get; set; }
        public string ConsumingAssembly { get; set; }
        public string Member { get; set; }
    }

    private readonly List<VersionMismatch> versionMismatches = new List<VersionMismatch>();

    public class CustomAssemblyResolver : BaseAssemblyResolver
    {
        private readonly Checker checker;

        public CustomAssemblyResolver(Checker checker)
        {
            this.checker = checker;
        }

        public override AssemblyDefinition Resolve(AssemblyNameReference name, ReaderParameters parameters)
        {
            RuntimeHelpers.EnsureSufficientExecutionStack(); // see https://github.com/KirillOsenkov/MetadataTools/issues/4
            var resolved = checker.Resolve(name);
            resolved = resolved ?? base.Resolve(name, parameters);
            return resolved;
        }
    }

    private static bool IsFrameworkName(string shortName)
    {
        return
            shortName.StartsWith("System.", StringComparison.OrdinalIgnoreCase) ||
            frameworkNames.Contains(shortName);
    }

    private HashSet<string> GetFrameworkAssemblyNames()
    {
        var corlibPath = typeof(object).Assembly.Location;
        var frameworkDirectory = Path.GetDirectoryName(corlibPath);
        var files = Directory.GetFiles(frameworkDirectory, "System*.dll").Select(Path.GetFileNameWithoutExtension);
        var result = new HashSet<string>(files, StringComparer.OrdinalIgnoreCase);
        result.UnionWith(frameworkNames);
        return result;
    }

    private static bool IsRoslynAssembly(string assemblyName)
    {
        if (assemblyName.Contains("Microsoft.CodeAnalysis") || assemblyName.Contains("VisualStudio.LanguageServices"))
        {
            return true;
        }

        return false;
    }

    private static bool IsNetFrameworkAssembly(string assemblyName)
    {
        frameworkAssemblyNames.TryGetValue(assemblyName, out bool result);
        return result;
    }

    /// <summary>
    /// Returns true if the <paramref name="assembly"/> is .NET Framework assembly.
    /// </summary>
    private static bool IsNetFrameworkAssembly(AssemblyDefinition assembly)
    {
        string key = assembly.MainModule.FileName;
        if (frameworkAssemblyNames.TryGetValue(key, out bool result))
        {
            return result;
        }

        // Hacky way of detecting it.
        result = assembly
            .CustomAttributes
            .FirstOrDefault(a => IsAssemblyProductFramework(a) || IsAssemblyMetadataFramework(a)) != null;
        frameworkAssemblyNames[key] = result;
        return result;
    }

    private static bool IsAssemblyMetadataFramework(CustomAttribute a)
    {
        return
            a.AttributeType.Name == "AssemblyMetadataAttribute" &&
            a.ConstructorArguments != null &&
            a.ConstructorArguments.Count > 0 &&
            a.ConstructorArguments[0].Value.ToString() == ".NETFrameworkAssembly";
    }

    private static bool IsAssemblyProductFramework(CustomAttribute a)
    {
        return
            a.AttributeType.Name == "AssemblyProductAttribute" &&
            a.ConstructorArguments != null &&
            a.ConstructorArguments.FirstOrDefault(c =>
                c.Value.ToString() == "Microsoft® .NET Framework" ||
                c.Value.ToString() == "Microsoft® .NET").Value != null;
    }

    /// <summary>
    /// Returns true if the <paramref name="assembly"/> is a facade assembly with type forwarders only.
    /// </summary>
    private static bool IsFacadeAssembly(AssemblyDefinition assembly)
    {
        var types = assembly.MainModule.Types;
        if (types.Count == 1 && types[0].FullName == "<Module>" && assembly.MainModule.HasExportedTypes)
        {
            return true;
        }

        return false;
    }

    private void OnAssemblyResolved(AssemblyDefinition assemblyDefinition)
    {
        //string filePath = assemblyDefinition.MainModule.FileName;
        //WriteLine(filePath, ConsoleColor.DarkGray);
    }

    private void OnAssemblyLoaded(AssemblyDefinition assemblyDefinition)
    {
        string filePath = assemblyDefinition.MainModule.FileName;
        WriteLine(filePath, ConsoleColor.DarkGray);
    }

    private AssemblyDefinition Resolve(AssemblyNameReference reference)
    {
        if (resolveCache.TryGetValue(reference.FullName, out AssemblyDefinition result))
        {
            return result;
        }

        foreach (var assemblyDefinition in filePathToModuleDefinition)
        {
            if (assemblyDefinition.Value == null)
            {
                continue;
            }

            if (assemblyDefinition.Value.Name.FullName == reference.FullName ||
                string.Equals(Path.GetFileNameWithoutExtension(assemblyDefinition.Key), reference.Name, StringComparison.OrdinalIgnoreCase))
            {
                result = assemblyDefinition.Value;
                resolveCache[reference.FullName] = result;
                OnAssemblyResolved(result);
                return result;
            }
        }

        foreach (var file in commandLine.Files)
        {
            if (string.Equals(Path.GetFileNameWithoutExtension(file), reference.Name, StringComparison.OrdinalIgnoreCase))
            {
                result = Load(file);
                if (result != null && !IsFacadeAssembly(result))
                {
                    resolveCache[reference.FullName] = result;
                    OnAssemblyResolved(result);
                }

                return result;
            }
        }

        string shortName = reference.Name;

        string frameworkCandidate = Path.Combine(dotnetRuntimeDirectory, shortName + ".dll");
        if (IsWindows)
        {
            if (shortName == "mscorlib" && File.Exists(mscorlibFilePath))
            {
                frameworkCandidate = mscorlibFilePath;
            }
            else
            {
                foreach (var dir in desktopNetFrameworkDirectories)
                {
                    var combined = Path.Combine(dir, shortName);
                    if (Directory.Exists(combined))
                    {
                        var first = Directory.GetDirectories(combined);
                        if (first.Length == 1)
                        {
                            var candidate = Path.Combine(first[0], shortName + ".dll");
                            if (File.Exists(candidate))
                            {
                                frameworkCandidate = candidate;
                                break;
                            }
                        }
                    }
                }
            }
        }

        bool isFrameworkName = IsFrameworkName(shortName);

        if (isFrameworkName && File.Exists(frameworkCandidate))
        {
            result = Load(frameworkCandidate);
            if (result != null)
            {
                OnAssemblyResolved(result);
                resolveCache[reference.FullName] = result;
                return result;
            }
        }

        foreach (var customResolveDirectory in customResolveDirectories)
        {
            var candidate = Path.Combine(customResolveDirectory, shortName + ".dll");
            if (File.Exists(candidate))
            {
                result = Load(candidate);
                if (result != null)
                {
                    resolveCache[reference.FullName] = result;
                    OnAssemblyResolved(result);
                    return result;
                }
            }
        }

        return null;
    }

    private AssemblyDefinition Load(string filePath)
    {
        if (!filePathToModuleDefinition.TryGetValue(filePath, out var assemblyDefinition))
        {
            try
            {
                if (!PEFile.IsManagedAssembly(filePath))
                {
                    filePathToModuleDefinition[filePath] = null;
                    return null;
                }

                var readerParameters = new ReaderParameters
                {
                    AssemblyResolver = this.resolver,
                    InMemory = true
                };
                assemblyDefinition = AssemblyDefinition.ReadAssembly(filePath, readerParameters);
                filePathToModuleDefinition[filePath] = assemblyDefinition;

                OnAssemblyLoaded(assemblyDefinition);

                if (!IsNetFrameworkAssembly(assemblyDefinition))
                {
                    string relativePath = GetRelativePath(filePath);
                    string targetFramework = GetTargetFramework(assemblyDefinition);
                    if (targetFramework != null)
                    {
                        targetFramework = " " + targetFramework;
                    }

                    assembliesExamined.Add($"{relativePath}    {assemblyDefinition.Name.Version}{targetFramework}");
                }
            }
            catch (Exception ex)
            {
                diagnostics.Add(ex.ToString());
                return null;
            }
        }

        return assemblyDefinition;
    }

    private string GetRelativePath(string filePath)
    {
        if (filePath.StartsWith(Environment.CurrentDirectory, StringComparison.OrdinalIgnoreCase))
        {
            filePath = filePath.Substring(Environment.CurrentDirectory.Length + 1);
        }

        return filePath;
    }
}