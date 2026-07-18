# Squire Outfitter M8 expanded product-policy review

Source task: `019f64f7-37e3-7b13-adfe-38ac35c188b1`

Status: Review only. No policy choice is approved by this document.

## Full task response
# Review Artifact: Squire Outfitter M8 Execution-Authority Policy

**Audience:** Fran  
**Status:** Product-policy review required  
**Implementation status:** Paused. No policy below is approved or implemented.  
**Decision rule:** Silence means no approval. M8 remains read-only until Fran explicitly answers the questions at the end.

---

## 1. What M8 is deciding

M7 can observe the player through automated UI, construct exact NQ/HQ equipment offers, score supported loadouts, display the cost/utility Pareto frontier, nominate one solution, and let the user select another frontier solution.

M8 governs what happens after that selection. It must answer:

1. How much market-price movement the user authorizes.
2. Which changed listings Squire may recover from automatically.
3. Which Workbench edits still describe the selected solution.
4. How current the evidence must be when Route spends gil.

These are execution-authority decisions, not recommendation-model decisions. The Advisor should continue choosing and comparing gear by utility, acquisition cost, burden, and evidence quality. A price envelope controls procurement drift after the user chooses a solution; it must not become a hard budget that silently prevents the Advisor from showing useful alternatives.

---

## 2. Fixed workflow and terminology

These definitions are facts about the intended workflow unless marked otherwise.

### Product surfaces

- **Advisor:** The read-only Squire surface that observes the target, evaluates supported loadouts, displays the cost/utility frontier, and explains whether it can nominate a solution or must abstain.
- **Advisor nomination:** The solution selected by Squire’s advisory rule. It is visually distinct from the user’s selection and carries no purchase authority by itself.
- **Selected solution:** The Pareto-frontier solution the user explicitly chooses. It may differ from the Advisor nomination.
- **Inbox:** Durable incoming Market Acquisition work from external or remote sources. It is not an Outfitter editor.
- **Workbench:** The sole editable local composition surface. Squire may place exact-quality acquisition lines into it, but must not create a second request form, staging pane, or competing manifest editor.
- **Finalized Workbench revision:** A specific version of the Workbench that the user has reviewed and confirmed. Later edits produce another revision.
- **Route:** The execution surface that travels, revalidates live market rows through UI automation, and purchases the finalized Workbench. Route must not reinterpret an unfinalized draft.

### Recommendation terms

- **JobUtilityScore:** A profile- and context-local score used to order supported loadouts. It is not a proof of optimal play and does not itself authorize execution.
- **Utility context:** The scenario in which stats are evaluated, such as ordinary MIN/BTN resource nodes. Utility values from materially different contexts are not directly interchangeable.
- **Pareto frontier:** Solutions for which no other observed solution is at least as good on all relevant dimensions and better on one. A frontier point may cost more but provide more utility, or cost less with a capability tradeoff.
- **Authority assessment:** A separate determination that says whether Squire has enough evidence and confidence to recommend. A high score does not override abstention.
- **Adjacent solution:** A nearby Pareto solution showing what the user gains or gives up by changing cost, utility, acquisition burden, or evidence risk.

### Acquisition terms

- **Offer:** A stable acquisition choice identified by item, exact NQ/HQ quality, source kind, and source catalog identity.
- **Market listing or observable row:** One concrete market-board row with item, exact quality, quantity, unit price, world, and observable row identity.
- **Exact-quality identity:** NQ and HQ are different offers. Squire-originated work must never use `Either`.
- **Market lot:** The portion of the selected solution assigned to one observed market row, including required quantity, observed available quantity, world, price, and evidence identity.
- **Line cap:** A guardrail applied to one Workbench line, such as maximum unit price or maximum gil for that line.
- **Plan-wide cap:** A maximum total acquisition cost for the finalized Workbench or for its Squire-owned subset.
- **Approval envelope:** The complete set of spending and substitution constraints the user confirms. It may contain line caps, a plan-wide cap, route restrictions, or other limits.
- **Listing substitution:** Replacing an unavailable or changed row with another row for the same item and exact NQ/HQ quality.
- **Route replanning:** Recalculating which listings and worlds should fulfill the unchanged Workbench lines. It changes procurement, not the selected gear.
- **Advisor-level replanning:** Selecting different equipment, quality, or quantity and therefore changing the loadout’s utility. This returns to the Advisor unless explicitly authorized otherwise.
- **Live revalidation:** Immediately before purchase, Route reads the visible market-board UI and proves that the row’s item, NQ/HQ quality, quantity, price, and world still satisfy the finalized authority.
- **Confirmation lease:** A rule that makes approval expire after time or evidence changes.
- **Return to Advisor:** Stop acquisition and reopen or flag the Advisor because satisfying the Workbench would require changing the selected gear solution.

### Evidence and lineage terms

- **Evidence generation:** One coherent market-discovery run with a source, region, scope, completion status, and publication time.
- **Evidence revision:** A version within that evidence lineage.
- **Evidence scope:** The source, region, discovery mode, and queried items. A refresh cannot prove a listing disappeared if it did not actually query that item.
- **Lineage:** The recorded relationship among the selected solution, its utility profile/context, every selected slot/allocation, exact market rows, and the evidence generation that produced them.
- **Lineage invalidation:** Declaring that the current Workbench no longer represents the selected Advisor solution.
- **Gear lineage:** Proof of which selected equipment solution the market lines serve.
- **Procurement authority:** Permission to purchase that equipment within the finalized envelope. Gear lineage may remain valid while procurement authority requires reconfirmation.

### Current implementation boundary

M8 currently has policy-neutral primitives that can:

- Translate a user-selected authoritative frontier solution into an immutable, non-persisted handoff.
- Preserve every selected slot and acquisition allocation, including owned and vendor choices.
- Preserve exact NQ/HQ market-row and evidence lineage.
- Reject no-market handoffs, mismatched evidence, unavailable quantities, and solutions outside the frontier.
- Classify listing disappearance, price movement, quantity movement, quality changes, world changes, and evidence changes.
- Enumerate alternative rows for the same item and exact quality, including partial rows, same-world status, and signed cost differences.

It currently cannot:

- Persist that lineage into Workbench.
- Populate price caps.
- Finalize an approval envelope.
- Decide that a changed row is acceptable.
- Select a replacement row.
- Replan Route autonomously.
- Authorize a purchase.

---

# 3. Decision One: Approval Envelope

## 3.1 What this controls

The selected solution has an observed acquisition cost, but market listings can change between discovery, Workbench review, travel, and purchase.

The approval envelope defines the maximum procurement drift the user accepts without another confirmation.

This is not a recommendation budget. The Advisor may still display a superior two-million-gil solution beside a weaker 200,000-gil solution. The envelope begins only after the user selects one.

## 3.2 Invariant and failure modes

**Invariant:** Route must never spend outside the authority the user knowingly confirmed.

Failure modes include:

- Treating the observed price as permission to spend an arbitrary higher amount.
- Letting several individually small increases create a large total overrun.
- Using only a plan-wide cap and allowing one absurdly overpriced line to consume most of it.
- Using only line caps and allowing aggregate drift across many lines to exceed the user’s intended total.
- Forcing reconfirmation for every one-gil change, making automation functionally useless.
- Hiding a hard budget inside recommendation logic and suppressing decision-relevant Pareto alternatives.

## 3.3 Scenarios

### Scenario A: Small ordinary drift

Fran selects a five-item gathering set observed at 420,000 gil. One HQ ring was 70,000 gil and is now 73,000 gil. Another item became 5,000 gil cheaper. The route total is now 418,000 gil.

The meaningful question is whether a harmless row-level increase should stop the route even though the complete plan became cheaper.

### Scenario B: Adverse aggregate drift

Fran selects a 1,200,000-gil set. During travel:

- Three listings rise by 30,000 gil each.
- A fourth listing disappears and the cheapest exact-quality replacement costs 110,000 gil more.
- The final route would cost 1,400,000 gil.

Each individual change might look tolerable, but the combined increase is material.

### Scenario C: Concentrated outlier

A four-line plan is approved at 600,000 gil. Three lines remain unchanged. A low-value accessory originally costing 20,000 gil now has only a 150,000-gil replacement. The total still fits a generous 750,000-gil plan cap, but the accessory is now grotesquely overpriced relative to the chosen solution.

## 3.4 Credible options

### Option A — Exact observed-price lock

Route may buy only at or below each accepted row’s observed unit price. Any upward movement stops for review.

**User-visible behavior:** Confirmation is simple: “Buy this exact plan for no more than the observed prices.” Cheaper rows proceed; any increase pauses.

**Operational consequences:** Existing observed prices can become strict per-line caps. A plan-wide cap adds little because no line may rise.

**Tradeoffs:**

- **Safety:** Very high.
- **Annoyance:** High in active markets; trivial changes interrupt Route.
- **Automation:** Low.
- **Reversibility:** Easy to loosen later, but users may come to expect that confirmation means zero upward drift.

**Interactions:** Recovery can automatically use only equally priced or cheaper exact-quality rows. Freshness matters more because older plans stop frequently.

---

### Option B — Per-line caps only

Each line has a maximum unit price and/or line gil cap. Route may substitute listings while every line remains within its own cap.

**User-visible behavior:** Each Workbench row shows its exact quality, expected price, and maximum accepted price. The total maximum is derived from the lines but is not independently authoritative.

**Operational consequences:** Existing Workbench line fields align with this model. Recovery is straightforward per item.

**Tradeoffs:**

- **Safety:** Good against individual outliers.
- **Annoyance:** Moderate.
- **Automation:** Good.
- **Reversibility:** Easy to add a plan cap later, but existing saved confirmations would need a migration default.
- **Weakness:** Many lines can rise to their caps simultaneously, producing an aggregate total the user never intended.

**Interactions:** Line edits directly change authority and require a new Workbench revision. Cross-world replanning remains possible if every line cap holds.

---

### Option C — Plan-wide cap only

The user approves only a maximum total. Route may redistribute cost among lines as long as the route stays below that total.

**User-visible behavior:** The Workbench emphasizes one number: expected total versus maximum total. Individual rows show current estimates but no authoritative caps.

**Operational consequences:** Recovery has maximum freedom to trade one higher line against another cheaper line. Route must continuously track committed and remaining gil authority.

**Tradeoffs:**

- **Safety:** Good against total overspend, weak against a single terrible purchase.
- **Annoyance:** Low.
- **Automation:** High.
- **Reversibility:** Adding line caps later changes the meaning of existing confirmations.
- **Weakness:** A wildly overpriced accessory could be purchased because savings elsewhere preserve the total.

**Interactions:** Route-wide replanning works naturally. Partial purchases complicate the remaining envelope because spent gil becomes irreversible.

---

### Option D — Layered line and plan-wide caps

Every line has a cap, and the complete finalized plan also has a maximum total. A purchase is authorized only if both remain satisfied.

**User-visible behavior:** The common path can show expected total and maximum total prominently, with per-line caps inline or behind expansion. The user sees both aggregate exposure and individual outliers.

**Operational consequences:** Route must track:

- Each line’s remaining quantity and remaining line authority.
- Gil already spent.
- The projected cost of all remaining lines.
- The plan-wide maximum.
- Whether a recovery proposal satisfies every constraint.

**Tradeoffs:**

- **Safety:** Highest practical balance.
- **Annoyance:** Moderate if defaults are sensible.
- **Automation:** High within the envelope.
- **Reversibility:** Individual default formulas can change later, but the persisted meaning of both caps must remain stable.
- **Complexity:** Higher than either cap alone, but the rules are explainable.

**Interactions:** This supports autonomous exact-quality recovery while bounding both local and aggregate drift. Cap edits preserve gear lineage but change procurement authority.

---

### Option E — Relative tolerance only

The user approves a percentage or absolute increase from the observed price, such as “up to 10%” or “up to 100,000 gil more.”

This may apply per line, plan-wide, or both.

**User-visible behavior:** The user thinks in drift rather than final totals. The UI shows expected cost and allowed increase.

**Operational consequences:** The system derives authoritative absolute caps when the Workbench is finalized. Persisting only the relative rule is dangerous because later refreshes could move the baseline and silently compound authority.

**Tradeoffs:**

- **Safety:** Depends on the baseline and formula.
- **Annoyance:** Low.
- **Automation:** High.
- **Reversibility:** Display can change easily, but the derived absolute maximum must be persisted.
- **Weakness:** Percentages behave poorly across cheap and expensive items; 20% of 5,000 gil is trivial, while 20% of 2,000,000 gil is substantial.

**Interactions:** Relative input can be a convenient editor for Option B or D, but should derive fixed absolute authority at confirmation.

---

### Option F — Confirm every changed plan

No reusable upward-drift envelope exists. Any price increase, row substitution, or route change produces a revised plan requiring explicit confirmation.

**User-visible behavior:** Route pauses and presents old versus new cost, worlds, and rows.

**Operational consequences:** Safe but highly interactive. The plugin must retain resumable route state.

**Tradeoffs:**

- **Safety:** Very high.
- **Annoyance:** Very high.
- **Automation:** Low.
- **Reversibility:** Easy to add bounded autonomy later.
- **Benefit:** Useful as an initial rollout mode or diagnostic fallback.

**Interactions:** Recovery classification still adds value because it explains the pause. Freshness determines how often confirmation repeats.

---

### Option G — No price envelope

Once a gear solution is selected, Route buys the required exact-quality items at whatever live price is available.

**User-visible behavior:** Minimal interruption and no meaningful spending boundary.

**Operational consequences:** Simple automation but unacceptable exposure to sparse, manipulated, or erroneous listings.

**Tradeoffs:**

- **Safety:** Poor.
- **Annoyance:** Lowest.
- **Automation:** Maximum.
- **Reversibility:** Technically easy to restrict later, but user trust damage is not reversible.

**Interactions:** This makes recovery nearly unconstrained and turns live revalidation into identity checking rather than spending protection.

## 3.5 Recommendation

**Recommendation: Option D, with absolute caps as authority and relative values as editing aids.**

The finalized Workbench should persist:

- An absolute maximum total for the Squire acquisition plan.
- An absolute maximum unit price or line total for every exact-quality line.
- The observed expected cost used to explain the difference.
- Any percentage or “extra gil” value only as a user-facing way to derive those fixed caps.

The default should be visible before confirmation and adjustable. It should not constrain which Pareto solutions the Advisor displays.

This gives recovery enough room to be useful while preventing both aggregate overruns and isolated absurd purchases. It also makes confirmation intelligible: “I selected this gear and authorize these exact-quality purchases up to these fixed limits.”

---

# 4. Decision Two: Recovery Authority

## 4.1 What this controls

Recovery authority determines what Route may do when the accepted row changes or disappears.

There are two fundamentally different kinds of change:

- **Procurement-only change:** Same item, same NQ/HQ quality, same required quantity; only listings, prices, quantities, worlds, or visit order change.
- **Gear-solution change:** Different item, quality, or required quantity; this can change stats, utility, thresholds, and the Pareto frontier.

The current M8 primitives can identify the first kind and enumerate exact-quality replacement rows. They do not approve or select them.

## 4.2 Invariant and failure modes

**Invariant:** Route may not silently turn procurement recovery into a new equipment decision.

Failure modes include:

- Buying NQ because the selected HQ listing vanished.
- Buying a different item because it appears “similar.”
- Replanning one line without checking whether the complete remaining route still fits the envelope.
- Refusing a safe same-item substitution and turning every market change into user labor.
- Considering only one replacement row when several partial rows could satisfy the quantity.
- Reusing an old route after earlier purchases changed the remaining cost and world sequence.

## 4.3 Scenarios

### Scenario A: Safe exact-quality substitution

The selected solution requires one HQ gathering body piece. The accepted Siren listing at 240,000 gil disappears. A different Siren retainer lists the same HQ item for 238,000 gil.

No gear decision changes. Only the row identity changes.

### Scenario B: Cross-world route repair

The accepted row on Siren disappears. Two exact HQ rows exist:

- One on Gilgamesh for 245,000 gil.
- One on Cactuar for 250,000 gil.

The route was already going to Gilgamesh for another item. Selecting the Gilgamesh row reduces route burden even though its price is slightly higher.

### Scenario C: Adverse partial availability

The selected solution needs two identical HQ rings. The accepted quantity-two listing is gone. Current evidence contains:

- One HQ ring on Siren at 80,000 gil.
- One HQ ring on Gilgamesh at 82,000 gil.
- A quantity-two NQ listing at 40,000 gil each.

A correct recovery planner may combine the two HQ rows. It must not substitute NQ.

### Scenario D: Gear identity failure

The selected HQ tool is completely unavailable. Another tool is marketable, slightly cheaper, and appears to have similar gathering stats. Choosing it would alter the selected loadout and could affect thresholds or utility.

This is not Route recovery. It is a return to Advisor.

## 4.4 Credible options

### Option A — No autonomous recovery

Any changed or missing accepted row pauses Route.

**User-visible behavior:** The user reviews every difference and explicitly accepts a replacement or returns to Workbench.

**Operational consequences:** The system needs resumable state and a clear changed-plan diff. Recovery enumeration is advisory only.

**Tradeoffs:**

- **Safety:** Highest.
- **Annoyance:** High.
- **Automation:** Low.
- **Reversibility:** Easy to add automation later.

**Interactions:** Works with any envelope, including no upward drift. Freshness changes cause frequent pauses.

---

### Option B — Exact row only

Route may proceed only if the originally accepted observable row still exists and satisfies its caps. It does not substitute another row automatically.

**User-visible behavior:** A price or quantity change on the same row may proceed if allowed; disappearance always pauses.

**Operational consequences:** Less complex than substitution, but listing IDs may change after partial purchases or normal market updates.

**Tradeoffs:**

- **Safety:** Very high.
- **Annoyance:** High.
- **Automation:** Low to moderate.
- **Reversibility:** Easy to broaden.

**Interactions:** Strongly depends on row-identity stability. It underuses the exact-quality alternative evidence already available.

---

### Option C — Same-world, same-item, exact-quality substitution

Route may select another row on the current world for the same item and quality, provided the envelope remains satisfied.

**User-visible behavior:** Small local market changes repair quietly. Cross-world alternatives require confirmation.

**Operational consequences:** Route may combine multiple same-world rows if no single row fulfills the quantity. It must recompute line and plan costs after each purchase.

**Tradeoffs:**

- **Safety:** High.
- **Annoyance:** Moderate.
- **Automation:** Good within one world.
- **Reversibility:** Straightforward to add cross-world recovery.
- **Weakness:** It may reject an obviously superior row on a world already present later in the route.

**Interactions:** Requires line and plan envelopes. World restrictions remain authoritative.

---

### Option D — Cross-world procurement replanning for unchanged gear

Route may substitute any observed row for the same item and exact quality, combine partial rows, and reorder remaining world visits, provided all route and price constraints remain satisfied.

**User-visible behavior:** Healthy recovery stays quiet. The Route surface updates the remaining worlds and projected cost. It surfaces only a material pause or failed recovery.

**Operational consequences:** After any row change or purchase, Route may need to recalculate:

- Remaining quantities.
- Remaining line and total authority.
- Candidate exact-quality rows.
- World grouping and visit order.
- Already completed worlds and purchases.
- Whether revisiting a world is worthwhile or permitted.

**Tradeoffs:**

- **Safety:** High if the envelope and identity invariants are strict.
- **Annoyance:** Low.
- **Automation:** High.
- **Reversibility:** Can be restricted later, but users may learn to expect uninterrupted recovery.
- **Complexity:** Substantial; this is genuine route recovery, not a listing swap stapled onto the current engine.

**Interactions:** Best paired with layered caps and live revalidation. Gear lineage remains unchanged.

---

### Option E — Present recovery proposal, require one confirmation

The system autonomously constructs the best exact-quality revised route but does not execute it until the user accepts the diff.

**User-visible behavior:** “The original route changed: +18,000 gil, one additional world, same gear and quality.” The user accepts or returns to Workbench.

**Operational consequences:** Much of Option D’s planning complexity is required, but execution authority remains manual.

**Tradeoffs:**

- **Safety:** Very high.
- **Annoyance:** Lower than manual row-by-row recovery.
- **Automation:** Moderate.
- **Reversibility:** Easy to add auto-accept within an envelope later.
- **Benefit:** Strong staged-rollout option.

**Interactions:** The approval envelope can determine when no prompt is needed or merely annotate the proposal.

---

### Option F — Different gear allowed within a utility floor

Route may choose a different item or quality if the revised solution stays above a user-approved utility threshold and within the cost envelope.

**User-visible behavior:** Confirmation would need to explain not just spending authority but a minimum utility/capability boundary and acceptable slot changes.

**Operational consequences:** Route becomes an Advisor. It must rerun the frontier against current evidence, reassess authority, preserve thresholds, and explain a new solution while already mid-execution.

**Tradeoffs:**

- **Safety:** Medium at best.
- **Annoyance:** Low when it works, severe when behavior surprises.
- **Automation:** Very high.
- **Reversibility:** Difficult because persisted approvals must encode utility semantics and profile versions.
- **Risk:** A “small” score change could cross an important threshold or depend on context.

**Interactions:** Lineage and confirmation become much more complex. Profile-version migration becomes execution-critical.

---

### Option G — Fully autonomous advisor reselection

If procurement fails, Squire selects any currently preferred Pareto solution and continues.

**User-visible behavior:** The purchased gear can differ materially from what the user selected.

**Operational consequences:** Requires complete re-solving, confirmation delegation, handling already purchased pieces, and portfolio-aware sunk-cost decisions.

**Tradeoffs:**

- **Safety:** Poor for M8.
- **Annoyance:** Lowest until the result is unwanted.
- **Automation:** Maximum.
- **Reversibility:** Very costly in both schema and user expectation.
- **Scope problem:** This pulls M10-style portfolio allocation and sunk-cost reasoning into M8.

## 4.5 Recommendation

**Recommendation: Option D, but only for unchanged item, exact quality, and required quantity, within the finalized envelopes.**

Route may:

- Replace a missing row with another exact-quality row.
- Combine partial exact-quality rows.
- Reorder remaining world visits.
- Use another allowed world.
- Proceed automatically only if every line cap, the plan-wide cap, and world restriction still hold.

Route must return to Advisor when recovery requires:

- A different item.
- NQ instead of HQ or HQ instead of NQ.
- A different required quantity.
- A utility-context or profile change.
- An unsupported source.
- A plan that cannot satisfy the finalized envelope.

This boundary is ambitious but clean: Route may optimize procurement; only Advisor may change the gear decision.

---

# 5. Decision Three: Lineage Invalidation

## 5.1 What this controls

Lineage answers, “Does this Workbench still represent the solution Fran selected in the Advisor?”

Workbench is intentionally editable. Some edits alter only spending or travel authority. Other edits destroy the relationship to the selected loadout.

A single Workbench may also contain unrelated manually added lines, so invalidation should not automatically erase all useful provenance.

## 5.2 Invariant and failure modes

**Invariant:** Squire provenance must never claim that an edited Workbench represents a selected solution when it no longer does.

Failure modes include:

- Changing an HQ line to NQ while retaining an “Advisor selected” badge.
- Deleting one of two required rings but preserving complete-solution lineage.
- Editing a cap and unnecessarily forcing a full Advisor recomputation.
- Adding an unrelated consumable line and invalidating otherwise valid gear lineage.
- Keeping lineage as historical decoration after edits, allowing Route or UI to mistake it for current authority.
- Automatically rerunning Advisor and replacing user edits without a clear ownership transition.

## 5.3 Scenarios

### Scenario A: Procurement-only edit

Fran selects an HQ gathering set. In Workbench, they raise one line cap from 100,000 to 110,000 gil and restrict travel from region-wide to one data center.

The selected items, exact qualities, and quantities are unchanged. Gear lineage remains valid, but the confirmed procurement authority does not.

### Scenario B: Added unrelated work

Fran adds 50 raid food manually using the Workbench’s inline blank row. The Squire gear lines remain unchanged.

The Workbench composition changed, but the food was never part of the selected equipment solution.

### Scenario C: Adverse gear edit

Fran changes an HQ ring line to NQ because it is cheaper. That changes the offer identity and may change stats or thresholds.

The Workbench must no longer claim to be the selected Advisor solution.

### Scenario D: Incomplete selected solution

Fran deletes one of two ring quantities or removes the selected tool line. The remaining lines are still individually traceable to the old solution, but the Workbench no longer represents the complete selected loadout.

## 5.4 Credible options

### Option A — Any Workbench edit invalidates all Squire lineage

**User-visible behavior:** Once anything changes, the Workbench becomes an ordinary manual composition. The user must return to Advisor to restore lineage.

**Operational consequences:** Simple and safe. No field classification is required.

**Tradeoffs:**

- **Safety:** High.
- **Annoyance:** High.
- **Automation:** Low.
- **Reversibility:** Easy to make more precise later.
- **Weakness:** Cap, route, or unrelated-line edits destroy useful provenance unnecessarily.

**Interactions:** Every envelope adjustment requires Advisor round-tripping, conflating gear choice with spending authority.

---

### Option B — Semantic invalidation by edit category

Edits are divided into:

- **Gear-semantic edits:** Item, NQ/HQ quality, quantity, slot/allocation completeness, or Squire-line removal. These invalidate selected-solution lineage.
- **Procurement edits:** Caps, allowed worlds, sweep scope, and route preferences. These preserve gear lineage but invalidate confirmation and create a new Workbench revision.
- **Unrelated manual lines:** These preserve Squire lineage for the Squire subset while remaining independently owned.

**User-visible behavior:** The UI can show:

- “Selected Squire solution intact; confirmation required.”
- “Modified from Squire recommendation; return to Advisor to restore.”
- “Contains Squire gear plus manual lines.”

**Operational consequences:** Provenance must be attached to the Squire-owned subset, not merely the whole Workbench document. Finalization must define whether the plan-wide cap covers only Squire lines or the complete composition.

**Tradeoffs:**

- **Safety:** High.
- **Annoyance:** Low to moderate.
- **Automation:** High.
- **Reversibility:** Good if metadata is designed explicitly.
- **Complexity:** Requires careful field ownership and invalidation tests.

**Interactions:** Aligns naturally with layered envelopes and exact-gear recovery.

---

### Option C — Per-line provenance only

Each Workbench line retains its origin and selected-solution identity independently. The complete solution itself has no authoritative intact/broken state.

**User-visible behavior:** Individual rows can say “From Squire,” even if the overall gear set is incomplete.

**Operational consequences:** Flexible for mixed compositions but insufficient to prove that all required slots and quantities remain present.

**Tradeoffs:**

- **Safety:** Medium.
- **Annoyance:** Low.
- **Automation:** High.
- **Reversibility:** Adding solution-level integrity later requires migration.
- **Weakness:** A collection of valid historical line origins can masquerade as a valid complete loadout.

**Interactions:** Route can safely buy individual lines, but the product cannot honestly say it is executing the selected solution.

---

### Option D — Preserve lineage as historical provenance but mark divergence

Edits never delete the original lineage. Instead, the Workbench records both the accepted solution and a structural diff from it.

**User-visible behavior:** The UI shows “Based on solution X; modified in three places.”

**Operational consequences:** This is excellent audit data, but current authority must be separate. Route must never interpret historical lineage as approval.

**Tradeoffs:**

- **Safety:** High only if current validity and historical origin are impossible to confuse.
- **Annoyance:** Low.
- **Automation:** High.
- **Reversibility:** Good.
- **Complexity:** More persisted state and more UI states.

**Interactions:** Works well as an extension of Option B, not as a replacement for invalidation.

---

### Option E — Automatically rerun Advisor after gear-semantic edits

Changing an item, quality, or quantity triggers a constrained re-solve and creates a new selected solution automatically.

**User-visible behavior:** Workbench edits immediately update utility, frontier position, and acquisition plan.

**Operational consequences:** The Workbench becomes a second loadout editor and duplicates the Advisor’s representation. It must handle abstention and unsupported edits inline.

**Tradeoffs:**

- **Safety:** Potentially high if explicit, but easy to blur authority.
- **Annoyance:** Low for expert users, confusing for ordinary use.
- **Automation:** High.
- **Reversibility:** Costly once users expect direct Workbench gear design.
- **UX conflict:** Violates the “one concept, one representation” direction unless the Advisor and Workbench become one unified surface.

**Interactions:** Expands M8 beyond acquisition into constrained recommendation editing.

---

### Option F — Never invalidate lineage

The Workbench retains its Squire identity regardless of edits.

**User-visible behavior:** Minimal friction but misleading provenance.

**Operational consequences:** Simplest implementation and weakest correctness.

**Tradeoffs:**

- **Safety:** Poor.
- **Annoyance:** Low.
- **Automation:** High.
- **Reversibility:** Technically possible, but existing persisted documents become ambiguous.

## 5.5 Recommendation

**Recommendation: Option B, supplemented by Option D for audit history.**

Specifically:

- Item, NQ/HQ quality, required quantity, or removal of a Squire-required line invalidates current selected-solution integrity.
- Cap, allowed-world, or route-preference edits preserve gear lineage but invalidate confirmation.
- Adding unrelated manual lines preserves lineage for the Squire subset.
- The Workbench may retain the original lineage and a structural diff for explanation, but Route must use an explicit current-validity state.
- A gear-semantic edit should offer “Return to Advisor with these constraints,” not silently create a second recommendation editor.

An unresolved detail must be decided: whether the plan-wide cap governs only the Squire subset or every line in the finalized Workbench. The cleaner first implementation is to govern the Squire acquisition subset and show unrelated lines separately, because the selected solution’s observed cost does not include manually added work.

---

# 6. Decision Four: Freshness and Confirmation

## 6.1 What this controls

Market evidence ages continuously. A timer can indicate that evidence is old, but only the live market-board UI can authorize a purchase.

This decision determines whether confirmation expires merely because time passed, because the evidence generation changed, because the Workbench changed, or only when live revalidation finds an operational difference.

## 6.2 Invariant and failure modes

**Invariant:** Every purchase must be authorized against the finalized Workbench and revalidated through the visible game UI immediately before spending gil.

Failure modes include:

- Treating aggregator evidence as purchase authority.
- Trusting a row because confirmation is only four minutes old.
- Expiring confirmation every five minutes even when live UI proves the row is unchanged.
- Allowing Workbench edits without reconfirmation.
- Requiring a full user confirmation after every routine evidence refresh even when procurement remains inside the envelope.
- Refreshing only once at route start and ignoring changes during a long multi-world route.

## 6.3 Scenarios

### Scenario A: Slow but unchanged market

Fran confirms a plan, then spends twelve minutes traveling and handling another task. The accepted row remains visible at the same price when Route reaches it.

A five-minute lease would interrupt for no safety gain. Live UI revalidation can prove the purchase remains valid.

### Scenario B: Fast adverse change

Fran confirms a plan and begins Route immediately. Thirty seconds later, another buyer purchases the accepted row. The next listing is 80% more expensive.

A five-minute lease would still be “fresh,” but live revalidation must reject the missing row and invoke recovery.

### Scenario C: Long route with partial completion

Route buys two of five items, then travels across data centers. The remaining listings change. Gil already spent is irreversible, so recovery must use the remaining envelope and quantities, not the original total as though nothing was purchased.

### Scenario D: Evidence refresh without operational change

A new complete evidence generation is published. Listing IDs or source revisions change, but live exact-quality rows remain inside every approved cap.

The system must decide whether a generation change alone invalidates confirmation.

## 6.4 Credible options

### Option A — Fixed confirmation lease

Confirmation expires after a fixed duration such as five minutes.

**User-visible behavior:** A countdown or “Review again” state appears when time expires.

**Operational consequences:** Simple to reason about and persist. Route must pause or obtain a fresh confirmation after expiry.

**Tradeoffs:**

- **Safety:** Superficially high but incomplete; rapid changes still occur inside the lease.
- **Annoyance:** High on long routes.
- **Automation:** Low to moderate.
- **Reversibility:** Duration is easy to change, but users may not understand why unchanged rows require approval.
- **Weakness:** Time is a poor proxy for live truth.

**Interactions:** Conservative recovery bundles may accept this annoyance. Cross-world autonomous routing suffers.

---

### Option B — Sliding lease renewed by evidence refresh

A complete refresh extends confirmation for another period.

**User-visible behavior:** Confirmation usually stays alive while discovery succeeds.

**Operational consequences:** Couples approval to aggregator availability and refresh cadence.

**Tradeoffs:**

- **Safety:** Better evidence recency, but still not purchase authority.
- **Annoyance:** Moderate.
- **Automation:** Good while the provider is healthy.
- **Reversibility:** Moderate.
- **Weakness:** A refreshed aggregator result may still differ from the visible game row seconds later.

**Interactions:** Can cause provider failure to halt otherwise UI-verifiable purchases.

---

### Option C — No wall-clock expiry; Workbench revision plus live UI revalidation

Confirmation remains valid for the finalized Workbench revision and envelope. Immediately before every purchase, Route proves the visible row and checks the remaining authority.

**User-visible behavior:** No arbitrary countdown. Routine unchanged purchases proceed. Changed rows invoke the chosen recovery policy.

**Operational consequences:** Route must revalidate each purchase and track remaining quantities, spent gil, caps, and current route state. Workbench edits always invalidate confirmation.

**Tradeoffs:**

- **Safety:** High because authority is checked at the spending boundary.
- **Annoyance:** Low.
- **Automation:** High.
- **Reversibility:** A maximum-age rule can be added later.
- **Weakness:** Requires robust UI automation and recovery logic; failures cannot fall back to structs or aggregator assumptions.

**Interactions:** Best match for layered envelopes and autonomous exact-quality recovery.

---

### Option D — Hybrid maximum plan age plus live revalidation

Every purchase uses live UI revalidation, but the complete confirmation also expires after a longer maximum age, such as 30 or 60 minutes.

**User-visible behavior:** Normal routes usually complete uninterrupted; abandoned plans eventually require review.

**Operational consequences:** Adds a stale-intent boundary even when individual rows remain valid.

**Tradeoffs:**

- **Safety:** Very high.
- **Annoyance:** Low to moderate.
- **Automation:** High for ordinary routes.
- **Reversibility:** Duration is easy to tune.
- **Benefit:** Prevents executing a plan the user confirmed hours or days ago.
- **Question:** The correct duration is a product judgment, not an evidence fact.

**Interactions:** Useful if finalized Workbenches can remain queued or survive restarts.

---

### Option E — Confirmation tied to evidence generation

Any new published generation invalidates confirmation.

**User-visible behavior:** Refreshing market evidence can require confirmation even when the resulting plan is equivalent or cheaper.

**Operational consequences:** Strong lineage simplicity: one confirmation maps to one generation.

**Tradeoffs:**

- **Safety:** High.
- **Annoyance:** High.
- **Automation:** Low.
- **Reversibility:** Can later compare generations structurally.
- **Weakness:** Evidence mechanics become user-facing authority even when no meaningful decision changed.

**Interactions:** Conflicts with quiet healthy automation and changed-evidence classification.

---

### Option F — Confirm once at route start; no per-purchase revalidation

Route trusts the prepared plan after initial confirmation.

**User-visible behavior:** Smooth until a purchase fails or buys the wrong row.

**Operational consequences:** Unsafe in a changing market and incompatible with the UI-authority requirement.

**Tradeoffs:**

- **Safety:** Poor.
- **Annoyance:** Low.
- **Automation:** High.
- **Reversibility:** Possible, but failures may already spend gil incorrectly.

This is not acceptable under the roadmap’s evidence rules.

---

### Option G — Aggregator refresh as sufficient authority

Route buys based on current provider data without proving the visible game row.

This is not a valid option. Decision-critical listings and purchases must be verified through UI automation; aggregator data may support discovery and planning only.

## 6.5 Recommendation

**Recommendation: Option C for active routes, with a narrowly defined stale-intent rule if finalized Workbenches can wait indefinitely.**

The core rule should be:

- Confirmation binds a Workbench revision and fixed approval envelope.
- Every purchase requires immediate live UI revalidation.
- A changed row invokes recovery; it does not automatically erase confirmation if recovery remains within the envelope.
- Any Workbench edit requires reconfirmation.
- Restarting the plugin or resuming an old finalized Workbench should not silently purchase without presenting its confirmed revision and remaining authority.

If Fran wants a wall-clock boundary, use Option D with a relatively long stale-intent duration. Do not use five minutes as a claim that market evidence remains trustworthy.

---

# 7. Beginning-to-End Recommended Workflow

This walkthrough demonstrates the balanced recommendation. It is not implemented or approved.

## Step 1 — Advisor produces choices

1. Squire observes the player’s equipment through automated UI.
2. It loads complete, exact-quality market evidence for the supported scope.
3. It evaluates feasible loadouts using the selected utility profile and context.
4. It shows the cost/utility Pareto frontier, Advisor nomination, adjacent solutions, acquisition burden, evidence status, and expected cost.
5. Fran selects a frontier solution. This selection, not the nomination, becomes the candidate handoff.

If evidence is incomplete, utility is unsupported, or the authority layer abstains, no acquisition handoff is available.

## Step 2 — Exact lineage is created

The handoff records:

- Selected solution ID.
- Advisor nomination ID for comparison.
- Utility profile and context.
- Every selected slot, item, exact NQ/HQ quality, quantity, and acquisition source.
- Every accepted market row and its world, quantity, price, and evidence generation.
- Expected market total.

Owned and gil-vendor selections remain part of full-solution lineage, but only market lines enter Market Acquisition.

A solution with no market acquisition produces no Workbench handoff.

## Step 3 — Existing Workbench receives the Squire subset

Squire places exact-quality lines into the existing Workbench. It does not open a second editor.

Each Squire line shows:

- Item name.
- NQ or HQ.
- Required quantity.
- Expected unit and line cost.
- Proposed line caps.
- Origin and selected-solution context when expanded.

The Workbench also shows:

- Expected Squire market total.
- Proposed absolute maximum total.
- Derived gil and percentage headroom.
- Allowed world scope.
- Whether unrelated manual lines are also present.

## Step 4 — Fran reviews and confirms

Fran may edit the envelope and routing preferences.

Under the recommended lineage rules:

- Cap or route edits preserve gear lineage but create a new Workbench revision.
- Adding unrelated manual work preserves the Squire subset.
- Item, quality, quantity, or required-line removal marks the solution as modified and removes Squire execution readiness.

Confirmation binds:

- The finalized Workbench revision.
- The exact Squire gear identity.
- Per-line caps.
- Plan-wide cap.
- World restrictions.
- Remaining quantities.
- The recovery-authority policy.

Route receives authority only after explicit confirmation.

## Step 5 — Route prepares from current evidence

Before travel, Route refreshes or obtains complete evidence for every remaining Squire market item.

It compares the finalized accepted lots with current evidence:

- Unchanged.
- Missing.
- Price increased or decreased.
- Quantity increased or decreased.
- Exact quality changed.
- World changed.
- Source/evidence revision changed.

Evidence changes are facts, not automatic failures. Route then computes the best exact-quality remaining route within the finalized envelope.

## Step 6 — Route travels and revalidates

At each world and before each purchase:

1. Route opens and searches the market board through UI automation.
2. It observes visible rows.
3. It proves item, exact quality, quantity, price, and world.
4. It checks the line cap and remaining plan-wide cap.
5. It checks whether the row fulfills all or part of the remaining quantity.

No struct or aggregator result authorizes the purchase.

## Step 7A — Accepted row remains valid

If the row is still present and inside the envelope, Route buys it and verifies the result through UI.

It then updates:

- Purchased quantity.
- Gil spent.
- Remaining line authority.
- Remaining plan authority.
- Remaining worlds and listings.

## Step 7B — Row changed but procurement recovery is possible

If the row is missing or changed, Route considers only rows for the same item and exact quality.

It may:

- Use another row on the same world.
- Combine partial rows.
- Use another allowed world.
- Reorder remaining visits.
- Account for gil already spent.

If the revised remaining route satisfies every finalized constraint, it proceeds autonomously under the recommended policy and updates the visible Route plan.

## Step 7C — Recovery exceeds authority

If exact-quality procurement is possible but exceeds a cap or world restriction, Route pauses.

The user sees:

- What changed.
- Original and revised total.
- Line-level cost differences.
- Added or removed worlds.
- Remaining quantities.
- Which constraint failed.

Fran may revise the Workbench envelope and reconfirm, or cancel.

## Step 7D — Gear identity must change

If fulfillment requires another item, another quality, or another quantity, Route does not improvise.

It returns to Advisor with:

- Items already purchased.
- Remaining need.
- Current market evidence.
- The failed exact-quality requirement.
- Any relevant user constraints.

The Advisor constructs a new frontier that accounts for already-owned purchases. Fran selects again.

## Step 8 — Completion

Route completes only when the finalized quantities are verified as purchased within the confirmed authority.

The audit record preserves:

- Selected solution lineage.
- Finalized Workbench revision.
- Approved caps.
- Every live row actually purchased.
- Substitutions and route changes.
- Actual gil spent.
- Any pauses, reconfirmations, or returns to Advisor.

---

# 8. Three Coherent Policy Bundles

## Bundle A — Conservative / Manual

### Rules

- **Envelope:** Exact observed-price lock or explicitly entered per-line caps; no meaningful automatic upward drift.
- **Recovery:** Changed rows generate a proposed revised route and require confirmation.
- **Lineage:** Any Squire-line item, quality, quantity, or deletion invalidates lineage; cap and route edits also require reconfirmation.
- **Freshness:** Live UI revalidation plus a short or moderate confirmation lease.

### User experience

The user approves frequently. The system explains every changed listing and does little silently beyond unchanged or cheaper exact-row execution.

### Advantages

- Safest early rollout.
- Easy to audit.
- Minimal chance of surprising spend or travel.
- Lowest implementation risk for autonomous recovery.

### Costs

- Market volatility produces frequent interruptions.
- Cross-world routes may require several confirmations.
- A timer can demand review even when nothing meaningful changed.
- Automation feels brittle despite strong evidence machinery.

### Best fit

A developer preview, initial opt-in execution release, or users who want acquisition assistance but not autonomous recovery.

---

## Bundle B — Balanced / Recommended

### Rules

- **Envelope:** Absolute per-line caps plus an absolute plan-wide cap. Percent or extra-gil controls derive those fixed values.
- **Recovery:** Automatic substitution, partial-row combination, and cross-world replanning for the same item and exact NQ/HQ quality while all constraints remain satisfied.
- **Lineage:** Semantic invalidation. Gear edits invalidate selected-solution integrity; cap and routing edits preserve gear lineage but require confirmation; unrelated manual lines remain separate.
- **Freshness:** Confirmation binds the Workbench revision and envelope. Every purchase receives immediate live UI revalidation. No short wall-clock lease.

### User experience

The user makes one informed decision in Advisor, reviews one concrete Workbench envelope, and usually lets Route complete quietly. The product interrupts only when spending authority, world constraints, or gear identity would change.

### Advantages

- Strong protection against total overruns and individual outliers.
- High useful automation.
- Clear boundary between Advisor decisions and Route optimization.
- Changed markets are repaired without pretending that a different item is equivalent.
- The UI can remain quiet during healthy recovery.

### Costs

- Requires real route replanning, not a superficial retry.
- Requires persisted solution-level and per-line provenance.
- Requires remaining-authority accounting after partial purchases.
- Confirmation and recovery states need careful tests and audit records.

### Best fit

The intended mainstream M8 behavior once the complete execution path has been tested.

---

## Bundle C — Permissive / Autonomous

### Rules

- **Envelope:** Plan-wide cap only, or a broad relative tolerance with few line constraints.
- **Recovery:** Automatic exact-quality route recovery and optionally automatic selection of a different supported Pareto solution above a pre-approved utility floor.
- **Lineage:** Edits trigger automatic constrained re-solving; historical lineage remains but current selection may change automatically.
- **Freshness:** No wall-clock expiry; live UI revalidation and continuous replanning.

### User experience

The user delegates an outcome such as “obtain at least this utility under this total,” and the system may change gear or route substantially to achieve it.

### Advantages

- Maximum resilience to volatile markets.
- Few interruptions.
- Can exploit cheaper or newly available alternatives dynamically.
- Potentially powerful long-term automation.

### Costs

- Confirmation must encode utility floors, threshold protections, profile versions, and acceptable structural changes.
- The system may buy gear different from what Fran selected.
- Already purchased items create sunk-cost and portfolio problems.
- Workbench risks becoming a second Advisor.
- Persisted authority becomes sensitive to future utility-model changes.
- This drags M10-style allocation and replanning concerns into M8.

### Best fit

A later opt-in expert mode, after the balanced model has proven its audit, recovery, and partial-purchase semantics. It is not recommended for the first execution-authority release.

---

# 9. What Is Easy or Expensive to Change Later

## Relatively easy to change

These can usually change through defaults or presentation without redefining persisted authority:

- Whether envelope input is displayed as percentage, extra gil, absolute maximum, or multiple synchronized fields—provided the persisted authority is always an absolute cap.
- Default tolerance suggestions.
- Whether per-line caps are expanded by default.
- Whether same-world recovery is preferred before cross-world recovery.
- Route sorting preferences among equally authorized alternatives.
- Whether healthy substitutions produce a transient status line or only appear in history.
- A long stale-intent timeout, if live UI revalidation remains mandatory.
- Conservative versus balanced mode as a user preference, if the underlying finalized document records the exact active policy.

## Moderate migration cost

These require document versioning or careful defaults for existing finalized work:

- Adding a plan-wide cap to documents that previously had only line caps.
- Adding per-line caps to documents that previously had only a total.
- Changing whether the cap covers the Squire subset or the entire mixed Workbench.
- Adding allowed-world or recovery-policy fields to persisted confirmation.
- Splitting historical origin from current-valid lineage.
- Introducing solution-level integrity on top of per-line origin metadata.

Existing documents must not silently receive broader authority during migration. Safe migration defaults should pause for reconfirmation.

## High migration and expectation cost

These choices shape what users believe “Confirm” means:

- Switching from exact-price confirmation to broad autonomous drift.
- Switching from total-only authority to line-and-total authority after users learn that any line is acceptable below the total.
- Allowing different items or qualities after initially promising exact gear.
- Making Workbench a second recommendation editor.
- Persisting relative tolerances without fixed derived caps.
- Binding execution authority to utility profiles that can later be recalibrated.
- Treating an Advisor nomination as authority instead of the user-selected solution.
- Removing live UI revalidation after users rely on it as the spending boundary.
- Retroactively interpreting historical Squire lineage as current execution authority.

The most important schema decision is to persist explicit, versioned authority rather than infer it from current defaults. A saved Workbench must say exactly what was confirmed at the time.

---

# 10. Exact Questions Fran Must Answer

No answer is inferred from silence. “Approve recommended bundle” is sufficient only if every recommendation below is accepted.

## A. Approval envelope

1. Should M8 use:

   - Exact observed-price lock;
   - Per-line caps only;
   - Plan-wide cap only;
   - Layered per-line and plan-wide caps;
   - Confirm every changed plan; or
   - Another model?

2. If using a plan-wide cap, should it govern:

   - Only Squire-originated market lines; or
   - The complete finalized Workbench, including unrelated manual lines?

3. How should the user enter headroom?

   - Absolute maximum total;
   - Extra gil above expected;
   - Percentage above expected;
   - Multiple synchronized controls, with an absolute value persisted as authority?

4. Should cheaper rows automatically proceed even when another line became more expensive, provided every cap remains satisfied?

5. Should the product suggest default headroom, and may the user set zero upward drift?

## B. Recovery authority

6. Which recovery scope is approved?

   - No automatic recovery;
   - Exact accepted row only;
   - Same-world, same-item, exact-quality substitution;
   - Cross-world replanning for unchanged item, quality, and quantity;
   - Proposed revised route requiring confirmation;
   - Different gear within a utility floor; or
   - Another boundary?

7. May Route combine multiple partial exact-quality rows to satisfy one line?

8. May Route revisit or add an allowed world when the complete remaining route still fits the envelope?

9. Confirm whether any item, NQ/HQ quality, or required-quantity change must return to Advisor.

10. If exact gear cannot be acquired within the envelope after some purchases succeeded, should Route:

    - Pause and return to Advisor with purchased items treated as owned;
    - Keep waiting and refreshing;
    - Offer both choices; or
    - Follow another rule?

## C. Lineage invalidation

11. Should item, NQ/HQ quality, quantity, or removal of a required Squire line invalidate current selected-solution integrity?

12. Should cap and routing edits preserve gear lineage while requiring a new confirmation?

13. Should unrelated manually added Workbench lines preserve the Squire subset’s lineage?

14. Should historical lineage and a structural diff remain visible after invalidation, clearly separated from current authority?

15. When a gear-semantic edit invalidates lineage, should the UI:

    - Offer a return to Advisor with constraints;
    - Convert the lines into ordinary manual work;
    - Do both; or
    - Automatically rerun Advisor?

## D. Freshness and confirmation

16. Should confirmation use:

    - A fixed time lease;
    - A refresh-renewed lease;
    - No wall-clock expiry during an active route, with live UI revalidation;
    - A long maximum age plus live UI revalidation; or
    - Another model?

17. If there is a maximum age, what duration should apply, and does it pause while Route is actively progressing?

18. Confirm that every purchase must be revalidated through visible game UI immediately before spending gil.

19. Should a new evidence generation alone invalidate confirmation, or only an operational change that violates the finalized envelope or identity?

20. What should happen after plugin restart or game restart with a partially completed finalized route?

    - Require explicit resume confirmation;
    - Resume automatically after live revalidation;
    - Expire after a specified age; or
    - Another rule?

## E. Bundle-level answer

21. Which coherent starting system should M8 implement?

    - Conservative/manual;
    - Balanced/recommended;
    - Permissive/autonomous; or
    - A specified hybrid?

22. If choosing the balanced bundle, are all of these approved?

    - Absolute per-line and plan-wide caps.
    - Exact item and NQ/HQ quality preserved.
    - Autonomous partial-row and cross-world procurement replanning inside every cap.
    - Any gear-identity change returns to Advisor.
    - Semantic lineage invalidation.
    - Confirmation bound to Workbench revision and envelope.
    - Live UI revalidation before every purchase.
    - No short fixed confirmation lease.

**Until Fran explicitly answers, M8 remains paused before Workbench persistence, cap population, UI mutation, recovery selection, and Route authority.**

