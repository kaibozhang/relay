using System.Text.Json.Serialization;

namespace Relay.Core;

// Explicit names also keep JsonSerializer.Serialize(model) camelCase without custom options.
// Required JSON members prevent an incomplete file from silently becoming a new empty vault.
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public class RelayCard
{
    [JsonPropertyName("id"), JsonRequired] public string Id { get; set; } = Guid.NewGuid().ToString("N");
    [JsonPropertyName("title"), JsonRequired] public string Title { get; set; } = "";
    [JsonPropertyName("nextAction"), JsonRequired] public string NextAction { get; set; } = "";
    [JsonPropertyName("context"), JsonRequired] public string Context { get; set; } = "";
    [JsonPropertyName("resources"), JsonRequired] public List<ResourceRef> Resources { get; set; } = [];
    [JsonPropertyName("status"), JsonRequired] public string Status { get; set; } = "parked";
    [JsonPropertyName("pinned"), JsonRequired] public bool Pinned { get; set; }
    [JsonPropertyName("createdAt"), JsonRequired] public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    [JsonPropertyName("updatedAt"), JsonRequired] public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    [JsonPropertyName("snoozedUntil"), JsonRequired] public DateTimeOffset? SnoozedUntil { get; set; }
    [JsonPropertyName("completedAt"), JsonRequired] public DateTimeOffset? CompletedAt { get; set; }
    [JsonPropertyName("resumeCount"), JsonRequired] public int ResumeCount { get; set; }
    [JsonPropertyName("lastResumedAt"), JsonRequired] public DateTimeOffset? LastResumedAt { get; set; }
    [JsonPropertyName("checkpoints"), JsonRequired] public List<Checkpoint> Checkpoints { get; set; } = [];
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public class ResourceRef
{
    [JsonPropertyName("id"), JsonRequired] public string Id { get; set; } = Guid.NewGuid().ToString("N");
    [JsonPropertyName("kind"), JsonRequired] public string Kind { get; set; } = "file";
    [JsonPropertyName("target"), JsonRequired] public string Target { get; set; } = "";
    [JsonPropertyName("label"), JsonRequired] public string Label { get; set; } = "";
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public class Checkpoint
{
    [JsonPropertyName("at"), JsonRequired] public DateTimeOffset At { get; set; } = DateTimeOffset.UtcNow;
    [JsonPropertyName("text"), JsonRequired] public string Text { get; set; } = "";
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public class RelaySettings
{
    [JsonPropertyName("closeToTray"), JsonRequired] public bool CloseToTray { get; set; } = true;
    [JsonPropertyName("startMinimized"), JsonRequired] public bool StartMinimized { get; set; }
    [JsonPropertyName("alwaysOnTopStrip"), JsonRequired] public bool AlwaysOnTopStrip { get; set; } = true;
    [JsonPropertyName("focusMinutes"), JsonRequired] public int FocusMinutes { get; set; } = 25;
    [JsonPropertyName("captureHotkey"), JsonRequired] public string CaptureHotkey { get; set; } = "Ctrl+Alt+Space";
    [JsonPropertyName("resumeHotkey"), JsonRequired] public string ResumeHotkey { get; set; } = "Ctrl+Alt+R";
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public class VaultDocument
{
    [JsonPropertyName("version"), JsonRequired] public int Version { get; set; } = 1;
    [JsonPropertyName("cards"), JsonRequired] public List<RelayCard> Cards { get; set; } = [];
    [JsonPropertyName("settings"), JsonRequired] public RelaySettings Settings { get; set; } = new();
    [JsonPropertyName("updatedAt"), JsonRequired] public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    [JsonPropertyName("revision"), JsonRequired] public long Revision { get; set; }
}
