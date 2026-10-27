// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.DotNet.Cli.Utils;
using Moq;

namespace Microsoft.DotNet.Cli;

[TestClass]
public class AgentModeBuildxTests : SdkTest
{
    [TestMethod]
    [DataRow(null, true, true, true)]
    [DataRow("auto", true, true, true)]
    [DataRow(null, false, true, false)]
    [DataRow("auto", true, false, false)]
    [DataRow("off", true, true, false)]
    [DataRow("FALSE", true, true, false)]
    [DataRow("0", true, true, false)]
    [DataRow("on", false, false, true)]
    [DataRow("TRUE", false, true, true)]
    [DataRow("1", false, true, true)]
    public void EnablementUsesTheSharedDetector(string? mode, bool detected, bool provisioned, bool expected)
    {
        string? GetEnvironmentVariable(string name) => name switch
        {
            AgentModeBuildxDispatcher.ModeVariable => mode,
            AgentModeBuildxDispatcher.BuildxPathVariable => provisioned ? "provisioned" : null,
            _ => null
        };
        ILLMEnvironmentDetector detector = Mock.Of<ILLMEnvironmentDetector>(d => d.IsLLMEnvironment() == detected);

        AgentModeBuildxDispatcher.IsEnabled(GetEnvironmentVariable, detector).Should().Be(expected);
    }

    [TestMethod]
    public void BypassPrecedesModeValidation()
    {
        AgentModeBuildxDispatcher.IsEnabled(
            name => name == AgentModeBuildxDispatcher.BypassVariable ? "1" : "invalid",
            Mock.Of<ILLMEnvironmentDetector>()).Should().BeFalse();

        Action invalidMode = () => AgentModeBuildxDispatcher.IsEnabled(
            _ => "invalid", Mock.Of<ILLMEnvironmentDetector>());
        invalidMode.Should().Throw<GracefulException>();
    }

    [TestMethod]
    public void TranslatesSupportedArgumentsWithoutSplittingValues()
    {
        string directory = TestAssetsManager.CreateTestDirectory().Path;
        string project = Path.Combine(directory, "My App.csproj");
        File.WriteAllText(project, "<Project />");
        string[] arguments = [
            "build", "My App.csproj", "-c", "Release", "-f", ToolsetInfo.CurrentTargetFramework,
            "--no-restore", "-v", "quiet"
        ];

        AgentModeBuildxDispatcher.TryGetBuildxArguments(
            Parser.Parse(arguments), arguments, directory, out string[] translated).Should().BeTrue();
        translated.Should().Equal(
            project, "--server", "-p:Configuration=Release", $"-p:TargetFramework={ToolsetInfo.CurrentTargetFramework}",
            "--no-restore", "--verbosity", "quiet");
    }

    [TestMethod]
    [DataRow("--configuration=Release", "-p:Configuration=Release")]
    [DataRow("-c:Release", "-p:Configuration=Release")]
    [DataRow("--framework=net11.0", "-p:TargetFramework=net11.0")]
    [DataRow("-f:net11.0", "-p:TargetFramework=net11.0")]
    [DataRow("--no-incremental", "--no-incremental")]
    public void NormalizesCompatibleOptions(string argument, string expected)
    {
        string directory = TestAssetsManager.CreateTestDirectory(identifier: argument.Replace(':', '_')).Path;
        string project = Path.Combine(directory, "App.csproj");
        File.WriteAllText(project, "<Project />");
        string[] arguments = ["build", argument];
        AgentModeBuildxDispatcher.TryGetBuildxArguments(Parser.Parse(arguments), arguments, directory,
            out string[] translated, out string reason).Should().BeTrue();
        translated.Should().Equal(project, "--server", expected);
        reason.Should().Be("supported");
    }

    [TestMethod]
    [DataRow("-v:minimal")]
    [DataRow("/v:minimal")]
    [DataRow("--verbosity=minimal")]
    [DataRow("-verbosity:minimal")]
    public void NormalizesVerbosity(string argument)
    {
        string directory = TestAssetsManager.CreateTestDirectory(identifier: argument.Replace(':', '_')).Path;
        string project = Path.Combine(directory, "App.csproj");
        File.WriteAllText(project, "<Project />");
        string[] arguments = ["build", argument];
        AgentModeBuildxDispatcher.TryGetBuildxArguments(Parser.Parse(arguments), arguments, directory,
            out string[] translated).Should().BeTrue();
        translated.Should().Equal(project, "--server", "--verbosity", "minimal");
    }

    [TestMethod]
    [DataRow("--nologo")]
    [DataRow("--no-logo")]
    [DataRow("-nologo")]
    [DataRow("/nologo")]
    public void AcceptsLogoSuppressionWithoutSendingAnUnknownSwitch(string argument)
    {
        string directory = TestAssetsManager.CreateTestDirectory(identifier: argument).Path;
        string project = Path.Combine(directory, "App.csproj");
        File.WriteAllText(project, "<Project />");
        string[] arguments = ["build", argument];
        AgentModeBuildxDispatcher.TryGetBuildxArguments(Parser.Parse(arguments), arguments, directory,
            out string[] translated).Should().BeTrue();
        translated.Should().Equal(project, "--server");
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public void ExplicitPropertiesRemainStockRegardlessOfPosition(bool propertyFirst)
    {
        string directory = TestAssetsManager.CreateTestDirectory(identifier: propertyFirst.ToString()).Path;
        string project = Path.Combine(directory, "App.csproj");
        File.WriteAllText(project, "<Project />");
        string[] arguments = propertyFirst
            ? ["build", "-p:Configuration=Debug", "-c", "Release"]
            : ["build", "-c", "Release", "-p:Configuration=Debug"];
        AgentModeBuildxDispatcher.TryGetBuildxArguments(Parser.Parse(arguments), arguments, directory,
            out string[] translated, out string reason).Should().BeFalse();
        translated.Should().BeEmpty();
        reason.Should().Be("msbuild-property-override");
    }

    [TestMethod]
    [DataRow("-o")]
    [DataRow("--output")]
    [DataRow("--artifacts-path")]
    public void OutputOverridesStayWithTheSdk(string option)
    {
        string directory = TestAssetsManager.CreateTestDirectory(identifier: option).Path;
        string project = Path.Combine(directory, "App.csproj");
        File.WriteAllText(project, "<Project />");
        string[] arguments = ["build", option, "output with spaces"];
        AgentModeBuildxDispatcher.TryGetBuildxArguments(Parser.Parse(arguments), arguments, directory,
            out string[] translated, out string reason).Should().BeFalse();
        translated.Should().BeEmpty();
        reason.Should().Be("output-path-override");
    }

    [TestMethod]
    [DataRow("--no-dependencies", "unsupported-option:no-dependencies")]
    [DataRow("--force", "unsupported-option:force")]
    [DataRow("--version-suffix=demo", "unsupported-option:version-suffix")]
    [DataRow("/t:test", "unsupported-option:t")]
    [DataRow("-bl:requested.binlog", "unsupported-option:bl")]
    [DataRow("/p:BuildProjectReferences=false", "msbuild-property-override")]
    [DataRow("-p:Platform=Any CPU", "msbuild-property-override")]
    [DataRow("-p:IsImplicitlyTriggeredBuild=false", "msbuild-property-override")]
    [DataRow("-p:TargetFramework=", "msbuild-property-override")]
    [DataRow("-p:A=1;B=2", "msbuild-property-override")]
    [DataRow("-p:Example=\"a,b\"", "msbuild-property-override")]
    [DataRow("/property:SkipBunCompile=true", "msbuild-property-override")]
    [DataRow("--p:Example=a=b", "msbuild-property-override")]
    [DataRow("-property:Example=hello world", "msbuild-property-override")]
    [DataRow("--property:Configuration=Release", "msbuild-property-override")]
    [DataRow("--output=out", "output-path-override")]
    [DataRow("--artifacts-path:out", "output-path-override")]
    public void ReportsIncompatibleSemanticsBeforeLaunching(string argument, string expectedReason)
    {
        string directory = TestAssetsManager.CreateTestDirectory(identifier: expectedReason.Replace(':', '_')).Path;
        File.WriteAllText(Path.Combine(directory, "App.csproj"), "<Project />");
        string[] arguments = ["build", argument];
        AgentModeBuildxDispatcher.TryGetBuildxArguments(Parser.Parse(arguments), arguments, directory,
            out string[] translated, out string reason).Should().BeFalse();
        translated.Should().BeEmpty();
        reason.Should().Be(expectedReason);
    }

    [TestMethod]
    [DataRow("App.csproj")]
    [DataRow("App.vbproj")]
    [DataRow("App.sln")]
    [DataRow("App.slnx")]
    [DataRow("App.slnf")]
    public void ResolvesAnUnambiguousImplicitTarget(string fileName)
    {
        string directory = TestAssetsManager.CreateTestDirectory(identifier: fileName).Path;
        string target = Path.Combine(directory, fileName);
        File.WriteAllText(target, "<Project />");
        string[] arguments = ["build"];

        AgentModeBuildxDispatcher.TryGetBuildxArguments(
            Parser.Parse(arguments), arguments, directory, out string[] translated).Should().BeTrue();
        translated.Should().Equal(target, "--server");
    }

    [TestMethod]
    public void LeavesAmbiguousAndMissingTargetsWithTheSdk()
    {
        string directory = TestAssetsManager.CreateTestDirectory().Path;
        string[] arguments = ["build"];
        AgentModeBuildxDispatcher.TryGetBuildxArguments(
            Parser.Parse(arguments), arguments, directory, out _).Should().BeFalse();

        File.WriteAllText(Path.Combine(directory, "First.csproj"), "<Project />");
        File.WriteAllText(Path.Combine(directory, "Second.csproj"), "<Project />");
        AgentModeBuildxDispatcher.TryGetBuildxArguments(
            Parser.Parse(arguments), arguments, directory, out _).Should().BeFalse();
    }

    [TestMethod]
    [DataRow("build --help")]
    [DataRow("build [parse]")]
    [DataRow("build @build.rsp")]
    [DataRow("build --getProperty:TargetFramework")]
    [DataRow("build -t:Clean")]
    [DataRow("build --disable-build-servers")]
    [DataRow("build --runtime win-x64")]
    [DataRow("build --verbosity invalid")]
    [DataRow("build App.csproj Another.csproj")]
    [DataRow("build missing.csproj")]
    [DataRow("test App.csproj")]
    [DataRow("restore App.csproj")]
    public void LeavesUnsupportedFormsWithTheSdk(string command)
    {
        string directory = TestAssetsManager.CreateTestDirectory(identifier: command.Replace(' ', '_').Replace(':', '_')).Path;
        File.WriteAllText(Path.Combine(directory, "App.csproj"), "<Project />");
        string[] arguments = command.Split(' ');

        AgentModeBuildxDispatcher.TryGetBuildxArguments(
            Parser.Parse(arguments), arguments, directory, out _).Should().BeFalse();
    }

    [TestMethod]
    [DataRow("buildx.dll", true)]
    [DataRow("buildx.exe", false)]
    [ResourceLock(WellKnownResources.CurrentDirectory)]
    public void PinsTheSelectedSdkAndPreventsRecursiveDispatch(string fileName, bool managed)
    {
        string directory = TestAssetsManager.CreateTestDirectory(identifier: fileName).Path;
        string executable = Path.Combine(directory, fileName);
        File.WriteAllText(executable, "");
        string host = SdkTestContext.Current.ToolsetUnderTest.DotNetHostPath;
        string sdk = Path.Combine(directory, "sdk");
        string[] arguments = ["App with spaces.csproj", "--server", "-p:Example=hello world"];

        var startInfo = AgentModeBuildxDispatcher.CreateStartInfo(executable, arguments, host, sdk);

        startInfo.FileName.Should().Be(managed ? host : executable);
        startInfo.ArgumentList.Should().Equal(managed ? [executable, .. arguments] : arguments);
        startInfo.UseShellExecute.Should().BeFalse();
        startInfo.WorkingDirectory.Should().Be(Environment.CurrentDirectory);
        startInfo.Environment[AgentModeBuildxDispatcher.BypassVariable].Should().Be("1");
        startInfo.Environment["FASTBUILD_SDK_PATH"].Should().Be(sdk);
        startInfo.Environment["DOTNET_HOST_PATH"].Should().Be(host);
        startInfo.Environment["DOTNET_ROOT"].Should().Be(Path.GetDirectoryName(host));
        startInfo.Environment["PATH"].Should().StartWith(Path.GetDirectoryName(host) + Path.PathSeparator);
    }

    [TestMethod]
    public void RejectsUnprovisionedOrRecursiveExecutables()
    {
        string host = SdkTestContext.Current.ToolsetUnderTest.DotNetHostPath;
        foreach (string? path in new[] { null, "relative.exe", Path.Combine(Path.GetDirectoryName(host)!, "missing.exe"), host })
        {
            Action create = () => AgentModeBuildxDispatcher.CreateStartInfo(path, [], host, Path.GetDirectoryName(host)!);
            create.Should().Throw<GracefulException>();
        }
    }

    [TestMethod]
    [DataRow(false, "minimal")]
    [DataRow(true, "minimal")]
    [DataRow(false, "diag")]
    [DataRow(true, "diag")]
    public void TheRedistHostDispatchesAndPreservesTheChildExitCode(bool aot, string verbosity)
    {
        var asset = TestAssetsManager.CopyTestAsset("TestAppSimple", identifier: aot + verbosity).WithSource();
        File.WriteAllText(Path.Combine(asset.Path, "Program.cs"), """
            System.Console.WriteLine("buildx-probe");
            System.Console.WriteLine(System.Environment.GetEnvironmentVariable("DOTNET_AGENT_DISPATCH_BYPASS"));
            System.Console.WriteLine(System.Environment.GetEnvironmentVariable("FASTBUILD_SDK_PATH"));
            System.Console.WriteLine(System.Environment.CurrentDirectory);
            foreach (string argument in args) System.Console.WriteLine(argument);
            return 37;
            """);
        new DotnetBuildCommand(Log, asset.Path)
            .WithEnvironmentVariable(AgentModeBuildxDispatcher.ModeVariable, "off")
            .Execute("-c", "Debug").Should().Pass();
        string probe = Path.Combine(
            OutputPathCalculator.FromProject(asset.Path).GetOutputDirectory(configuration: "Debug"), "TestAppSimple.dll");

        var result = new DotnetCommand(Log)
            .WithWorkingDirectory(asset.Path)
            .WithEnvironmentVariable("DOTNET_CLI_ENABLEAOT", aot.ToString())
            .WithEnvironmentVariable("DOTNET_CLI_TELEMETRY_OPTOUT", "1")
            .WithEnvironmentVariable("AI_AGENT", "github_copilot_app_agent")
            .WithEnvironmentVariable(AgentModeBuildxDispatcher.ModeVariable, "")
            .WithEnvironmentVariable(AgentModeBuildxDispatcher.BypassVariable, "")
            .WithEnvironmentVariable(AgentModeBuildxDispatcher.BuildxPathVariable, probe)
            .Execute("build", "-c", "Release with spaces", "-v:" + verbosity);

        result.ExitCode.Should().Be(37);
        if (verbosity == "diag")
        {
            result.StdErr.Should().Contain("Release with spaces");
        }
        else
        {
            result.StdErr.Should().NotContain("Release with spaces");
        }
        result.StdOut.Should().Contain("buildx-probe")
            .And.Contain("1" + Environment.NewLine)
            .And.Contain(asset.Path)
            .And.Contain("--server")
            .And.Contain("-p:Configuration=Release with spaces");
        result.StdOut.Should().NotContain("Microsoft.FastBuild");
        if (!SdkTestContext.IsLocalized())
        {
            result.StdOut.Should().Contain("Agent mode: using experimental buildx.");
            if (verbosity == "diag")
            {
                result.StdErr.Should().Contain("Agent mode buildx command:");
            }
        }
    }

    [TestMethod]
    [DataRow("minimal")]
    [DataRow("detailed")]
    public void ExplainsStockFallbackOnTheConsoleAndPreservesFailure(string verbosity)
    {
        var asset = TestAssetsManager.CopyTestAsset("TestAppSimple", identifier: verbosity).WithSource();
        var result = new DotnetCommand(Log).WithWorkingDirectory(asset.Path)
            .WithEnvironmentVariable(AgentModeBuildxDispatcher.ModeVariable, "on")
            .WithEnvironmentVariable(AgentModeBuildxDispatcher.BuildxPathVariable, "not-a-tool")
            .Execute("build", "--no-dependencies", "--no-restore", "-v:" + verbosity);
        result.ExitCode.Should().NotBe(0);
        result.StdOut.Should().NotContain("Agent mode: using experimental buildx.");
        result.StdErr.Should().Contain("unsupported-option:no-dependencies").And.NotContain("not-a-tool");
        if (!SdkTestContext.IsLocalized())
        {
            result.StdErr.Should().Contain("Agent mode: using the stock SDK (unsupported-option:no-dependencies).");
        }

    }

    [TestMethod]
    [DataRow("-p:Example=hello world")]
    [DataRow("/property:Example=hello world")]
    [DataRow("--property:Configuration=Release")]
    public void ExplainsPropertyOverridesWithoutLaunchingTheTool(string property)
    {
        var asset = TestAssetsManager.CopyTestAsset("TestAppSimple", identifier: property.Replace(':', '_')).WithSource();
        var result = new DotnetCommand(Log).WithWorkingDirectory(asset.Path)
            .WithEnvironmentVariable(AgentModeBuildxDispatcher.ModeVariable, "on")
            .WithEnvironmentVariable(AgentModeBuildxDispatcher.BuildxPathVariable, "not-a-tool")
            .Execute("build", property, "--no-restore");
        result.ExitCode.Should().NotBe(0);
        result.StdErr.Should().NotContain("hello world").And.NotContain("not-a-tool");
        if (!SdkTestContext.IsLocalized())
        {
            result.StdErr.Should().Contain("buildx skipped due to MSBuild property override");
        }
    }
}
