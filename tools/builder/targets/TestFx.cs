using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

[Target(nameof(TestFx),
        nameof(Build))]
public static class TestFx
{
    public static async Task OnExecute(BuildContext context)
    {
        context.BuildStep("Running .NET Framework tests");

        await Task.Delay(0);

        // var xunitConsoleExe = Path.Combine("src", "xunit.console", "bin", context.ConfigurationText, "net472", "xunit.console.exe");
        // var testV1Dll = Path.Combine("test", "test.xunit1", "bin", context.ConfigurationText, "net45", "test.xunit1.dll");
        // await context.Exec(xunitConsoleExe, $"{testV1Dll} -xml artifacts/test/v1.xml -html artifacts/test/v1.html -appdomains denied {context.TestFlagsNonParallel}");

        // TODO: Eventually need to update this to use a multi-runner
        var netFxSubpath = Path.Combine("bin", context.ConfigurationText, "net4");
        var testV3Apps = Directory.GetFiles(context.BaseFolder, "xunit3.*.test.exe", SearchOption.AllDirectories)
                                  .Where(x => x.Contains(netFxSubpath))
                                  .OrderBy(x => x)
                                  .Select(x => new
                                  {
                                      dll = x.Substring(context.BaseFolder.Length + 1),
                                      xml = $"artifacts/test/netfx-{Path.GetFileNameWithoutExtension(x)}.xml",
                                      html = $"artifacts/test/netfx-{Path.GetFileNameWithoutExtension(x)}.html"
                                  });

        foreach (var testV3App in testV3Apps)
            await context.Exec(testV3App.dll, $"-xml {testV3App.xml} -html {testV3App.html} {context.TestFlagsParallel}");

        Console.WriteLine();
    }
}
