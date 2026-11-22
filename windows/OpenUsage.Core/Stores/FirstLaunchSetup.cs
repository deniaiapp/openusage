using OpenUsage.Core.Models;
using OpenUsage.Core.Providers;
using OpenUsage.Core.Support;

namespace OpenUsage.Core.Stores;

public enum ConnectionState { NotChecked, Checking, Connected, Failed }

/// <summary>One provider row on the welcome screen.</summary>
public sealed class FirstLaunchChoice
{
    public FirstLaunchChoice(Provider provider) => Provider = provider;

    public Provider Provider { get; }
    public string Id => Provider.Id;
    public bool Detected { get; internal set; }
    public ConnectionState Connection { get; internal set; } = ConnectionState.NotChecked;
    public string? FailureMessage { get; internal set; }
}

/// <summary>
/// The welcome screen's model. A fresh install starts with nothing on; detection only looks at what's
/// on this PC (local files, Credential Manager — never the network) and preselects what it finds.
/// Connect turns the picked providers on and refreshes them one at a time; a provider that fails goes
/// back off and shows why. Scheduled refreshes start once the welcome screen closes.
/// </summary>
public sealed class FirstLaunchSetup
{
    private readonly LayoutStore _layout;
    private readonly WidgetDataStore _data;
    private readonly AppSettings _settings;

    public FirstLaunchSetup(LayoutStore layout, WidgetDataStore data, AppSettings settings)
    {
        _layout = layout;
        _data = data;
        _settings = settings;
        Choices = layout.Providers.Where(p => !p.IsAccountTemplate).Select(p => new FirstLaunchChoice(p.Provider)).ToList();
    }

    /// <summary>
    /// Marks a fresh install (or one that quit before leaving the welcome screen) as owing the welcome,
    /// starting it with no providers on. Returns whether the welcome screen should show.
    /// </summary>
    public static bool MarkPendingIfNeeded(AppSettings settings)
    {
        if (!settings.Seeded)
        {
            settings.EnabledProviders = new HashSet<string>(StringComparer.Ordinal);
            settings.Seeded = true;
            settings.WelcomePending = true;
            settings.CustomizeHintDismissed = false;
            settings.Save();
            AppLog.Info("config", "first run: no providers on until the welcome screen");
        }
        return settings.WelcomePending;
    }

    public event Action? Changed;

    public IReadOnlyList<FirstLaunchChoice> Choices { get; }
    public HashSet<string> SelectedIds { get; } = new(StringComparer.Ordinal);
    public bool ShowAll { get; private set; }
    public bool IsDetecting { get; private set; } = true;
    public bool IsConnecting { get; private set; }
    public bool HasAttemptedConnection { get; private set; }

    public IReadOnlyList<FirstLaunchChoice> VisibleChoices =>
        Choices.Where(c => ShowAll || c.Detected).ToList();

    public bool HasFailures => Choices.Any(c => c.Connection == ConnectionState.Failed);

    public bool CanConnect => !IsDetecting && !IsConnecting && SelectedIds.Count > 0;

    public void ToggleShowAll()
    {
        ShowAll = !ShowAll;
        Raise();
    }

    public void SetSelected(string providerId, bool selected)
    {
        if (selected) SelectedIds.Add(providerId);
        else SelectedIds.Remove(providerId);
        Raise();
    }

    /// <summary>
    /// Probes every provider's local credentials concurrently on the thread pool, so file and Credential
    /// Manager reads never block the UI thread, then preselects the providers that have a sign-in.
    /// </summary>
    public async Task DetectAsync(CancellationToken cancellationToken = default)
    {
        var probes = _layout.Providers.Where(p => !p.IsAccountTemplate).Select(provider => Task.Run(async () =>
        {
            try
            {
                return await provider.HasLocalCredentialsAsync(cancellationToken) ? provider.Provider.Id : null;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                AppLog.Warn(provider.Provider.Id, $"credential probe failed: {ex.Message}");
                return null;
            }
        }, cancellationToken));
        var detected = (await Task.WhenAll(probes)).OfType<string>().ToHashSet(StringComparer.Ordinal);
        cancellationToken.ThrowIfCancellationRequested();
        foreach (var choice in Choices)
            choice.Detected = detected.Contains(choice.Id);
        SelectedIds.Clear();
        SelectedIds.UnionWith(detected);
        IsDetecting = false;
        AppLog.Info("config", $"welcome: detected {string.Join(", ", detected.Order())}");
        Raise();
    }

    /// <summary>Turns on each selected provider and refreshes it, one at a time. Retry skips ones that connected.</summary>
    public async Task ConnectSelectedAsync(CancellationToken cancellationToken = default)
    {
        if (IsConnecting || IsDetecting) return;
        IsConnecting = true;
        HasAttemptedConnection = true;
        Raise();
        try
        {
            foreach (var choice in Choices)
            {
                if (!SelectedIds.Contains(choice.Id) || choice.Connection == ConnectionState.Connected) continue;
                cancellationToken.ThrowIfCancellationRequested();
                choice.Connection = ConnectionState.Checking;
                choice.FailureMessage = null;
                Raise();
                await ConnectAsync(choice, cancellationToken);
                AppLog.Info("config", $"welcome: {choice.Id} -> {choice.Connection}");
                Raise();
            }
        }
        finally
        {
            IsConnecting = false;
            Raise();
        }
    }

    private async Task ConnectAsync(FirstLaunchChoice choice, CancellationToken cancellationToken)
    {
        _layout.SetProviderEnabled(choice.Id, true);
        await Task.Run(() => _data.RefreshProviderAsync(choice.Id, cancellationToken), cancellationToken);
        if (_data.ProviderError(choice.Id) is { } message)
        {
            _layout.SetProviderEnabled(choice.Id, false);
            choice.Connection = ConnectionState.Failed;
            choice.FailureMessage = message;
            return;
        }
        choice.Connection = ConnectionState.Connected;
    }

    /// <summary>Leaves the welcome screen. With nothing on, the empty dashboard already points to Customize.</summary>
    public void Finish()
    {
        _settings.WelcomePending = false;
        if (_settings.EnabledProviders.Count == 0)
            _settings.CustomizeHintDismissed = true;
        _settings.Save();
        AppLog.Info("config", $"welcome: finished with {string.Join(", ", _settings.EnabledProviders.Order())}");
    }

    private void Raise() => Changed?.Invoke();
}
