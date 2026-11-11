using Microsoft.Extensions.Configuration;

namespace Sounding.Dashboard;

public sealed class DashboardOptions
{
    [ConfigurationKeyName("STALE_AFTER_SECONDS")]
    public int StaleAfterSeconds { get; set; } = 10;

    [ConfigurationKeyName("HIDE_OFFLINE_HOSTS_AFTER_MINUTES")]
    public int HideOfflineHostsAfterMinutes { get; set; } = 60;

    [ConfigurationKeyName("HIDE_OFFLINE_CONTAINERS_AFTER_MINUTES")]
    public int HideOfflineContainersAfterMinutes { get; set; } = 10;

    [ConfigurationKeyName("HIDE_STOPPED_CONTAINERS_AFTER_MINUTES")]
    public int HideStoppedContainersAfterMinutes { get; set; } = 2;
}
