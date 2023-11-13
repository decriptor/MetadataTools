﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace BinaryCompatChecker;

public class CommandLine
{
    public bool ReportEmbeddedInteropTypes { get; set; }
    public bool ReportIVT { get; set; }
    public bool ReportVersionMismatch { get; set; } = true;
    public bool ReportIntPtrConstructors { get; set; }
    public bool ReportUnreferencedAssemblies { get; set; } = false;

    public string ReportFile { get; set; } = "BinaryCompatReport.txt";
    public bool ListAssemblies { get; set; }

    public bool Recursive { get; set; }

    public static readonly StringComparison PathComparison = Checker.IsWindows ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
    public static readonly StringComparer PathComparer = Checker.IsWindows ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    public static CommandLine Parse(string[] args)
    {
        var result = new CommandLine();
        if (!result.Process(args))
        {
            return null;
        }

        return result;
    }

    public IEnumerable<string> Files => files;
    public IEnumerable<string> ClosureRootFiles => closureRootFiles;

    public bool IsClosureRoot(string filePath)
    {
        if (filePath.EndsWith(".config", PathComparison))
        {
            return false;
        }

        foreach (var root in closureRootPatterns)
        {
            if (filePath.Contains(root, PathComparison))
            {
                return true;
            }
        }

        return false;
    }

    HashSet<string> inclusions = new(PathComparer);
    HashSet<string> exclusions = new(PathComparer);
    HashSet<string> files = new(PathComparer);
    HashSet<string> patterns = new();
    HashSet<string> closureRootPatterns = new(PathComparer);
    List<string> closureRootFiles = new();
    IncludeExcludePattern includeExclude;

    public bool Process(string[] args)
    {
        // Parse parameterized args
        var arguments = new List<string>(args);

        var currentDirectory = Environment.CurrentDirectory;

        while (arguments.FirstOrDefault(a => a.StartsWith("@")) is string responseFile)
        {
            arguments.Remove(responseFile);
            responseFile = responseFile.Substring(1);
            if (File.Exists(responseFile))
            {
                var lines = File.ReadAllLines(responseFile);
                foreach (var line in lines)
                {
                    arguments.Add(line);
                }
            }
            else
            {
                Checker.WriteError("Response file doesn't exist: " + responseFile);
                return false;
            }
        }

        var helpArgument = arguments.FirstOrDefault(a => a == "/?" || a == "-?" || a == "-h" || a == "/h" || a == "-help" || a == "/help");
        if (helpArgument != null)
        {
            PrintUsage();
            return false;
        }

        foreach (var arg in arguments.ToArray())
        {
            if (arg.Equals("/ignoreVersionMismatch", StringComparison.OrdinalIgnoreCase) ||
                arg.Equals("-ignoreVersionMismatch", StringComparison.OrdinalIgnoreCase))
            {
                ReportVersionMismatch = false;
                arguments.Remove(arg);
                continue;
            }

            if (arg.Equals("/embeddedInteropTypes", StringComparison.OrdinalIgnoreCase) ||
                arg.Equals("-embeddedInteropTypes", StringComparison.OrdinalIgnoreCase))
            {
                ReportEmbeddedInteropTypes = true;
                arguments.Remove(arg);
                continue;
            }

            if (arg.Equals("/ivt", StringComparison.OrdinalIgnoreCase) ||
                arg.Equals("-ivt", StringComparison.OrdinalIgnoreCase))
            {
                ReportIVT = true;
                arguments.Remove(arg);
                continue;
            }

            if (arg.Equals("/s", StringComparison.OrdinalIgnoreCase) ||
                arg.Equals("-s", StringComparison.OrdinalIgnoreCase))
            {
                Recursive = true;
                arguments.Remove(arg);
                continue;
            }

            if (arg.Equals("/intPtrCtors", StringComparison.OrdinalIgnoreCase) ||
                arg.Equals("-intPtrCtors", StringComparison.OrdinalIgnoreCase))
            {
                ReportIntPtrConstructors = true;
                arguments.Remove(arg);
                continue;
            }

            if (arg.StartsWith("/out:") || arg.StartsWith("-out:"))
            {
                var report = arg.Substring(5);
                report = report.Trim('"');
                arguments.Remove(arg);
                ReportFile = report;
                continue;
            }

            if (arg.StartsWith("/l") || arg.StartsWith("-l"))
            {
                if (arg.Length == 2)
                {
                    ListAssemblies = true;
                }

                arguments.Remove(arg);
                continue;
            }

            if (arg.StartsWith("!") && arg.Length > 1)
            {
                string pattern = arg.Substring(1).Trim('"');
                exclusions.Add(pattern);
                arguments.Remove(arg);
                continue;
            }

            if (arg.StartsWith("/closure:") || arg.StartsWith("-closure:"))
            {
                string closure = arg.Substring("/closure:".Length);
                closure = closure.Trim('"');
                closureRootPatterns.Add(closure);
                ReportUnreferencedAssemblies = true;
                arguments.Remove(arg);
                continue;
            }

            if ((arg.StartsWith("/p:") || arg.StartsWith("-p:")) && arg.Length > 3)
            {
                string pattern = arg.Substring(3);
                pattern = pattern.Trim('"');

                if (pattern.Contains(';'))
                {
                    foreach (var sub in pattern.Split(';', StringSplitOptions.RemoveEmptyEntries))
                    {
                        patterns.Add(sub.Trim());
                    }
                }
                else
                {
                    patterns.Add(pattern);
                }

                arguments.Remove(arg);
                continue;
            }
        }

        if (patterns.Count == 0)
        {
            patterns.Add("*.dll");
            patterns.Add("*.exe");
            patterns.Add("*.dll.config");
            patterns.Add("*.exe.config");
        }

        if (exclusions.Count == 0)
        {
            exclusions.Add("*.resources.dll");
        }

        includeExclude = new IncludeExcludePattern("", IncludeExcludePattern.Combine(exclusions.Select(e => IncludeExcludePattern.PrepareRegexPattern(e))));

        foreach (var arg in arguments.ToArray())
        {
            if (!AddInclusion(arg, currentDirectory))
            {
                Checker.WriteError($"Expected directory, file glob or pattern: {arg}");
                return false;
            }
        }

        if (inclusions.Count == 0)
        {
            if (Recursive)
            {
                AddInclusion(Path.Combine(currentDirectory, "**") , currentDirectory);
            }
            else
            {
                AddInclusion(currentDirectory, currentDirectory);
            }
        }

        return true;
    }

    private bool AddInclusion(string text, string currentDirectory)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        text = text.Trim('"');

        text = text.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);

        inclusions.Add(text);
        Checker.WriteLine(text);

        bool windowsNetworkShare = false;
        bool startsWithDirectorySeparator = false;
        if (Checker.IsWindows)
        {
            if (text.StartsWith(@"\"))
            {
                startsWithDirectorySeparator = true;
                if (text.StartsWith(@"\\"))
                {
                    windowsNetworkShare = true;
                }
            }
        }
        else
        {
            if (text.StartsWith(Path.DirectorySeparatorChar))
            {
                startsWithDirectorySeparator = true;
            }
        }

        var parts = text.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);

        string root = null;

        if (windowsNetworkShare)
        {
            if (parts.Length < 2)
            {
                return false;
            }

            root = $@"\\{parts[0]}\{parts[1]}";
            parts = parts.Skip(2).ToArray();
        }
        else if (startsWithDirectorySeparator)
        {
            root = $"\\{parts[0]}";
            parts = parts.Skip(1).ToArray();
        }
        else if (parts[0] == "**")
        {
            root = currentDirectory;
        }
        else if (parts[0] == ".")
        {
            root = currentDirectory;
            parts = parts.Skip(1).ToArray();
        }
        else if (parts[0] == "..")
        {
            root = Path.GetDirectoryName(currentDirectory);
            parts = parts.Skip(1).ToArray();
        }
        else if (Checker.IsWindows && parts[0].Length == 2 && parts[0][1] == ':')
        {
            root = parts[0] + "\\";
            parts = parts.Skip(1).ToArray();
        }

        if (root == null)
        {
            root = currentDirectory;
        }

        if (root == null || !Directory.Exists(root))
        {
            return false;
        }

        return AddFiles(root, parts.ToArray());
    }

    private bool AddFiles(string root, string[] parts)
    {
        if (parts.Length == 0)
        {
            if (Recursive)
            {
                var dirs = Directory.GetDirectories(root);
                foreach (var dir in dirs)
                {
                    AddFiles(dir, parts);
                }
            }

            AddFilesInDirectory(root, patterns);
            return true;
        }

        if (parts[0] == "..")
        {
            root = Path.GetDirectoryName(root);
            if (root == null)
            {
                return false;
            }

            AddFilesInDirectory(root, parts.Skip(1).ToArray());
            return true;
        }

        var subdirectories = Directory.GetDirectories(root);

        string first = parts[0];
        if (first == "**")
        {
            if (!AddFiles(root, parts.Skip(1).ToArray()))
            {
                return false;
            }

            foreach (var subdirectory in subdirectories)
            {
                if (!AddFiles(subdirectory, parts))
                {
                    return false;
                }
            }

            return true;
        }

        if (subdirectories.FirstOrDefault(d => string.Equals(Path.GetFileName(d), first, PathComparison)) is string found)
        {
            return AddFiles(found, parts.Skip(1).ToArray());
        }

        if (parts.Length != 1)
        {
            return true;
        }

        string[] localPatterns = null;

        if (first.Contains(';'))
        {
            localPatterns = first.Split(';', StringSplitOptions.RemoveEmptyEntries);
        }
        else
        {
            localPatterns = new[] { first };
        }

        AddFilesInDirectory(root, localPatterns);

        return true;
    }

    private void AddFilesInDirectory(string root, IEnumerable<string> patterns)
    {
        foreach (var pattern in patterns)
        {
            var filesInDirectory = Directory.GetFiles(root, pattern);
            foreach (var file in filesInDirectory)
            {
                if (includeExclude != null && includeExclude.Excludes(file))
                {
                    continue;
                }

                AddFile(file);
            }
        }
    }

    private void AddFile(string file)
    {
        if (IsClosureRoot(file))
        {
            closureRootFiles.Add(file);
        }
        else
        {
            files.Add(file);
        }
    }

    public static void PrintUsage()
    {
        Checker.WriteLine("https://github.com/KirillOsenkov/MetadataTools/tree/main/src/BinaryCompatChecker", ConsoleColor.Blue);
        Checker.Write(@"Usage: ");
        Checker.Write(@"checkbinarycompat", ConsoleColor.Cyan);
        Checker.Write(@" <file-spec>* <option>* @<response-file>*

File spec:
    * absolute directory path
    * directory relative to current directory
    * may include ** to indicate recursive subtree
    * may optionally end with:
        - an optional file name (a.dll)
        - a pattern such as *.dll
        - semicolon-separated patterns such as *.dll;*.exe;*.exe.config
    When no file-specs are specified, uses the current directory
    non-recursively. Pass -s for recursion.
    When no patterns are specified, uses *.dll;*.exe;*.dll.config;*.exe.config.

Options:
    !<exclude-pattern>      exclude a relative path or file pattern from analysis
    -l                      output list of visited assemblies to BinaryCompatReport.Assemblies.txt
    -s                      recursive (when no file specs are specified)
    -closure:<file.dll>     path to one or more root assemblies of a closure (to report unused references)
    -p:<pattern>            semicolon-separated file pattern(s) such as *.dll;*.exe
    -out:<report.txt>       write report to <report.txt> instead of BinaryCompatReport.txt
    -ignoreVersionMismatch  do not report assembly version mismatches
    -ivt                    report internal API surface area used via InternalsVisibleTo
    -embeddedInteropTypes   report embedded interop types
    @response.rsp           Response file containing additional command-line arguments, one per line.
    -?:                     display help
");
    }
}