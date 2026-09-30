using System;
using System.Collections.Generic;
using System.Linq;
using Relay.Core;
namespace Relay.Desktop.Services;
public static class ResurfacePolicy
{
    public static bool IsQuiet(DateTimeOffset localNow)=>localNow.Hour<8||localNow.Hour>=22;
    public static IReadOnlyList<RelayCard> Due(VaultDocument state,DateTimeOffset now)=>state.Cards.Where(c=>c.Status!="done"&&c.SnoozedUntil.HasValue&&c.SnoozedUntil.Value<=now).ToList();
    public static VaultDocument Acknowledge(VaultDocument state,DateTimeOffset now)
    {
        var clone=VaultStore.Clone(state);var ids=Due(state,now).Select(c=>c.Id).ToHashSet();foreach(var card in clone.Cards.Where(c=>ids.Contains(c.Id)))card.SnoozedUntil=null;return clone;
    }
}
