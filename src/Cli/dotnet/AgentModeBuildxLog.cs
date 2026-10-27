// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.CommandLine;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.DotNet.Cli.Commands;
using Microsoft.DotNet.Cli.Utils;

namespace Microsoft.DotNet.Cli;

internal static class AgentModeBuildxLog
{
    internal const string DirectoryVariable = "DOTNET_AGENT_LOG_DIR";

    internal sealed record Entry(int Version, DateTimeOffset Timestamp, int ProcessId, string Route, string Reason,
        int ExitCode, string SdkDirectory);

    internal static void Write(ParseResult parseResult, string reason, int exitCode)
    {
        string route = reason == "supported" ? "buildx" : reason == "dispatch-error" ? "error" : "sdk";
        if (route == "sdk" && (CommandLoggingContext.IsVerbose ||
            (parseResult.Errors.Count == 0 &&
                parseResult.GetValue(Parser.RootCommand.BuildCommand.VerbosityOption) is
                    VerbosityOptions.detailed or VerbosityOptions.d or VerbosityOptions.diagnostic or VerbosityOptions.diag)))
        {
            Reporter.Error.WriteLine(string.Format(CliCommandStrings.AgentModeBuildxStock, reason));
        }
        string? directory = Environment.GetEnvironmentVariable(DirectoryVariable);
        if (string.IsNullOrEmpty(directory)) { return; }
        if (!Path.IsPathFullyQualified(directory))
        {
            throw new GracefulException(string.Format(CliCommandStrings.AgentModeBuildxInvalidLogPath, DirectoryVariable));
        }
        Directory.CreateDirectory(directory);
        Entry entry = new(1, DateTimeOffset.UtcNow, Environment.ProcessId, route, reason, exitCode, SdkPaths.SdkDirectory);
        // One file per invocation avoids append races across concurrent SDK processes. No raw arguments are stored.
        File.WriteAllText(Path.Combine(directory, $"{Environment.ProcessId}-{Guid.NewGuid():N}.json"),
            JsonSerializer.Serialize(entry, AgentModeBuildxLogJsonContext.Default.Entry));
    }
}

[JsonSerializable(typeof(AgentModeBuildxLog.Entry))]
internal partial class AgentModeBuildxLogJsonContext : JsonSerializerContext;
