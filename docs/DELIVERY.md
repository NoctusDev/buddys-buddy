# Delivering a claimed job

Buddy's Jobs has an **Auto-deliver** action for requests claimed by your current character.
The recipient is the requester's full `Name@World`; you do not need to enter their name again.

1. Put the requested materials in your four main inventory bags. Retainers, the saddlebag, and
   company storage must be withdrawn first. Normal-quality requests use NQ materials; HQ requests
   use HQ materials. The delivery never silently substitutes HQ for NQ.
2. Claim the job, then select **Auto-deliver** on its row.
3. Choose travel:
   - Leave **Housing route** blank for Lifestream's automatic friend estate travel (requires
     Lifestream 2.6.0.12 or newer). Open your Friends List once if it has not loaded yet. Choose
     **First available**, **Private estate**, **Free company estate**, or **Apartment**. First
     available prefers a private estate, then an apartment, then an FC estate. Lifestream selects
     the destination and handles world travel when required.
   - Alternatively, create a housing address alias in Lifestream and enter only that alias
     in **Housing route**. Buddy's Buddy remembers it for this requester. It must lead to that
     person's estate, not your own home or a character-login shortcut.
4. Click **Start delivery**. The recipient should wait outside near the arrival point. If you land
   farther away, move within three yalms; this feature does not navigate inside houses or follow
   players around.
5. The recipient can use **Waiting for delivery** below, or accept/pay manually. Dropbox is not a
   dependency of Buddy's Buddy.

The runner sends up to five stacks per trade, trims the last stack to the exact remainder, and
continues across trades. It caps the run at the lesser of the job's remainder and the matching
materials in your bags when you start. It verifies the partner's home world, offered item,
quality, quantity, and zero outgoing gil before confirming. Completed trades must agree with the
observed inventory changes before being counted. The job stays claimed during the run and its
delivered amount is updated once at the end. Short deliveries use the server's existing partial
delivery behavior and reopen the remaining request.

## Waiting for delivery and automatic payment

Both players need this updated build for the fully automated flow.

1. Post a job with its **total** gil reward, for example `5,000,000 gil`. Numeric rewards are totals
   for the whole job, not per-item prices. Empty rewards mean no gil. Free-text rewards such as
   `500 each` or `a favour` cannot authorize automatic payment.
2. After another player claims your job, click **Waiting for delivery** on its row.
3. Review the exact claimant, requested units, and remaining total payment. Click **Start waiting
   and auto-pay**. Have the remaining payment in your character's gil balance.
4. The claimant uses **Auto-deliver**. Your waiting mode offers the proportional payment, accepts
   the main trade window after the sender locks its completed offer, and accepts the final trade
   confirmation. It accepts only the claimant's full `Name@World` and the requested item/quality.

Each trade is limited to **1,000,000 gil**. The sender splits materials into batches payable under
that cap. A 1,000-item job worth 5,000,000 gil uses five trades of 200 items and 1,000,000 gil.
If even one item is worth more than the cap, its first trade includes at most 1,000,000 gil and the
sender opens subsequent gil-only trades for the outstanding balance. A single item worth 5,000,000
gil therefore uses five trades. The receiver pays gil-only trades only when verified received
materials created an unpaid balance. No new materials are requested until that balance is paid.

Payment is the difference between the cumulative prorated totals before and after each batch, so
rounding carries forward: a 100-gil job for three items pays 33, 33, then 34 gil. Prior recorded
deliveries are assumed to have received their proportional share already. Review older jobs that
were paid differently before using automatic payment on their remainder.

Both sides verify actual item/gil inventory changes. Only the **sender** updates the delivered
amount on the board after the material and payment trades finish. The receiver stays armed across
trades and stops once it sees that board update, so delivery credit is never added twice.

Automatic payment opens the trade window's own gil input, confirms the exact amount, and waits for
that amount to appear in the offer before accepting. It does not use the direct inventory-manager
gil setter. The payment status shows whether the dialog is opening, entry is being confirmed, or
the trade is still awaiting the entered amount. Another already-open quantity dialog is left alone.
**Stop waiting for delivery** or `/bb jobs stop` stops receiving. An interrupted or uncertain
payment leaves a receiving record for review; it does not automatically repay after a reload.

The incoming/payment record is `jobs-receiving-pending.json` in the plugin config directory.
Review received units, paid gil, the unverified trade, and the outstanding balance against the board
before clearing its notice. Keep other auto-trade plugins disabled for the test so they cannot
change or confirm an offer while this runner is filling it.

**Stop delivery** (also `/bb jobs stop`) cancels further trades and lets the current trade settle before recording any
verified deliveries. Disabling the module, logging out, an uncertain trade result, or a lost board
reply can leave a recovery notice. `jobs-delivery-pending.json` in the plugin's config directory
records verified units and the last unverified batch. Review the actual trades and board, correct
the delivered amount if necessary, then acknowledge and clear the notice. The runner blocks new
deliveries while this record exists and does not automatically retry a possibly successful board
update.

Do not run another outgoing trade queue on the sending character at the same time; it can change
the offer or confirm it before this delivery has finished filling it.

## Travel integration

The integration uses Lifestream's `ExecuteCommand`, `IsBusy`, and `Abort` calls. The installed
2.6.0.12 assembly confirms friend commands `f <name>` (first available), `fh <name>` (private),
`ff <name>` (FC), and `fa <name>` (apartment). These newer commands are absent from the older public
main-branch source initially checked. The default delivery route now uses those commands rather
than opening a native picker and waiting for manual selection.

Because Lifestream matches friend name prefixes, Buddy's Buddy first checks the exact requester
and home world, and rejects ambiguous matching names. Use an explicit housing alias if more than
one friend shares the requester's name. Travel no longer needs a private game-function signature.

The final trade confirmation matches the localized game's prompt as plain text with whitespace
normalized. It uses the Yes button's registered event and requires our trade to be locked, rather
than waiting for both sides to lock before pressing our own confirmation.

- [Lifestream IPC source](https://github.com/NightmareXIV/Lifestream/blob/main/Lifestream/IPC/IPCProvider.cs)
- [Friends List game API](https://github.com/aers/FFXIVClientStructs/blob/main/FFXIVClientStructs/FFXIV/Client/UI/Agent/AgentFriendlist.cs)
- [Inventory context game API](https://github.com/aers/FFXIVClientStructs/blob/main/FFXIVClientStructs/FFXIV/Client/UI/Agent/AgentInventoryContext.cs)

## Verification

Run `dotnet run --project scripts/DeliveryTests/DeliveryTests.csproj` for the batch limits, exact
remainder, item quality, recipient identity, and offer checks. Build and run the existing
`scripts/verify.cs` and `scripts/verify-config.cs` checks as usual.

The friend travel and final Yes/No confirmation have been confirmed in game. The main Trade button
was corrected to use node-list position 3 instead of node ID 3. The paid/waiting path and revised
button handling still require an in-game retest. Start with a
one-unit request, check the recipient and both players' inventories, then test a partial stack and
a request spanning more than five stacks. Also test refusing a trade and pressing Stop. Confirm
that the board changes only by verified delivered units. No live trades are performed by the
offline verification scripts.
