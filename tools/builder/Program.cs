using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Bullseye.Internal;
using McMaster.Extensions.CommandLineUtils;
using Newtonsoft.Json;
using SimpleExec;

[Command(Name = "build", Description = "Build utility for xUnit.net")]
[HelpOption("-?|-h|--help")]
public class Program
{
    static string baseFolder = Directory.GetCurrentDirectory();
    static string nuGetVersion = "4.9.2";
    static string signClientVersion = "0.9.1";

    readonly bool needMono;
    readonly string nonparallelFlags = "-parallel none -maxthreads 1";
    readonly string nuGetExe;
    readonly string nuGetUrl = $"https://dist.nuget.org/win-x86-commandline/v{nuGetVersion}/nuget.exe";
    readonly string packageOutputFolder = Path.Combine(baseFolder, "artifacts", "packages");
    readonly string signClientFolder = Path.Combine(baseFolder, "packages", $"SignClient.{signClientVersion}");
    readonly string signClientAppSettings = Path.Combine(baseFolder, "tools", "SignClient", "appsettings.json");
    readonly string[] submoduleFolders = new[] { Path.Combine(baseFolder, "src", "xunit.assert", "Asserts") };
    readonly string testOutputFolder = Path.Combine(baseFolder, "artifacts", "test");

    string parallelFlags = "-parallel all -maxthreads 16";

    public Program()
    {
        needMono = !RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

        var homeFolder = needMono
            ? Environment.GetEnvironmentVariable("HOME")
            : Environment.GetEnvironmentVariable("USERPROFILE");

        var nuGetCliFolder = Path.Combine(homeFolder, ".nuget", "cli", nuGetVersion);
        nuGetExe = Path.Combine(nuGetCliFolder, "nuget.exe");

        Directory.CreateDirectory(nuGetCliFolder);
        Directory.CreateDirectory(testOutputFolder);
    }

    [Option("--buildAssemblyVersion", Description = "Set the build assembly version (default: '99.99.99.0')")]
    public string BuildAssemblyVersion { get; }

    [Option("--buildSemanticVersion", Description = "Set the build semantic version (default: '99.99.99-dev')")]
    public string BuildSemanticVersion { get; }

    [Option("-c|--configuration", Description = "The target configuration (default: 'Release')")]
    public Configuration Configuration { get; } = Configuration.Release;

    string ConfigurationText => Configuration.ToString();

    [Option("-N|--no-color", Description = "Disable colored output")]
    public bool NoColor { get; }

    string NuGetExe { get; }

    [Option("-s|--skip-dependencies", Description = "Do not run targets' dependencies")]
    public bool SkipDependencies { get; }

    [Argument(0, "targets", Description = "The target(s) to run (default: 'Test')")]
    public BuildTarget[] Targets { get; } = new[] { BuildTarget.Test };

    [Option("-v|--verbose", Description = "Enable verbose output")]
    public bool Verbose { get; }

    void BuildStep(string message)
    {
        WriteLineColor(ConsoleColor.White, $"==> {message} <==");
        Console.WriteLine();
    }

    Task CmdBuild()
    {
        BuildStep("Compiling binaries");

        return Exec("dotnet", $"build --no-restore --configuration {ConfigurationText}");
    }

    async Task CmdDownloadNuGet()
    {
        if (File.Exists(nuGetExe))
            return;

        using (var httpClient = new HttpClient())
        using (var stream = File.OpenWrite(nuGetExe))
        {
            BuildStep($"Downloading {nuGetUrl} to {nuGetExe}");

            var response = await httpClient.GetAsync(nuGetUrl);
            response.EnsureSuccessStatusCode();

            await response.Content.CopyToAsync(stream);
        }
    }

    async Task CmdPackages()
    {
        BuildStep("Creating NuGet packages");

        var nuspecFiles = Directory.GetFiles(baseFolder, "*.nuspec", SearchOption.AllDirectories)
                                   .OrderBy(x => x)
                                   .Select(x => x.Substring(baseFolder.Length + 1));

        foreach (var nuspecFile in nuspecFiles)
            await Exec(nuGetExe, $"pack {nuspecFile} -NonInteractive -NoPackageAnalysis -OutputDirectory {packageOutputFolder} -Properties Configuration={ConfigurationText}");
    }

    async Task CmdPushMyGet()
    {
        BuildStep("Pushing packages to MyGet");

        var myGetApiKey = Environment.GetEnvironmentVariable("MyGetApiKey");
        if (myGetApiKey == null)
        {
            WriteLineColor(ConsoleColor.Yellow, "Skipping MyGet push because environment variable 'MyGetApiKey' is not set.");
            return;
        }

        var packageFiles = Directory.GetFiles(packageOutputFolder, "*.nupkg", SearchOption.AllDirectories)
                                    .OrderBy(x => x)
                                    .Select(x => x.Substring(baseFolder.Length + 1));

        foreach (var packageFile in packageFiles)
        {
            var args = $"push -source https://www.myget.org/F/xunit/api/v2/package -apiKey {myGetApiKey} {packageFile}";
            var redactedArgs = args.Replace(myGetApiKey, "[redacted]");
            await Exec(nuGetExe, args, redactedArgs);
        }
    }

    Task CmdRestore()
    {
        BuildStep("Restoring NuGet packages");

        return Exec("dotnet", "restore");
    }

    async Task CmdSetVersion()
    {
        if (BuildAssemblyVersion != null)
        {
            BuildStep($"Setting assembly version: {BuildAssemblyVersion}");

            var filesToPatch = Directory.GetFiles(baseFolder, "GlobalAssemblyInfo.cs", SearchOption.AllDirectories);
            foreach (var fileToPatch in filesToPatch)
            {
                WriteLineColor(ConsoleColor.DarkGray, $"PATCH: {fileToPatch}");

                var text = await File.ReadAllTextAsync(fileToPatch);
                var newText = text.Replace("99.99.99.0", BuildAssemblyVersion);
                if (newText != text)
                    await File.WriteAllTextAsync(fileToPatch, newText);
            }

            Console.WriteLine();
        }

        if (BuildSemanticVersion != null)
        {
            BuildStep($"Setting semantic version: {BuildSemanticVersion}");

            var filesToPatch = Directory.GetFiles(baseFolder, "GlobalAssemblyInfo.cs", SearchOption.AllDirectories)
                       .Concat(Directory.GetFiles(baseFolder, "*.nuspec", SearchOption.AllDirectories));

            foreach (var fileToPatch in filesToPatch)
            {
                WriteLineColor(ConsoleColor.DarkGray, $"PATCH: {fileToPatch}");

                var text = await File.ReadAllTextAsync(fileToPatch);
                var newText = text.Replace("99.99.99-dev", BuildSemanticVersion);
                if (newText != text)
                    await File.WriteAllTextAsync(fileToPatch, newText);
            }

            Console.WriteLine();
        }
    }

    async Task CmdSignPackages()
    {
        var signClientUser = Environment.GetEnvironmentVariable("SignClientUser");
        var signClientSecret = Environment.GetEnvironmentVariable("SignClientSecret");
        if (signClientUser == null || signClientSecret == null)
        {
            WriteLineColor(ConsoleColor.Yellow, "Skipping packing signing because environment variables 'SignClientUser' and/or 'SignClientSecret' are not set.");
            return;
        }

        if (!Directory.Exists(signClientFolder))
        {
            BuildStep($"Downloading SignClient {signClientVersion}");

            await Exec(nuGetExe, $"install SignClient -version {signClientVersion} -SolutionDir \"{baseFolder}\" -Verbosity quiet -NonInteractive");
        }

        BuildStep("Signing NuGet packages");

        var appPath = Path.Combine(signClientFolder, "tools", "netcoreapp2.0", "SignClient.dll");
        var packageFiles = Directory.GetFiles(packageOutputFolder, "*.nupkg", SearchOption.AllDirectories)
                                    .OrderBy(x => x)
                                    .Select(x => x.Substring(baseFolder.Length + 1));

        foreach (var packageFile in packageFiles)
        {
            var args = $"\"{appPath}\" sign -c \"{signClientAppSettings}\" -r \"{signClientUser}\" -s \"{signClientSecret}\" -n \"xUnit.net\" -d \"xUnit.net\" -u \"https://github.com/xunit/xunit\" -i \"{packageFile}\"";
            var redactedArgs = args.Replace(signClientUser, "[redacted]")
                                   .Replace(signClientSecret, "[redacted]");

            await Exec("dotnet", args, redactedArgs);
        }
    }

    Task CmdTestCore()
    {
        BuildStep("Running .NET Core tests");

        var netCoreSubpath = Path.Combine("bin", ConfigurationText, "netcoreapp");
        var testDlls = Directory.GetFiles(baseFolder, "test.xunit.*.dll", SearchOption.AllDirectories)
                                .Where(x => x.Contains(netCoreSubpath))
                                .OrderBy(x => x)
                                .Select(x => x.Substring(baseFolder.Length + 1));

        Console.WriteLine($"Would run: {string.Join(" ", testDlls)}");

        Console.WriteLine();
        return Task.CompletedTask;
    }

    Task CmdTestFx()
    {
        BuildStep("Running .NET Framework tests");

        var net472Subpath = Path.Combine("bin", ConfigurationText, "net472");
        var testV1Dll = Path.Combine("test", "test.xunit1", "bin", ConfigurationText, "net45", "test.xunit1.dll");
        var testDlls = Directory.GetFiles(baseFolder, "test.xunit.*.dll", SearchOption.AllDirectories)
                                .Where(x => x.Contains(net472Subpath))
                                .Select(x => x.Substring(baseFolder.Length + 1));

        var xunitConsoleExe = Path.Combine("src", "xunit.console", "bin", ConfigurationText, "net472", "xunit.console.exe");

        // await Exec(xunitConsoleExe, $"{testV1Dll} -xml artifacts/test/v1.xml -html artifacts/test/v1.html -appdomains denied {nonparallelFlags}");
        // await Exec(xunitConsoleExe, $"{string.Join(" ", testDlls)} -xml artifacts/test/v2.xml -html artifacts/test/v2.html -appdomains denied -serialize {parallelFlags}");

        Console.WriteLine($"Would run: {testV1Dll}");
        Console.WriteLine($"Would run: {string.Join(" ", testDlls)}");

        Console.WriteLine();
        return Task.CompletedTask;
    }

    Task CmdValidateEnvironment()
    {
        foreach (var submoduleFolder in submoduleFolders)
        {
            var filesInSubmoduleFolder = Directory.GetFiles(submoduleFolder);
            if (filesInSubmoduleFolder.Length == 0)
            {
                WriteLineColor(ConsoleColor.Red, "One or more submodules is missing. Please run 'git submodule update --init'.");
                throw new NonZeroExitCodeException(1);
            }
        }

        return Task.CompletedTask;
    }

    async Task Exec(string name, string args, string redactedArgs = null, string workingDirectory = null)
    {
        if (redactedArgs == null)
            redactedArgs = args;

        if (needMono && name.EndsWith(".exe"))
        {
            args = $"{name} {args}";
            redactedArgs = $"{name} {redactedArgs}";
            name = "mono";
        }

        WriteLineColor(ConsoleColor.DarkGray, $"EXEC: {name} {redactedArgs}{Environment.NewLine}");
        await Command.RunAsync(name, args, workingDirectory, /*noEcho*/ true);
        Console.WriteLine();
    }

    public static Task<int> Main(string[] args)
        => CommandLineApplication.ExecuteAsync<Program>(args);

    async Task<int> OnExecuteAsync()
    {
        Exception error = default;

        try
        {
            // Parse the targets and Bullseye-specific arguments
            var bullseyeArguments = Targets.Select(x => x.ToString());
            if (NoColor)
                bullseyeArguments = bullseyeArguments.Append("--no-color");
            if (SkipDependencies)
                bullseyeArguments = bullseyeArguments.Append("--skip-dependencies");
            if (Verbose)
                bullseyeArguments = bullseyeArguments.Append("--verbose");

            if (Targets.Contains(BuildTarget.CI))
                parallelFlags = nonparallelFlags;

            var targetCollection = new TargetCollection();

            // Meta targets
            targetCollection.Add(new Target("CI", new[] { "SetVersion", "Test", "Packages", "SignPackages", "PushMyGet" }));
            targetCollection.Add(new Target("Test", new[] { "TestCore", "TestFx" }));

            // Core targets
            targetCollection.Add(new ActionTarget("Build", new[] { "Restore" }, CmdBuild));
            targetCollection.Add(new ActionTarget("Packages", new[] { "Build", "DownloadNuGet" }, CmdPackages));
            targetCollection.Add(new ActionTarget("PushMyGet", new[] { "DownloadNuGet" }, CmdPushMyGet));
            targetCollection.Add(new ActionTarget("Restore", new[] { "ValidateEnvironment" }, CmdRestore));
            targetCollection.Add(new ActionTarget("SetVersion", default, CmdSetVersion));
            targetCollection.Add(new ActionTarget("SignPackages", new[] { "Packages" }, CmdSignPackages));
            targetCollection.Add(new ActionTarget("TestCore", new[] { "Build" }, CmdTestCore));
            targetCollection.Add(new ActionTarget("TestFx", new[] { "Build" }, CmdTestFx));
            targetCollection.Add(new ActionTarget("ValidateEnvironment", default, CmdValidateEnvironment));

            // Utility targets
            targetCollection.Add(new ActionTarget("DownloadNuGet", default, CmdDownloadNuGet));

            await targetCollection.RunAsync(bullseyeArguments, new NullConsole());
            return 0;
        }
        catch (TargetFailedException ex)
        {
            error = ex.InnerException;
        }
        catch (Exception ex)
        {
            error = ex;
        }

        Console.WriteLine();

        if (error is NonZeroExitCodeException nonZeroExit)
        {
            WriteLineColor(ConsoleColor.Red, "==> Build failed! <==");
            return nonZeroExit.ExitCode;
        }

        WriteLineColor(ConsoleColor.Red, $"==> Build failed! An unhandled exception was thrown <==");
        Console.WriteLine(error.ToString());
        return -1;
    }

    void WriteColor(ConsoleColor foregroundColor, string text)
    {
        if (!NoColor)
            Console.ForegroundColor = foregroundColor;

        Console.Write(text);

        if (!NoColor)
            Console.ResetColor();
    }

    void WriteLineColor(ConsoleColor foregroundColor, string text)
        => WriteColor(foregroundColor, $"{text}{Environment.NewLine}");
}
