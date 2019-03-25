using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

[Target(nameof(TestCore),
        nameof(Build))]
public static class TestCore
{
    public static async Task OnExecute(BuildContext context)
    {
        context.BuildStep("Running .NET Core tests");

        await Task.Delay(0);

        var netCoreSubpath = Path.Combine("bin", context.ConfigurationText, "netcoreapp");
        var testV3Apps = Directory.GetFiles(context.BaseFolder, "xunit3.*.test.dll", SearchOption.AllDirectories)
                                  .Where(x => x.Contains(netCoreSubpath))
                                  .OrderBy(x => x)
                                  .Select(x => new
                                  {
                                      dll = x.Substring(context.BaseFolder.Length + 1),
                                      xml = $"artifacts/test/netcore-{Path.GetFileNameWithoutExtension(x)}.xml",
                                      html = $"artifacts/test/netcore-{Path.GetFileNameWithoutExtension(x)}.html"
                                  });

        // TODO: Eventually need to update this to use a multi-runner
        foreach (var testV3App in testV3Apps)
            await context.Exec("dotnet", $"exec {testV3App.dll} -- -xml {testV3App.xml} -html {testV3App.html} {context.TestFlagsParallel}");

        Console.WriteLine();
    }
}
