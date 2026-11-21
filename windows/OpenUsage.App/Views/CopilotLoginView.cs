using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using OpenUsage.Core;
using OpenUsage.Core.Providers.Copilot;
using OpenUsage.Core.Support;
using Button = System.Windows.Controls.Button;
using Orientation = System.Windows.Controls.Orientation;
using TextBox = System.Windows.Controls.TextBox;

namespace OpenUsage.App.Views;

internal sealed class CopilotLoginView : StackPanel
{
    private readonly AppContainer _container;
    private readonly CopilotProvider _copilot;
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 6) };
    private readonly TextBox _code = new() { IsReadOnly = true, Visibility = Visibility.Collapsed, Padding = new Thickness(6) };
    private readonly Button _signIn;
    private readonly Button _openGitHub;
    private readonly Button _cancel;
    private CancellationTokenSource? _attempt;
    private bool _returnFromBrowser;

    public CopilotLoginView(AppContainer container, CopilotProvider copilot, Style buttonStyle)
    {
        _container = container;
        _copilot = copilot;
        Margin = new Thickness(0, 0, 0, 10);
        _signIn = new Button { Content = SignInLabel, Style = buttonStyle };
        _openGitHub = new Button { Content = "Open GitHub", Style = buttonStyle, Visibility = Visibility.Collapsed };
        _cancel = new Button { Content = "Cancel", Style = buttonStyle, Visibility = Visibility.Collapsed };
        AutomationProperties.SetLiveSetting(_status, AutomationLiveSetting.Polite);
        AutomationProperties.SetName(_code, "GitHub device code");
        Children.Add(_status);
        Children.Add(_code);
        var controls = new StackPanel { Orientation = Orientation.Horizontal };
        controls.Children.Add(_signIn);
        controls.Children.Add(_openGitHub);
        controls.Children.Add(_cancel);
        Children.Add(controls);

        _status.Text = _copilot.HasAppCredential
            ? "GitHub is connected to OpenUsage."
            : "Connect a GitHub account with access to Copilot.";
        _signIn.Click += async (_, _) => await SignInAsync();
        _openGitHub.Click += (_, _) =>
        {
            _returnFromBrowser = true;
            DashboardWindow.OpenUrl(CopilotProvider.VerificationUrl);
        };
        _cancel.Click += (_, _) => _attempt?.Cancel();
        Unloaded += (_, _) => _attempt?.Cancel();
    }

    private string SignInLabel => _copilot.HasAppCredential ? "Sign In Again" : "Sign In with GitHub";

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
            await _container.ConnectCopilotAsync(code =>
            {
                _status.Text = "Copy this code, then enter it on GitHub.";
                _code.Text = code;
                _code.Visibility = Visibility.Visible;
                _openGitHub.Visibility = Visibility.Visible;
                _code.Focus();
                _code.SelectAll();
            }, attempt.Token);
            _status.Text = _container.Layout.IsProviderEnabled("copilot")
                ? "GitHub is connected to OpenUsage."
                : "GitHub is connected. Turn on Copilot in Customize to show its usage.";
        }
        catch (OperationCanceledException)
        {
            _status.Text = "Sign-in canceled.";
        }
        catch (ProviderRequestException ex)
        {
            AppLog.Warn("copilot", ex.Message);
            _status.Text = ex.Message;
        }
        catch (Exception ex)
        {
            AppLog.Error("copilot", "could not save the GitHub sign-in", ex);
            _status.Text = "OpenUsage couldn't save your GitHub sign-in. Try again.";
        }
        finally
        {
            _attempt = null;
            _code.Clear();
            _code.Visibility = Visibility.Collapsed;
            _openGitHub.Visibility = Visibility.Collapsed;
            _cancel.Visibility = Visibility.Collapsed;
            _signIn.Content = SignInLabel;
            _signIn.Visibility = Visibility.Visible;
        }
    }
}
