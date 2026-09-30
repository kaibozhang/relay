using System.Text.Json;
using Relay.Core;
using Relay.Desktop.Services;

// Pure policy linked from Desktop: no WPF/WinForms references, UI launch, or packages.
var tests = new (string Name, Action Run)[]
{
    ("00:00 is quiet", () => Quiet("2026-10-01T00:00:00+08:00", true)),
    ("07:59:59.9999999 is quiet", () => Quiet("2026-10-01T07:59:59.9999999+08:00", true)),
    ("08:00 begins reminder window", () => Quiet("2026-10-01T08:00:00+08:00", false)),
    ("21:59:59.9999999 remains nonquiet", () => Quiet("2026-10-01T21:59:59.9999999+08:00", false)),
    ("22:00 begins quiet hours", () => Quiet("2026-10-01T22:00:00+08:00", true)),
    ("23:59:59 is quiet", () => Quiet("2026-10-01T23:59:59+08:00", true)),
    ("Positive offset uses wall clock not UTC hour", PositiveOffset),
    ("Negative offset and previous UTC day do not alter local hour", NegativeOffset),
    ("DST spring offsets retain local quiet boundary", SpringDst),
    ("DST repeated hour remains quiet at both offsets", FallDst),
    ("Due includes exact same instant across offsets", SameInstant),
    ("Due excludes future by one tick", FutureTick),
    ("Due includes past reminders", PastDue),
    ("Due skips done even when overdue", DoneExcluded),
    ("Due skips null snooze", NullExcluded),
    ("Due includes both active and parked", ActiveAndParked),
    ("Due preserves card order and does not mutate state", DueImmutable),
    ("Empty document has no reminders", Empty),
    ("Acknowledge clears only due non-done cards", SelectiveAcknowledge),
    ("Acknowledge deeply clones cards resources settings checkpoints", DeepClone),
    ("Acknowledge is idempotent and leaves original unchanged", Idempotent),
    ("Quiet policy is separate from due detection and acknowledgement", QuietComposition),
    ("Persisted acknowledgement prevents restart reminders", Restart),
    ("Acknowledgement rotates exact prior vault into backup", Backup),
    ("Failed persist leaves reminder pending for later retry", FailedPersist),
    ("Stale acknowledgement cannot replace another writer", ConcurrentPersist),
    ("Second save keeps acknowledged state recoverable in backup", AcknowledgedRecovery),
    ("Explicit resnooze produces one new due reminder", Resnooze),
    ("Recovery of older unacknowledged backup honestly retains pending reminder", OlderBackup)
};
var failures = 0;
foreach (var test in tests)
{
    try { test.Run(); Console.WriteLine($"PASS {test.Name}"); }
    catch (Exception ex) { failures++; Console.Error.WriteLine($"FAIL {test.Name}: {ex}"); }
}
Console.WriteLine($"{tests.Length - failures}/{tests.Length} policy tests passed.");
return failures == 0 ? 0 : 1;

static DateTimeOffset Now() => DateTimeOffset.Parse("2026-10-01T12:00:00+08:00");
static DateTimeOffset At(string value) => DateTimeOffset.Parse(value);
static void Check(bool value, string message = "Assertion failed") { if (!value) throw new Exception(message); }
static void Throws<T>(Action run) where T : Exception
{ try { run(); } catch (T) { return; } throw new Exception($"Expected {typeof(T).Name}"); }
static string Json(VaultDocument doc) => JsonSerializer.Serialize(doc);
static RelayCard Card(DateTimeOffset? snooze, string status = "parked") => new()
{
    Title = "续页提醒", CreatedAt = At("2026-09-01T00:00:00+08:00"), UpdatedAt = At("2026-09-01T00:00:00+08:00"),
    SnoozedUntil = snooze, Status = status, Resources = [new ResourceRef { Kind = "url", Target = "https://example.com", Label = "resource" }],
    Checkpoints = [new Checkpoint { At = At("2026-09-02T00:00:00+08:00"), Text = "checkpoint" }]
};
static VaultDocument Doc(params RelayCard[] cards) => new() { Cards = cards.ToList() };
static void Quiet(string value, bool expected) => Check(ResurfacePolicy.IsQuiet(At(value)) == expected);
static void PositiveOffset()
{
    Quiet("2026-10-02T08:00:00+14:00", false); // UTC previous day 18:00
    Quiet("2026-10-02T00:30:00+08:00", true); // UTC previous day 16:30
    Quiet("2026-10-02T12:00:00+08:00", false); // UTC 04:00
}
static void NegativeOffset()
{
    Quiet("2026-10-01T21:00:00-12:00", false); // UTC next day 09:00
    Quiet("2026-10-01T23:00:00-04:00", true);
    Quiet("2026-10-01T08:00:00-10:00", false);
}
static void SpringDst()
{
    Quiet("2026-03-08T01:59:59-05:00", true);
    Quiet("2026-03-08T03:00:00-04:00", true);
    Quiet("2026-03-08T07:59:59-04:00", true);
    Quiet("2026-03-08T08:00:00-04:00", false);
}
static void FallDst()
{
    Quiet("2026-11-01T01:30:00-04:00", true);
    Quiet("2026-11-01T01:30:00-05:00", true);
    Quiet("2026-11-01T08:00:00-05:00", false);
}
static void SameInstant()
{
    var state = Doc(Card(Now()), Card(Now().ToOffset(TimeSpan.FromHours(-4))));
    Check(ResurfacePolicy.Due(state, Now()).Count == 2);
}
static void FutureTick() => Check(ResurfacePolicy.Due(Doc(Card(Now().AddTicks(1))), Now()).Count == 0);
static void PastDue() => Check(ResurfacePolicy.Due(Doc(Card(Now().AddTicks(-1)), Card(Now().AddDays(-100))), Now()).Count == 2);
static void DoneExcluded() => Check(ResurfacePolicy.Due(Doc(Card(Now().AddDays(-1), "done")), Now()).Count == 0);
static void NullExcluded() => Check(ResurfacePolicy.Due(Doc(Card(null)), Now()).Count == 0);
static void ActiveAndParked() => Check(ResurfacePolicy.Due(Doc(Card(Now(), "active"), Card(Now(), "parked")), Now()).Count == 2);
static void DueImmutable()
{
    var state = Doc(Card(Now()), Card(null), Card(Now().AddMinutes(-1))); var original = Json(state);
    var due = ResurfacePolicy.Due(state, Now());
    Check(due.Select(c => c.Id).SequenceEqual(new[] { state.Cards[0].Id, state.Cards[2].Id }));
    Check(Json(state) == original);
}
static void Empty()
{
    var state = Doc(); Check(ResurfacePolicy.Due(state, Now()).Count == 0);
    Check(ResurfacePolicy.Acknowledge(state, Now()).Cards.Count == 0);
}
static void SelectiveAcknowledge()
{
    var future = Now().AddTicks(1); var past = Now().AddDays(-1);
    var state = Doc(Card(Now()), Card(future), Card(past, "done"), Card(null), Card(past, "active"));
    var result = ResurfacePolicy.Acknowledge(state, Now());
    Check(result.Cards[0].SnoozedUntil is null && result.Cards[4].SnoozedUntil is null);
    Check(result.Cards[1].SnoozedUntil == future && result.Cards[2].SnoozedUntil == past && result.Cards[3].SnoozedUntil is null);
    Check(ResurfacePolicy.Due(result, Now()).Count == 0);
    for (var i = 0; i < state.Cards.Count; i++)
    {
        var expected = VaultStore.Clone(Doc(state.Cards[i]));
        expected.Cards[0].SnoozedUntil = result.Cards[i].SnoozedUntil;
        Check(JsonSerializer.Serialize(expected.Cards[0]) == JsonSerializer.Serialize(result.Cards[i]), "No unrelated card fields should change");
    }
}
static void DeepClone()
{
    var state = Doc(Card(Now())); var original = Json(state); var clone = ResurfacePolicy.Acknowledge(state, Now());
    Check(!ReferenceEquals(state, clone) && !ReferenceEquals(state.Cards, clone.Cards) && !ReferenceEquals(state.Settings, clone.Settings));
    clone.Cards[0].Title = "changed"; clone.Cards[0].Resources[0].Label = "changed";
    clone.Cards[0].Checkpoints[0].Text = "changed"; clone.Settings.FocusMinutes = 30;
    Check(Json(state) == original);
}
static void Idempotent()
{
    var state = Doc(Card(Now()), Card(Now().AddHours(1))); var original = Json(state);
    var once = ResurfacePolicy.Acknowledge(state, Now()); var twice = ResurfacePolicy.Acknowledge(once, Now());
    Check(Json(once) == Json(twice) && Json(state) == original);
}
static void QuietComposition()
{
    var local = At("2026-10-01T23:00:00+08:00"); var state = Doc(Card(local.AddMinutes(-1)));
    Check(ResurfacePolicy.IsQuiet(local)); Check(ResurfacePolicy.Due(state, local).Count == 1);
    // Caller must check quiet hours before acknowledging; policy deliberately separates responsibilities.
    Check(ResurfacePolicy.Acknowledge(state, local).Cards[0].SnoozedUntil is null);
    Check(state.Cards[0].SnoozedUntil is not null);
    Check(!ResurfacePolicy.IsQuiet(local.AddHours(9)) && ResurfacePolicy.Due(state, local.AddHours(9)).Count == 1);
}
static void WithVault(Action<VaultStore, string> run)
{
    var root = Path.Combine(AppContext.BaseDirectory, "relay-policy-tests-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(root);
    try { run(new VaultStore(root), root); }
    finally { Directory.Delete(root, true); }
}
static VaultDocument Seed(VaultStore store)
{
    var state = store.Load(); state.Cards.Add(Card(Now().AddMinutes(-1))); state.Cards.Add(Card(Now().AddHours(1)));
    store.Save(state); return state;
}
static void Restart() => WithVault((store, root) =>
{
    var state = Seed(store); var acknowledged = ResurfacePolicy.Acknowledge(state, Now()); store.Save(acknowledged);
    for (var i = 0; i < 5; i++)
    {
        var restarted = new VaultStore(root).Load();
        Check(ResurfacePolicy.Due(restarted, Now()).Count == 0);
        Check(restarted.Cards[1].SnoozedUntil == Now().AddHours(1));
    }
});
static void Backup() => WithVault((store, _) =>
{
    var state = Seed(store); var original = File.ReadAllBytes(store.DataPath);
    var acknowledged = ResurfacePolicy.Acknowledge(state, Now()); store.Save(acknowledged);
    Check(original.SequenceEqual(File.ReadAllBytes(store.DataPath + ".bak")));
    Check(ResurfacePolicy.Due(store.Import(store.DataPath + ".bak"), Now()).Count == 1);
    Check(acknowledged.Revision == 2 && state.Revision == 1);
});
static void FailedPersist() => WithVault((store, root) =>
{
    var state = Seed(store); var original = Json(state); var acknowledged = ResurfacePolicy.Acknowledge(state, Now());
    using (var locked = new FileStream(store.DataPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        Throws<IOException>(() => store.Save(acknowledged));
    Check(Json(state) == original && acknowledged.Revision == 1);
    Check(ResurfacePolicy.Due(new VaultStore(root).Load(), Now()).Count == 1);
    store.Save(acknowledged); Check(ResurfacePolicy.Due(new VaultStore(root).Load(), Now()).Count == 0);
});
static void ConcurrentPersist() => WithVault((store, root) =>
{
    var state = Seed(store); var acknowledged = ResurfacePolicy.Acknowledge(state, Now());
    var other = new VaultStore(root); var newer = other.Load(); newer.Cards[0].Title = "newer"; other.Save(newer);
    Throws<InvalidOperationException>(() => store.Save(acknowledged));
    var disk = new VaultStore(root).Load(); Check(disk.Cards[0].Title == "newer" && ResurfacePolicy.Due(disk, Now()).Count == 1);
});
static void AcknowledgedRecovery() => WithVault((store, root) =>
{
    var state = ResurfacePolicy.Acknowledge(Seed(store), Now()); store.Save(state); store.Save(state);
    File.WriteAllText(store.DataPath, "corrupt main"); var recoveredStore = new VaultStore(root); var recovered = recoveredStore.Load();
    Check(recoveredStore.RecoveryNotice is not null && ResurfacePolicy.Due(recovered, Now()).Count == 0);
    Check(recovered.Cards[1].SnoozedUntil == Now().AddHours(1));
});
static void Resnooze() => WithVault((store, root) =>
{
    var state = ResurfacePolicy.Acknowledge(Seed(store), Now()); store.Save(state);
    state.Cards[0].SnoozedUntil = Now().AddMinutes(30); store.Save(state);
    var disk = new VaultStore(root).Load(); Check(ResurfacePolicy.Due(disk, Now()).Count == 0);
    Check(ResurfacePolicy.Due(disk, Now().AddMinutes(30)).Count == 1);
    var ack = ResurfacePolicy.Acknowledge(disk, Now().AddMinutes(30)); Check(ResurfacePolicy.Due(ack, Now().AddMinutes(30)).Count == 0);
});
static void OlderBackup() => WithVault((store, root) =>
{
    var state = ResurfacePolicy.Acknowledge(Seed(store), Now()); store.Save(state);
    File.WriteAllText(store.DataPath, "corrupt main"); var recoveredStore = new VaultStore(root); var recovered = recoveredStore.Load();
    // Corruption recovery intentionally rolls back to last good state; old backup may predate acknowledgement.
    Check(recoveredStore.RecoveryNotice is not null && recovered.Revision == 1);
    Check(ResurfacePolicy.Due(recovered, Now()).Count == 1);
});
