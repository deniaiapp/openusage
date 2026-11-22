using System.Diagnostics;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using OpenUsage.Core;
using OpenUsage.Core.Providers.Cursor;
using OpenUsage.Core.Support;
using Button = System.Windows.Controls.Button;

namespace OpenUsage.App.Views;

internal sealed class CursorLoginView : StackPanel
{
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 6) };
    private readonly Button _signIn;
    private readonly Button _cancel;
    private readonly AppContainer _container;
    private readonly CursorProvider _cursor;
    private CancellationTokenSource? _attempt;
    private bool _returnFromBrowser;

    public CursorLoginView(AppContainer container, CursorProvider cursor, Style buttonStyle)
    {
        _container = container;
        _cursor = cursor;
        Margin = new Thickness(0, 0, 0, 10);
        AutomationProperties.SetLiveSetting(_status, AutomationLiveSetting.Polite);
        _status.Text = cursor.HasAppCredential
            ? "Cursor is connected to OpenUsage."
            : "Connect a Cursor account to see its usage.";
        _signIn = new Button { Content = SignInLabel, Style = buttonStyle };
        _cancel = new Button { Content = "Cancel", Style = buttonStyle, Visibility = Visibility.Collapsed };
        Children.Add(_status);
        Children.Add(_signIn);
        Children.Add(_cancel);
        _signIn.Click += async (_, _) => await SignInAsync();
        _cancel.Click += (_, _) => _attempt?.Cancel();
        Unloaded += (_, _) => _attempt?.Cancel();
    }

    private string SignInLabel => _cursor.HasAppCredential ? "Sign In Again" : "Sign In with Cursor";

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
        _status.Text = "Finish signing in to Cursor in your browser.";
        try
        {
            await _container.ConnectCursorAsync(url =>
            {
                try { Process.Start(new ProcessStartInfo(url.AbsoluteUri) { UseShellExecute = true })?.Dispose(); }
                catch (System.ComponentModel.Win32Exception)
                {
                    throw new ProviderRequestException("OpenUsage couldn't open your browser. Try again.");
                }
                _returnFromBrowser = true;
            }, attempt.Token);
            _status.Text = _container.Layout.IsProviderEnabled("cursor")
                ? "Cursor is connected to OpenUsage."
                : "Cursor is connected. Turn on Cursor in Customize to show its usage.";
        }
        catch (Exception ex) when (ex is OperationCanceledException || attempt.IsCancellationRequested)
        {
            _status.Text = "Sign-in canceled.";
        }
        catch (ProviderRequestException ex)
        {
            AppLog.Warn("cursor", ex.Message);
            _status.Text = ex.Message;
        }
        catch (Exception ex)
        {
            AppLog.Warn("cursor", $"could not verify and save the Cursor sign-in: {ex.GetType().Name}");
            _status.Text = "OpenUsage couldn't verify and save your Cursor sign-in. Try again.";
        }
        finally
        {
            _attempt = null;
            _cancel.Visibility = Visibility.Collapsed;
            _signIn.Content = SignInLabel;
            _signIn.Visibility = Visibility.Visible;
        }
    }
}
