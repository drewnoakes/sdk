// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.CommandLine;
using System.CommandLine.Parsing;
using System.Diagnostics;
using Microsoft.DotNet.Cli.Commands;
using Microsoft.DotNet.Cli.Commands.Test;
using Microsoft.DotNet.Cli.Telemetry;
using Microsoft.DotNet.Cli.Utils;

namespace Microsoft.DotNet.Cli;

internal static class AgentModeBuildxDispatcher
{
    internal const string ModeVariable = "DOTNET_AGENT_MODE";
    internal const string BuildxPathVariable = "DOTNET_AGENT_BUILDX_PATH";
    internal const string BypassVariable = "DOTNET_AGENT_DISPATCH_BYPASS";

    internal static bool TryExecute(ParseResult parseResult, string[]? arguments, out int exitCode)
    {
        exitCode = 0;
        if (!IsBuildInvocation(parseResult, arguments) ||
            !IsEnabled(Environment.GetEnvironmentVariable, new LLMEnvironmentDetectorForTelemetry()) ||
            !TryGetBuildxArguments(parseResult, arguments, Environment.CurrentDirectory, out string[] buildxArguments))
        {
            return false;
        }

        ProcessStartInfo startInfo = CreateStartInfo(
            Environment.GetEnvironmentVariable(BuildxPathVariable),
            buildxArguments,
            new Muxer().MuxerPath,
            SdkPaths.SdkDirectory);

        Reporter.Output.WriteLine(CliCommandStrings.AgentModeBuildxActivated);
        using Process process = new() { StartInfo = startInfo };
        exitCode = new Utils.Command(process).Execute().ExitCode;
        return true;
    }

    internal static bool IsEnabled(Func<string, string?> getEnvironmentVariable, ILLMEnvironmentDetector detector)
    {
        if (getEnvironmentVariable(BypassVariable) == "1")
        {
            return false;
        }

        string? mode = getEnvironmentVariable(ModeVariable);
        if (string.IsNullOrEmpty(mode) || mode.Equals("auto", StringComparison.OrdinalIgnoreCase))
        {
            return !string.IsNullOrWhiteSpace(getEnvironmentVariable(BuildxPathVariable)) && detector.IsLLMEnvironment();
        }

        if (mode.Equals("off", StringComparison.OrdinalIgnoreCase) ||
            mode.Equals("false", StringComparison.OrdinalIgnoreCase) || mode == "0")
        {
            return false;
        }

        if (mode.Equals("on", StringComparison.OrdinalIgnoreCase) ||
            mode.Equals("true", StringComparison.OrdinalIgnoreCase) || mode == "1")
        {
            return true;
        }

        throw new GracefulException(string.Format(CliCommandStrings.AgentModeInvalidMode, mode, ModeVariable));
    }

    private static bool IsBuildInvocation(ParseResult parseResult, string[]? arguments)
        => arguments is { Length: > 0 } && arguments[0] == "build"
            && parseResult.CommandResult.Command == Parser.RootCommand.BuildCommand
            && parseResult.Errors.Count == 0
            && parseResult.Action is not InvocableOptionAction { Terminating: true }
            && !parseResult.Tokens.Any(token => token.Type == TokenType.Directive);

    internal static bool TryGetBuildxArguments(
        ParseResult parseResult, string[]? arguments, string workingDirectory, out string[] buildxArguments)
    {
        buildxArguments = [];
        if (!IsBuildInvocation(parseResult, arguments))
        {
            return false;
        }

        // Microsoft.FastBuild is not an argv-transparent dotnet build replacement. Unknown forms
        // must remain with the SDK before launching anything, rather than losing a switch or retrying.
        List<string> options = [];
        string? target = null;
        for (int i = 1; i < arguments!.Length; i++)
        {
            string argument = arguments[i];
            switch (argument)
            {
                case "-c" or "--configuration" or "-f" or "--framework" or "-v" or "--verbosity":
                    if (++i == arguments.Length)
                    {
                        return false;
                    }
                    options.Add(argument switch
                    {
                        "-c" or "--configuration" => $"-p:Configuration={arguments[i]}",
                        "-f" or "--framework" => $"-p:TargetFramework={arguments[i]}",
                        _ => "--verbosity"
                    });
                    if (argument is "-v" or "--verbosity")
                    {
                        options.Add(arguments[i]);
                    }
                    break;
                case "--no-restore":
                    options.Add(argument);
                    break;
                default:
                    string? property = new[] { "-p:", "--property:", "-property:", "/p:", "/property:" }
                        .FirstOrDefault(prefix => argument.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
                    if (property is not null && argument.IndexOf('=', property.Length) > property.Length)
                    {
                        options.Add("-p:" + argument[property.Length..]);
                    }
                    else if (target is null && !argument.StartsWith('-') && !argument.StartsWith('@'))
                    {
                        target = argument;
                    }
                    else
                    {
                        return false;
                    }
                    break;
            }
        }

        target = Path.GetFullPath(target ?? workingDirectory, workingDirectory);
        if (Directory.Exists(target) &&
            !SolutionAndProjectUtility.TryGetProjectOrSolutionFilePath(target, out target, out _).SolutionOrProjectFileFound)
        {
            return false;
        }

        if (!File.Exists(target) ||
            !(Path.GetExtension(target).ToLowerInvariant() is ".csproj" or ".vbproj" or ".sln" or ".slnx" or ".slnf"))
        {
            return false;
        }

        buildxArguments = [target, "--server", .. options];
        return true;
    }

    internal static ProcessStartInfo CreateStartInfo(
        string? buildxPath, string[] arguments, string dotnetHostPath, string sdkDirectory)
    {
        if (string.IsNullOrWhiteSpace(buildxPath) || !Path.IsPathFullyQualified(buildxPath) ||
            !File.Exists(buildxPath) ||
            Path.GetFullPath(buildxPath).Equals(Path.GetFullPath(dotnetHostPath),
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal) ||
            Path.GetFullPath(buildxPath).Equals(Path.Combine(sdkDirectory, "dotnet.dll"),
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
        {
            throw new GracefulException(string.Format(CliCommandStrings.AgentModeBuildxInvalidPath, BuildxPathVariable));
        }

        bool managed = Path.GetExtension(buildxPath).Equals(".dll", StringComparison.OrdinalIgnoreCase);
        ProcessStartInfo startInfo = new(managed ? dotnetHostPath : buildxPath)
        {
            UseShellExecute = false,
            WorkingDirectory = Environment.CurrentDirectory,
        };
        if (managed)
        {
            startInfo.ArgumentList.Add(buildxPath);
        }
        foreach (string argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        string dotnetRoot = Path.GetDirectoryName(dotnetHostPath)!;
        startInfo.Environment[BypassVariable] = "1";
        startInfo.Environment["FASTBUILD_SDK_PATH"] = sdkDirectory;
        startInfo.Environment["DOTNET_HOST_PATH"] = dotnetHostPath;
        startInfo.Environment["PATH"] = dotnetRoot + Path.PathSeparator + startInfo.Environment["PATH"];
        foreach (string name in startInfo.Environment.Keys.Where(name =>
            name.StartsWith("DOTNET_ROOT", StringComparison.OrdinalIgnoreCase)).ToArray())
        {
            startInfo.Environment.Remove(name);
        }
        startInfo.Environment["DOTNET_ROOT"] = dotnetRoot;
        startInfo.Environment["DOTNET_ROLL_FORWARD_TO_PRERELEASE"] = "1";
        return startInfo;
    }
}
