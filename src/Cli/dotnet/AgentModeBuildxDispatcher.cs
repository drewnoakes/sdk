// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.CommandLine;
using System.CommandLine.Parsing;
using System.Diagnostics;
using Microsoft.DotNet.Cli.Extensions;
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

    internal static void ReportFallback(ParseResult parseResult, string reason)
    {
        if (reason is "not-build" or "non-execution" or "recursive-bypass")
        {
            return;
        }
        bool detailed = CommandLoggingContext.IsVerbose || (parseResult.Errors.Count == 0 &&
            parseResult.GetValue(Parser.RootCommand.BuildCommand.VerbosityOption) is
                VerbosityOptions.detailed or VerbosityOptions.d or VerbosityOptions.diagnostic or VerbosityOptions.diag);
        if (detailed || reason is not ("disabled" or "unprovisioned" or "no-agent"))
        {
            // Keep query stdout machine-readable and avoid repeating notices in nested SDK builds.
            Reporter.Error.WriteLine(reason == "msbuild-property-override"
                ? CliCommandStrings.AgentModeBuildxPropertyOverride
                : string.Format(CliCommandStrings.AgentModeBuildxStock, reason));
        }
    }

    internal static bool TryExecute(ParseResult parseResult, string[]? arguments, out int exitCode, out string reason)
    {
        exitCode = 0;
        reason = "not-build";
        if (arguments is not ["build", ..])
        {
            return false;
        }
        reason = "dispatch-error";
        if (!IsEnabled(Environment.GetEnvironmentVariable, new LLMEnvironmentDetectorForTelemetry()))
        {
            reason = Environment.GetEnvironmentVariable(BypassVariable) == "1" ? "recursive-bypass"
                : Environment.GetEnvironmentVariable(ModeVariable)?.ToLowerInvariant() is "off" or "false" or "0" ? "disabled"
                : string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(BuildxPathVariable)) ? "unprovisioned" : "no-agent";
            return false;
        }
        if (!TryGetBuildxArguments(parseResult, arguments, Environment.CurrentDirectory, out string[] buildxArguments, out reason))
        {
            return false;
        }
        // The pinned package does not include environment identity in resident-server reuse.
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("Configuration")) ||
            !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("Platform")))
        {
            reason = "unsupported-environment";
            return false;
        }

        reason = "dispatch-error";
        ProcessStartInfo startInfo = CreateStartInfo(
            Environment.GetEnvironmentVariable(BuildxPathVariable),
            buildxArguments,
            new Muxer().MuxerPath,
            SdkPaths.SdkDirectory);

        Reporter.Output.WriteLine(CliCommandStrings.AgentModeBuildxActivated);
        if (CommandLoggingContext.IsVerbose ||
            parseResult.GetValue(Parser.RootCommand.BuildCommand.VerbosityOption) is
                VerbosityOptions.detailed or VerbosityOptions.d or VerbosityOptions.diagnostic or VerbosityOptions.diag)
        {
            Reporter.Error.WriteLine(string.Format(CliCommandStrings.AgentModeBuildxCommand,
                ArgumentEscaper.EscapeAndConcatenateArgArrayForProcessStart([startInfo.FileName, .. startInfo.ArgumentList])));
        }
        reason = "supported";
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
        => TryGetBuildxArguments(parseResult, arguments, workingDirectory, out buildxArguments, out _);

    internal static bool TryGetBuildxArguments(
        ParseResult parseResult, string[]? arguments, string workingDirectory, out string[] buildxArguments, out string reason)
    {
        buildxArguments = [];
        reason = parseResult.Errors.Count > 0 ? "parse-error" : "non-execution";
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
            if (argument.StartsWith('@'))
            {
                reason = "response-file";
                return false;
            }
            if (!argument.StartsWith('-') && !argument.StartsWith('/'))
            {
                if (target is not null)
                {
                    reason = "multiple-targets";
                    return false;
                }
                target = argument;
                continue;
            }
            // Unix absolute paths are operands, not MSBuild slash switches.
            if (Path.IsPathFullyQualified(argument) && (File.Exists(Path.GetFullPath(argument, workingDirectory)) ||
                Directory.Exists(Path.GetFullPath(argument, workingDirectory))))
            {
                if (target is not null) { reason = "multiple-targets"; return false; }
                target = argument;
                continue;
            }
            int separator = argument.IndexOfAny([':', '=']);
            string spelling = separator < 0 ? argument : argument[..separator];
            string name = spelling.TrimStart('-', '/').ToLowerInvariant();
            string? value = separator < 0 ? null : argument[(separator + 1)..];
            reason = "unsupported-option:" + name;
            bool cliOption = spelling is "-c" or "--configuration" or "-f" or "--framework"
                or "-o" or "--output" or "--artifacts-path" or "--no-restore" or "--no-incremental" or "--no-logo";
            bool msbuildOption = spelling.ToLowerInvariant() is "-p" or "--p" or "/p" or "-property" or "--property"
                or "/property" or "-v" or "--v" or "/v" or "-verbosity" or "--verbosity" or "/verbosity"
                or "-nologo" or "--nologo" or "/nologo";
            if (!cliOption && !msbuildOption) { return false; }
            switch (name)
            {
                case "p" or "property":
                    reason = "msbuild-property-override";
                    return false;
                case "o" or "output" or "artifacts-path":
                    reason = "output-path-override";
                    return false;
                case "c" or "configuration" or "f" or "framework" or "v" or "verbosity":
                    if (value is null && ++i < arguments.Length)
                    {
                        value = arguments[i];
                    }
                    if (string.IsNullOrEmpty(value))
                    {
                        reason = "missing-option-value:" + name;
                        return false;
                    }
                    if (name is "v" or "verbosity")
                    {
                        if (!Enum.TryParse(value, ignoreCase: true, out VerbosityOptions verbosity) || !Enum.IsDefined(verbosity))
                        {
                            reason = "invalid-verbosity";
                            return false;
                        }
                        options.Add("--verbosity");
                        options.Add(verbosity.ToString());
                        break;
                    }
                    if (value.IndexOfAny([';', ',', '"']) >= 0)
                    {
                        reason = "unsupported-option-value:" + name;
                        return false;
                    }
                    string propertyName = name switch
                    {
                        "c" or "configuration" => "Configuration",
                        _ => "TargetFramework"
                    };
                    options.Add($"-p:{propertyName}={value}");
                    break;
                case "no-restore" or "no-incremental":
                    if (value is not null) { return false; }
                    options.Add("--" + name);
                    break;
                case "nologo" or "no-logo":
                    if (value is not null) { return false; }
                    // This package has no product logo; its spawned SDK also defaults to nologo.
                    break;
                default:
                    return false;
            }
        }

        target = Path.GetFullPath(target ?? workingDirectory, workingDirectory);
        if (Directory.Exists(target) &&
            !SolutionAndProjectUtility.TryGetProjectOrSolutionFilePath(target, out target, out _).SolutionOrProjectFileFound)
        {
            reason = "ambiguous-or-missing-target";
            return false;
        }

        if (!File.Exists(target) ||
            !(Path.GetExtension(target).ToLowerInvariant() is ".csproj" or ".vbproj" or ".sln" or ".slnx" or ".slnf"))
        {
            reason = "unsupported-or-missing-target";
            return false;
        }

        buildxArguments = [target, "--server", .. options];
        reason = "supported";
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
        startInfo.Environment["DOTNET_NOLOGO"] = "1";
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
