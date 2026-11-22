using OpenUsage.Core.Models;

namespace OpenUsage.App;

public partial class DashboardWindow
{
    private void RenderPriorityLimitSettings()
    {
        ContentHost.Children.Add(SectionTitle("Priority Limits"));
        var limits = _container.Settings.PriorityLimits;
        AddThreshold("Warning at", PriorityLimits.MinWarning, PriorityLimits.MaxWarning, limits.Warning, limits.WithWarning);
        AddThreshold("Critical at", PriorityLimits.MinCritical, PriorityLimits.MaxCritical, limits.Critical, limits.WithCritical);
        ContentHost.Children.Add(SettingToggle("Show Use It or Lose It",
            _container.Settings.ShowUnusedQuotaInPriority, value =>
            {
                _container.Settings.ShowUnusedQuotaInPriority = value;
                _container.Settings.Save();
                _container.Data.NotifyPresentationChanged();
            }));
    }

    private void AddThreshold(string label, int min, int max, int selected, Func<int, PriorityLimits> change)
    {
        var values = Enumerable.Range(0, (max - min) / PriorityLimits.Step + 1)
            .Select(index => min + index * PriorityLimits.Step).ToArray();
        ContentHost.Children.Add(SettingCombo(label, values.Select(value => $"{value}% left").ToArray(),
            Array.IndexOf(values, selected), index =>
            {
                var settings = _container.Settings;
                var limits = change(values[index]);
                if (settings.PriorityLimits == limits) return;
                settings.PriorityLimits = limits;
                settings.Save();
                _container.Data.NotifyPresentationChanged();
            }));
    }
}
