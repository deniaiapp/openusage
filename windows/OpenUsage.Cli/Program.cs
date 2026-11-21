using System.Reflection;
using OpenUsage.Core.Services;
using OpenUsage.Core.Support;

var version = Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
return LimitsCommand.Run(args, Path.Combine(AppPaths.AppData, "OpenUsage"),
    Console.Out, Console.Error, DateTimeOffset.UtcNow, version);
