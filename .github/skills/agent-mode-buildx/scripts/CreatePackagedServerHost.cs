// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#:package Microsoft.NET.HostModel
#:property PublishAot=false

using System.Text.RegularExpressions;
using Microsoft.NET.HostModel.AppHost;

if (!OperatingSystem.IsWindows() || args.Length != 4)
{
    Console.Error.WriteLine("Usage: CreatePackagedServerHost.cs <packaged-server-directory> <apphost-template> <output-directory> <sdk-muxer>");
    return 1;
}

string source = Path.GetFullPath(args[0]);
string template = Path.GetFullPath(args[1]);
string destination = Path.GetFullPath(args[2]);
string muxer = Path.GetFullPath(args[3]);
if (!IsLongPathAware(muxer))
{
    throw new IOException($"The SDK muxer must declare Windows long-path awareness: {muxer}");
}
Directory.CreateDirectory(destination);
foreach (string file in Directory.EnumerateFiles(source))
{
    string copy = Path.Combine(destination, Path.GetFileName(file));
    File.Copy(file, copy, overwrite: true);
    if (!File.ReadAllBytes(file).AsSpan().SequenceEqual(File.ReadAllBytes(copy)))
    {
        throw new IOException($"Packaged server copy differs from its source: {file}");
    }
}

// Keep the packaged DLLs unchanged; an apphost selects FastBuild's Windows shell-detached launch.
string host = Path.Combine(destination, "fastbuild-server.exe");
HostWriter.CreateAppHost(template, host, "fastbuild-server.dll", assemblyToCopyResourcesFrom: muxer);
if (!IsLongPathAware(host))
{
    throw new IOException($"The generated server host lost the SDK's long-path manifest: {host}");
}
Console.WriteLine($"Created native launcher for the unchanged packaged server: {host}");
return 0;

static bool IsLongPathAware(string file)
    => Regex.IsMatch(Encoding.UTF8.GetString(File.ReadAllBytes(file)),
        @"<longPathAware\b[^>]*>\s*true\s*</longPathAware>", RegexOptions.CultureInvariant);
