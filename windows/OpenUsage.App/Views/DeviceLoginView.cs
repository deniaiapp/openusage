using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using OpenUsage.Core;
using OpenUsage.Core.Providers;
using OpenUsage.Core.Support;
using Button = System.Windows.Controls.Button;
using Orientation = System.Windows.Controls.Orientation;
using TextBox = System.Windows.Controls.TextBox;

namespace OpenUsage.App.Views;

internal sealed class DeviceLoginView : StackPanel
{
    private readonly AppContainer _container;
    private readonly IDeviceSignInProvider _provider;
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 6) };
    private readonly TextBox _code = new() { IsReadOnly = true, Visibility = Visibility.Collapsed, Padding = new Thickness(6) };
    private readonly Button _signIn;
    private readonly Button _open;
    private readonly Button _cancel;
    private CancellationTokenSource? _attempt;
    private bool _returnFromBrowser;
    private Uri? _verificationUrl;

    public DeviceLoginView(AppContainer container, IDeviceSignInProvider provider, Style style)
    {
        _container = container;
        _provider = provider;
        Margin = new Thickness(0, 0, 0, 10);
        _signIn = new Button { Content = SignInLabel, Style = style };
        _open = new Button { Content = $"Open {provider.SignInAuthority}", Style = style, Visibility = Visibility.Collapsed };
        _cancel = new Button { Content = "Cancel", Style = style, Visibility = Visibility.Collapsed };
        AutomationProperties.SetLiveSetting(_status, AutomationLiveSetting.Polite);
        AutomationProperties.SetName(_code, $"{provider.SignInAuthority} device code");
        Children.Add(_status);
        Children.Add(_code);
        var controls = new StackPanel { Orientation = Orientation.Horizontal };
        controls.Children.Add(_signIn);
        controls.Children.Add(_open);
        controls.Children.Add(_cancel);
        Children.Add(controls);

        _status.Text = provider.HasAppCredential
            ? $"{provider.Provider.DisplayName} is connected."
            : $"Connect your {provider.Provider.DisplayName} account.";
        _signIn.Click += async (_, _) => await SignInAsync();
        _open.Click += (_, _) =>
        {
            if (_verificationUrl is not { } url) return;
            _returnFromBrowser = true;
            DashboardWindow.OpenUrl(url.AbsoluteUri);
        };
        _cancel.Click += (_, _) => _attempt?.Cancel();
        Unloaded += (_, _) => _attempt?.Cancel();
    }

    private string SignInLabel => _provider.HasAppCredential ? "Sign In Again" : $"Sign In with {_provider.SignInAuthority}";

    public bool ResumeIfPending()
    {
        var resume = _attempt is not null || _returnFromBrowser;
        _returnFromBrowser = false;
        return resume;
    }

    private async Task SignInAsync()
    {
        using var attempt = new CancellationTokenSource();
        _attempt = attempt;
        _signIn.Visibility = Visibility.Collapsed;
        _cancel.Visibility = Visibility.Visible;
        _status.Text = "Requesting a sign-in code…";
        try
        {
            await _container.ConnectDeviceProviderAsync(_provider, prompt =>
            {
                _verificationUrl = prompt.VerificationUrl;
                _status.Text = $"Copy this code, then enter it on {_provider.SignInAuthority}.";
                _code.Text = prompt.Code;
                _code.Visibility = Visibility.Visible;
                _open.Visibility = Visibility.Visible;
                _code.Focus();
                _code.SelectAll();
            }, attempt.Token);
            _status.Text = !_container.Layout.IsProviderEnabled(_provider.Provider.Id)
                ? "Connected. Turn on the provider in Customize to show its usage."
                : $"{_provider.Provider.DisplayName} is connected.";
        }
        catch (OperationCanceledException)
        {
            _status.Text = "Sign-in canceled.";
        }
        catch (ProviderRequestException ex)
        {
            AppLog.Warn(_provider.Provider.Id, ex.Message);
            _status.Text = ex.Message;
        }
        catch (Exception ex)
        {
            AppLog.Error(_provider.Provider.Id, "could not save the sign-in", ex);
            _status.Text = "OpenUsage couldn't save your sign-in. Try again.";
        }
        finally
        {
            _attempt = null;
            _verificationUrl = null;
            _code.Clear();
            _code.Visibility = Visibility.Collapsed;
            _open.Visibility = Visibility.Collapsed;
            _cancel.Visibility = Visibility.Collapsed;
            _signIn.Content = SignInLabel;
            _signIn.Visibility = Visibility.Visible;
        }
    }
}
