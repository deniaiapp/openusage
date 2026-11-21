using OpenUsage.Core.Services;
using OpenUsage.Core.Support;

return LimitsCommand.Run(args, Path.Combine(AppPaths.AppData, "OpenUsage"),
    Console.Out, Console.Error, DateTimeOffset.UtcNow);
