using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using OpenUsage.Core;
using OpenUsage.Core.Providers.Devin;
using OpenUsage.Core.Support;
using Button = System.Windows.Controls.Button;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using Orientation = System.Windows.Controls.Orientation;

namespace OpenUsage.App.Views;

internal sealed class DevinSignInView : StackPanel
{
    private CancellationTokenSource? _pending;

    public DevinSignInView(AppContainer container, DevinProvider provider)
    {
        var signIn = new Button { Content = ButtonTitle(), Margin = new Thickness(0, 0, 6, 0) };
        var cancel = new Button { Content = "Cancel", Visibility = Visibility.Collapsed };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Left };
        buttons.Children.Add(signIn);
        buttons.Children.Add(cancel);
        Children.Add(buttons);
        var status = new TextBlock
        {
            Text = provider.IsSignedIn ? "Connected" : "Connect your account in your browser.",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 6, 0, 10)
        };
        Children.Add(status);

        cancel.Click += (_, _) => Cancel();
        signIn.Click += async (_, _) =>
        {
            using var pending = new CancellationTokenSource();
            _pending = pending;
            signIn.IsEnabled = false;
            cancel.Visibility = Visibility.Visible;
            status.Text = "Finish signing in to Devin in your browser.";
            try
            {
                await container.ConnectDevinAsync(provider, OpenBrowser, pending.Token);
                status.Text = "Connected";
            }
            catch (Exception) when (pending.IsCancellationRequested)
            {
                status.Text = "Sign-in canceled.";
            }
            catch (Exception ex)
            {
                AppLog.Warn("devin", $"Sign-in failed ({ex.GetType().Name}).");
                status.Text = ex is ProviderRequestException ? ex.Message : "Couldn't sign in to Devin. Try again.";
            }
            finally
            {
                _pending = null;
                signIn.Content = ButtonTitle();
                signIn.IsEnabled = true;
                cancel.Visibility = Visibility.Collapsed;
            }
        };

        string ButtonTitle() => provider.IsSignedIn ? "Sign In Again…" : "Sign In to Devin…";
    }

    public bool IsPending => _pending is not null;

    public void Cancel() => _pending?.Cancel();

    private static void OpenBrowser(Uri url)
    {
        try { Process.Start(new ProcessStartInfo(url.AbsoluteUri) { UseShellExecute = true }); }
        catch (Exception ex) { throw new ProviderRequestException("Couldn't open your browser.", ex); }
    }
}
