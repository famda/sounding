namespace Sounding.Agent.Reporting;

// Something a sampler noticed, written to the events table with this container's id.
public sealed record AgentEvent(string Kind, string Level, string Message);
