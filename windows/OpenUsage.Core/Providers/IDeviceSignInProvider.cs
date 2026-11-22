using OpenUsage.Core.Models;
using OpenUsage.Core.Services;

namespace OpenUsage.Core.Providers;

public interface IDeviceSignInProvider : IProviderRuntime
{
    string SignInAuthority { get; }
    bool HasAppCredential { get; }
    Task<ProviderSnapshot> ConnectAsync(Action<DevicePrompt> showCode, CancellationToken cancellationToken);
}
