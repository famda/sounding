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

    // Samples are kept in whole hours: between this and one hour more.
    [ConfigurationKeyName("RETENTION_HOURS")]
    public int RetentionHours { get; set; } = 24;

    // A database role the agents connect as: the dashboard grants it what agents need, and nothing more.
    [ConfigurationKeyName("AGENT_ROLE")]
    public string? AgentRole { get; set; }
}
