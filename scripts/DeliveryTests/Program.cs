using BuddysBuddy.Game;
using BuddysBuddy.Modules.BuddysJobs;

var nq = new ItemKey(5066, false);
var hq = new ItemKey(5066, true);
var other = new ItemKey(1, false);
var checks = 0;
void Check(bool value, string name)
{
    if (!value) throw new Exception(name);
    checks++;
    Console.WriteLine($"PASS {name}");
}
DeliveryStack Slot(ushort slot, int quantity, ItemKey? key = null) => new(0, slot, key ?? nq, quantity, 0);

var full = Enumerable.Range(0, 7).Select(i => Slot((ushort)i, 999)).ToList();
var batch = DeliveryPlan.Batch(full, nq, 5500);
Check(batch.Count == 5 && batch.Sum(x => x.Quantity) == 4995, "five-stack limit leaves remaining units for another trade");
var next = DeliveryPlan.Batch(full.Skip(5), nq, 5500 - batch.Sum(x => x.Quantity));
Check(next.Count == 1 && next[0].Quantity == 505, "second trade sends only the exact remainder");
batch = DeliveryPlan.Batch(new[] { Slot(0, 300), Slot(1, 999) }, nq, 500);
Check(batch.Count == 2 && batch[1].Quantity == 200, "partial stack does not overshoot the job");
Check(DeliveryPlan.Batch(full, nq, 0).Count == 0, "completed job does not offer materials");
Check(DeliveryPlan.Batch(full, nq, -1).Count == 0, "negative remainder does not offer materials");
Check(DeliveryPlan.Batch(new[] { Slot(0, 5, hq), Slot(1, 9) }, hq, 7).Sum(x => x.Quantity) == 5, "HQ request excludes NQ stacks");
Check(DeliveryPlan.Batch(new[] { Slot(0, 5, hq), Slot(1, 9) }, nq, 7).Sum(x => x.Quantity) == 7, "normal request does not silently spend HQ stacks");
Check(DeliveryPlan.Batch(new[] { Slot(0, 0), Slot(1, 8, other) }, nq, 4).Count == 0, "empty and unrelated slots are ignored");
Check(DeliveryPlan.Batch(new[] { Slot(0, int.MaxValue), Slot(1, int.MaxValue) }, nq, int.MaxValue).Count == 1, "large quantities stay within the requested total");
Check(DeliveryPlan.SameRecipient("Buddy Bearfeet@Coeurl", "Buddy Bearfeet", "Coeurl"), "exact recipient and home world match");
Check(!DeliveryPlan.SameRecipient("Buddy Bearfeet@Coeurl", "Buddy Bearfeet", "Balmung"), "same name on another world is rejected");
Check(!DeliveryPlan.SameRecipient("Buddy Bearfeet@Coeurl", "Buddy Bearfeet", null), "unknown home world is rejected");
Check(!DeliveryPlan.SameRecipient("Buddy Bearfeet@Coeurl", "Someone Else", "Coeurl"), "another character is rejected");
var offered = new Dictionary<ItemKey, int> { [nq] = 500 };
Check(DeliveryPlan.MatchesOffer(offered, 0, nq, 500), "exact planned offer can be confirmed");
Check(!DeliveryPlan.MatchesOffer(offered, 1, nq, 500), "unexpected gil blocks confirmation");
Check(!DeliveryPlan.MatchesOffer(offered, 0, nq, 499), "unexpected item quantity blocks confirmation");
Check(!DeliveryPlan.MatchesOffer(offered, 0, hq, 500), "wrong quality blocks confirmation");
offered[hq] = 1;
Check(!DeliveryPlan.MatchesOffer(offered, 0, nq, 500), "extra item blocks confirmation");
Check(!DeliveryPlan.MatchesOffer(new Dictionary<ItemKey, int>(), 0, nq, 0), "empty offer cannot be confirmed");
Check(DeliveryPlan.TryFriendCommand("Buddy Bearfeet@Coeurl", new[] { "Buddy Bearfeet" }, 0, out var command)
      && command == "f Buddy Bearfeet", "default travel delegates friend estate selection to Lifestream");
Check(DeliveryPlan.TryFriendCommand("Buddy Bearfeet@Coeurl", new[] { "Buddy Bearfeet" }, 1, out command)
      && command == "fh Buddy Bearfeet", "private estate preference uses the exact full friend name");
Check(DeliveryPlan.TryFriendCommand("Buddy Bearfeet@Coeurl", new[] { "Buddy Bearfeet" }, 2, out command)
      && command == "ff Buddy Bearfeet", "FC estate preference uses the FC friend command");
Check(DeliveryPlan.TryFriendCommand("Buddy Bearfeet@Coeurl", new[] { "Buddy Bearfeet" }, 3, out command)
      && command == "fa Buddy Bearfeet", "apartment preference uses the apartment friend command");
Check(!DeliveryPlan.TryFriendCommand("Buddy Bearfeet@Coeurl", new[] { "Buddy Bearfeet", "Buddy Bearfeet" }, 0, out _), "duplicate names across home worlds cannot choose the wrong estate");
Check(!DeliveryPlan.TryFriendCommand("Buddy Bearfeet@Coeurl", new[] { "Buddy Bearfeet", "Buddy Bearfeets" }, 0, out _), "ambiguous name prefixes cannot choose another friend");
Check(!DeliveryPlan.TryFriendCommand("Buddy Bearfeet@Coeurl", new[] { "Another Friend" }, 0, out _), "unknown friend does not produce a travel command");
Check(!DeliveryPlan.TryFriendCommand("Buddy Bearfeet,home@Coeurl", new[] { "Buddy Bearfeet,home" }, 0, out _), "command separators are rejected");
Check(DeliveryPlan.SamePrompt("Proceed with the\ntrade?", "Proceed with the trade?"), "wrapped final confirmation matches the localized prompt");
Check(!DeliveryPlan.SamePrompt("Discard this item?", "Proceed with the trade?"), "unrelated Yes/No popup is not accepted");
Check(!DeliveryPlan.SamePrompt("", ""), "missing prompt text cannot authorize a confirmation");
Check(DeliveryPlan.TryTotalGil("5,000,000 gil", out var total) && total == 5_000_000, "total reward supports commas and gil suffix");
Check(DeliveryPlan.TryTotalGil("5000000g", out total) && total == 5_000_000, "total reward supports short gil suffix");
Check(DeliveryPlan.TryTotalGil("", out total) && total == 0, "empty reward is a gift");
Check(!DeliveryPlan.TryTotalGil("500 each", out _), "per-item or ambiguous free text never authorizes automatic payment");
Check(!DeliveryPlan.TryTotalGil("-500", out _), "negative reward is rejected");
Check(!DeliveryPlan.TryTotalGil("1,000,000,000", out _), "reward above character gil capacity is rejected");
Check(DeliveryPlan.Payment(100, 3, 0, 1) == 33 && DeliveryPlan.Payment(100, 3, 1, 1) == 33
      && DeliveryPlan.Payment(100, 3, 2, 1) == 34, "rounding carries forward and full delivery pays exactly the total");
Check(DeliveryPlan.Payment(5_000_000, 1000, 200, 200) == 1_000_000, "partial delivery pays a proportional share of the total");
Check(DeliveryPlan.PaymentBatchLimit(5_000_000, 1000, 0, 1000) == 200, "five-million-gil request splits materials into million-gil batches");
Check(DeliveryPlan.PaymentBatchLimit(5_000_000, 1, 0, 1) == 1, "single expensive item is permitted with follow-up payments");
var debt = DeliveryPlan.Payment(5_000_000, 1, 0, 1);
var chunks = new List<long>();
while (debt > 0) { var chunk = Math.Min(debt, DeliveryPlan.MaxTradeGil); chunks.Add(chunk); debt -= chunk; }
Check(chunks.Count == 5 && chunks.All(x => x == 1_000_000) && chunks.Sum() == 5_000_000,
    "one five-million-gil item pays in five separate capped trades");
Check(DeliveryPlan.MatchesExchange(new Dictionary<ItemKey, int>(), 1_000_000, nq, 0, 1_000_000), "gil-only follow-up trade is accepted for the exact balance chunk");
Check(!DeliveryPlan.MatchesExchange(new Dictionary<ItemKey, int>(), 1_000_001, nq, 0, 1_000_001), "no trade can authorize more than one million gil");
Check(!DeliveryPlan.MatchesExchange(new Dictionary<ItemKey, int> { [nq] = 1 }, 1_000_000, nq, 0, 1_000_000), "unexpected items in a gil-only payment are rejected");
Check(!DeliveryPlan.MatchesExchange(new Dictionary<ItemKey, int>(), 999_999, nq, 0, 1_000_000), "short payment is rejected");
for (var q = 1; q <= 25; q++)
{
    long sum = 0;
    for (var i = 0; i < q; i++) sum += DeliveryPlan.Payment(5_000_003, q, i, 1);
    if (sum != 5_000_003) throw new Exception("Prorated totals drifted");
}
Check(true, "prorated payments remain exact across 25 different delivery splits");
Check(DeliveryPlan.PaymentBatchLimit(0, int.MaxValue, 0, int.MaxValue) == int.MaxValue, "batch search does not overflow on large quantities");
var now = new DateTime(2026, 10, 31, 0, 0, 0, DateTimeKind.Utc);
var entry = new TradeGilEntry();
Check(entry.Next(now, 0, 1_000_000, true, false) == GilEntryStep.Open, "payment opens the game's gil dialog rather than changing a backing field");
Check(entry.Next(now.AddMilliseconds(250), 0, 1_000_000, true, false) == GilEntryStep.Wait, "opening does not repeatedly fire the gil callback while the dialog loads");
Check(entry.Next(now.AddMilliseconds(500), 0, 1_000_000, true, true) == GilEntryStep.Confirm && entry.Amount == 1_000_000,
    "owned gil dialog confirms the exact capped payment");
Check(entry.Next(now.AddMilliseconds(750), 0, 1_000_000, true, false) == GilEntryStep.Wait,
    "trade acceptance waits for the entered gil to appear in the offer");
Check(entry.Next(now.AddSeconds(1), 1_000_000, 1_000_000, true, false) == GilEntryStep.Ready,
    "trade can proceed only after the offered amount matches");
entry = new TradeGilEntry();
Check(entry.Next(now, 0, 500, true, true) == GilEntryStep.Blocked, "unrelated numeric dialog is never filled as payment");
Check(entry.Next(now, 0, 500, false, false) == GilEntryStep.Wait, "unstable or locked trade cannot open a payment dialog");
Check(entry.Next(now, 0, 500, true, false) == GilEntryStep.Open
    && entry.Next(now.AddSeconds(6), 0, 500, true, false) == GilEntryStep.TimedOut,
    "missing gil dialog fails visibly rather than waiting silently");
entry = new TradeGilEntry();
entry.Next(now, 0, 500, true, false);
Check(entry.Next(now.AddMilliseconds(500), 0, 750, true, true) == GilEntryStep.Confirm && entry.Amount == 750,
    "changed incoming quantity uses the current prorated payment before submitting");
Check(entry.Next(now.AddSeconds(6), 0, 750, true, false) == GilEntryStep.TimedOut,
    "an unacknowledged payment never authorizes trade acceptance");
entry = new TradeGilEntry();
Check(entry.Next(now, 500, 0, true, false) == GilEntryStep.Open
    && entry.Next(now.AddMilliseconds(500), 500, 0, true, true) == GilEntryStep.Confirm && entry.Amount == 0,
    "payment can be cleared to zero when the offer changes");
entry = new TradeGilEntry();
Check(entry.Next(now, 0, 0, true, false) == GilEntryStep.Ready, "gift delivery needs no gil-entry dialog");
var rejected = false;
try { entry.Next(now, 0, 1_000_001, true, false); } catch (ArgumentOutOfRangeException) { rejected = true; }
Check(rejected, "gil input itself rejects amounts above one million");
Console.WriteLine($"{checks} delivery checks passed.");
