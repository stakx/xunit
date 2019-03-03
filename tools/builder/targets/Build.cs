using System.Threading.Tasks;

[Target(nameof(Build),
        nameof(Restore))]
public static class Build
{
    public static Task OnExecute(BuildContext context)
    {
        context.BuildStep("Compiling binaries");

        return context.Exec("dotnet", $"build --no-restore --configuration {context.ConfigurationText}");
    }
}
