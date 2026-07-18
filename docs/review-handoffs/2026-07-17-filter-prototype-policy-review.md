# Filter prototype review against Fran's policy

Source task: `019f5f39-2b1a-7721-bb68-20a1a75940e8`

Status: Review only. No code, branch, deployment, or runtime change was authorized.

## Full task response
# Intentional Filter Grammar Policy Review

## Grammar model after Fran’s decisions

The language should have two visibly distinct modes:

```text
darksteel
-darksteel
```

These are default item-name searches.

```text
location:armoury
quantity:20
quantity:>20
is:equipped
```

These are intentional structured filters. The colon is the unmistakable boundary between a qualifier and its specifier.

A suitable grammar is:

```ebnf
term             = [ "-" ] , ( free-text | structured | group ) ;

structured       = qualifier , ":" , specifier ;

specifier        = direct-value
                 | comparison-operator , value ;

comparison-operator
                 = "=" | "==" | "!=" | "<" | "<=" | ">" | ">=" ;
```

Legacy forms such as `quantity>20` and dotted paths such as `instance.quantity:>20` remain accepted, but autocomplete and primary reference UI should emit the intentional form.

## Recommended operator algebra

### `:` — structure only

The colon separates a qualifier from its specifier:

```text
location:armoury
quantity:20
quantity:>20
name:=darksteel
```

It is not itself a comparison operator. The qualifier’s schema determines whether a direct specifier is valid.

### Direct values — concise human-language selection

Fran has approved concise direct values:

```text
location:armoury
quality:hq
quantity:20
retainer:Belladonna
```

For symbolic, identity, numeric, and boolean values, the direct form should mean exact semantic selection:

- `location:armoury` selects the `Armoury` enum value.
- `quality:hq` selects HQ.
- `quantity:20` means numeric equality with 20.
- `is:equipped` selects the `equipped = true` predicate.
- Registered aliases remain valid ways to identify the same semantic value.

This is exact at the domain-value level, not necessarily byte-for-byte string equality.

### `=` — deterministic matching

Given Fran’s preference, `=` should become the explicit matching operator.

```text
name:=darksteel
retainer:=bella
```

The semantics should be deterministic:

- Unicode-normalize and case-fold both operands.
- Collapse repeated whitespace.
- Perform normalized substring matching for free text.
- Resolve registered aliases for named values.
- Never use edit distance, phonetic matching, stemming, ranking thresholds, or regex.
- For finite named vocabularies, an abbreviated value must resolve to one semantic candidate; multiple candidates produce an ambiguity diagnostic.
- For collections, `=` succeeds when any member matches.
- For numbers and other non-text scalar types, matching collapses to typed equality.
- Unknown evidence remains unknown.

Thus `name:=darksteel` matches “Darksteel Ingot,” while numeric `quantity:=20` behaves like `quantity:20`.

### `==` — exact whole-value equality

I recommend `==` as the distinct exact operator:

```text
name:=="Darksteel Ingot"
retainer:==Belladonna
quantity:==20
```

Its semantics should be:

- Text must equal the complete normalized value; substring matching is disabled.
- Named values and aliases resolve to semantic identity, then compare identity.
- Numbers, durations, percentages, and timestamps compare typed values.
- Collections match when any member is exactly equal.
- Unknown evidence remains unknown.

`==` is preferable to `~=` because Fran has assigned fuzzy semantics to `=`. It is also preferable to `:=`, which is commonly read as assignment and visually competes with the qualifier colon.

“Exact” should mean whole semantic value, not case-sensitive bytes. Requiring users to reproduce capitalization or Unicode presentation exactly would be hostile without improving selection correctness.

### `!=` — negated matching

To keep the operator algebra coherent, `!=` should negate `=`, not `==`:

```text
name:!=darksteel
```

This means “name does not contain the normalized term darksteel.”

Exact inequality can be expressed through ordinary predicate negation:

```text
-name:=="Darksteel Ingot"
```

This avoids inventing `!==`, keeps `!=` visually paired with `=`, and gives every predicate one universal negation mechanism.

For unknown evidence, `!=` remains unknown rather than becoming true.

### Ordered comparisons

Ordered comparisons retain their ordinary meanings:

```text
quantity:>20
quantity:>=20
condition:<100
price:<=2500
age:<10m
```

They are valid only for fields whose value types define ordering. `name:>darksteel` should be rejected rather than assigned lexical ordering.

## Decision: permit `-darksteel`

`-darksteel` should exclude default-text matches.

```text
darksteel -ore
```

This means “item name contains darksteel and does not contain ore.”

This does not violate intentional structured filtering because bare-text negation remains bare-text search. It cannot resolve fields, booleans, or aliases:

- `equipped` searches item names for “equipped.”
- `-equipped` excludes item names containing “equipped.”
- `is:equipped` tests equipment state.
- `-is:equipped` excludes equipped instances.

That distinction is crisp: only a colon activates structured semantics.

The tokenizer should recognize unary `-` only at a term boundary—start of input, after whitespace, or after `(`. Internal hyphens remain literal:

```text
high-quality
```

A literal name beginning with a hyphen would require quoting. That is a narrow and explainable exception.

`-name:darksteel` remains valid and more explicit, but it should not be required for the common exclusion case.

## Human-readable evidence predicates

Future evidence filtering should use user language:

```text
has:price
has:condition
has:marketdata
-has:price
```

These predicates should preserve Franthropy’s three-valued evidence semantics. `-has:price` means evidence is absent; it is not equivalent to `price:!=0`.

The existing `known(...)` and `unknown(...)` functions can remain supported as advanced syntax, but should eventually leave the taught surface.

## Taught surface versus advanced compatibility

Primary autocomplete and reference UI should teach:

```text
location:armoury
quality:hq
quantity:20
quantity:>=20
name:=darksteel
name:=="Darksteel Ingot"
is:equipped
-darksteel
-location:saddlebag
```

It should not primarily teach:

```text
instance.location:armoury
quantity>=20
known(instance.condition)
equipped
```

Those remain valid compatibility or advanced forms. They should appear in diagnostics, expanded reference material, or ambiguity resolution—not compete with the common vocabulary.

Short aliases must retain stable identity across contexts. A qualifier may become unavailable on a surface, but it must never silently resolve to a different field.

# Prototype Review

## Franthropy `51a29ec` — MODIFY

The commit contains valuable infrastructure, but its operator model does not implement the approved language.

### Colon-prefixed ordered comparisons — KEEP behavior, MODIFY representation

The commit accepts:

```text
quantity:>20
quantity:>=20
```

while preserving:

```text
quantity>20
quantity>=20
```

That matches the approved compatibility policy.

However, the implementation stores the colon as an optional separator attached to the old field-comparison node while continuing to treat direct `field:value` colon syntax through the old `Match` comparator. The future grammar should represent the colon consistently as the qualifier/specifier boundary.

The behavior and compatibility tests are reusable; the syntax model needs restructuring when the grammar is implemented fully.

### `quantity:20` — KEEP

The existing direct-colon path already accepts numeric shorthand, ranges, symbolic values, and lists. That aligns with Fran’s approval of intuitive direct specifiers.

Tests for direct values and ranges remain relevant.

### Existing `=` semantics — SCRAP and replace

The commit leaves `=` as exact equality and `:` as match. That directly conflicts with the new policy direction.

The comparison enum, descriptions, completion ordering, compiler binding, and documentation must eventually be redesigned around:

- `=` deterministic match
- `==` exact whole-value equality
- `!=` negated match
- ordered comparisons unchanged

Trying to reinterpret only display labels would be dangerous; parser, compiler, formatter, reference metadata, and diagnostics must agree.

### Post-colon operator completion — MODIFY

The caret handling and replacement-span mechanics are reusable. Completion already understands `quantity:>` and offers `>`/`>=`.

Its operator catalog is policy-incompatible because it offers the old `=` semantics and has no `==`. Keep the completion machinery; replace the operator vocabulary and descriptions.

### Negated completion — KEEP mechanism, MODIFY language context

Preserving the leading `-` while replacing `loc` in `-loc` is good direct-manipulation behavior.

The same mechanism can support both:

```text
-darksteel
-location:saddlebag
-is:equipped
```

But completion must not suggest a structured field merely because a bare word happens to share its alias. `-equipped` must remain negative text search; structured state completion should live under `-is:`.

### Context-safe preferred names — KEEP

`FilterCatalog.GetPreferredName`, generated `PreferredName`, ambiguous-leaf fallback, and alias-shadowing tests solve a real correctness problem.

This work is independent of the rejected operator model and should remain:

- Alias precedence matches parser resolution.
- A leaf is suggested only when it resolves to that field.
- Ambiguous fields fall back to qualified names.
- Reference generation and autocomplete share one resolution authority.

The eventual taught vocabulary will need stable aliases such as `is:`, but this resolution primitive is still correct and valuable.

### Short-field completion — MODIFY

Preferring `location` over `instance.location` is aligned with the policy.

However, simply selecting the shortest valid leaf does not create the intended user vocabulary. Boolean fields need intentional aliases such as `is:equipped`, while dotted paths remain advanced fallbacks.

Keep the ambiguity machinery; redesign the public qualifier catalog.

### Grammar documentation — SCRAP and rewrite

The modified grammar documents optional colon prefixes around the old comparator system. It does not distinguish text search from intentional structured filtering, does not define `is:`, and preserves the old `=` meaning.

A replacement document should start from the qualifier/specifier grammar rather than incrementally editing this version.

### Tests — MODIFY

Keep and adapt tests covering:

- Legacy operator-only compatibility.
- Colon-prefixed ordered comparisons.
- Alias-versus-leaf shadowing.
- Context ambiguity.
- Negation-aware replacement spans.
- Formatter round-tripping.

Replace assumptions that `=` means exact and add coverage for:

- `=` normalized matching.
- `==` exact equality.
- `!=` negated matching.
- `-field:==value` exact exclusion.
- Bare `equipped` versus `is:equipped`.
- `-darksteel` versus `-is:equipped`.
- Human-readable evidence predicates when that phase begins.

## MarketMafioso `46b08d9` — MODIFY

This commit is small and contains useful tests, but its taught UI reflects the incomplete prototype policy.

### Franthropy pin — MODIFY as integration bookkeeping

The pin correctly records the integrated shared commit. It is not itself a product behavior.

When an approved grammar revision eventually exists, the pin should follow the approved shared commit. No action is authorized now.

### `PreferredName` reference rendering — KEEP mechanism, MODIFY vocabulary

Rendering the shared generated preferred name is better than duplicating resolution logic in MMF.

Keep that architecture. Change the generated vocabulary upstream so MMF receives `is:` and other approved human-facing qualifiers rather than inventing presentation aliases locally.

### Example `location:Armoire` — KEEP

This is exactly the approved concise symbolic form.

Capitalization can remain presentation-friendly while parsing stays case-insensitive.

### Example `quantity:>20` — KEEP

This matches the approved intentional syntax, with `quantity>20` retained only as compatibility syntax.

### Example `-equipped` — SCRAP

Under the intentional grammar, this means negative item-name search, not equipment-state filtering.

The structured example must become:

```text
-is:equipped
```

Teaching `-equipped` as a state predicate would preserve the accidental-filter ambiguity Fran is explicitly trying to eliminate.

### End-to-end mixed-query test — KEEP with future vocabulary expansion

This query remains good:

```text
darksteel -location:inventory quantity:>20
```

It exercises bare text, structured negation, and an ordered comparison together.

Additional tests should eventually distinguish:

```text
-equipment
-is:equipped
```

so text exclusion and state exclusion cannot regress into each other.

### Short-field completion test — MODIFY

Completing `-loc` to `-location` is reasonable because the partial token clearly begins a known qualifier, but colon insertion should be part of the final UX:

```text
-loc
→ -location:
```

Autocomplete must avoid turning arbitrary negative text into a structured predicate merely because it contains a field name. The colon remains the moment structured semantics become committed.

### Operator completion test — MODIFY

Offering `>` and `>=` after `quantity:` is correct.

The wider completion behavior needs the approved algebra:

- `=` match
- `==` exact
- `!=` not-match
- ordered operators

Descriptions must make the difference unmistakable.

# Live-State Consequences

## Primary plugin

Primary was built from MMF `local-dev` at `68b0162`, which includes Franthropy `51a29ec`.

Therefore Primary currently contains:

- Parsing support for `quantity:>20`.
- Legacy operator-only compatibility.
- Context-safe preferred-name infrastructure.
- Negation-aware completion machinery where consumed.
- The old `=` exact and `:` match semantics.
- No intentional `is:` grammar.
- No fuzzy `=` or exact `==`.

This is an integrated prototype, not the approved final language. Its presence should be tracked, but no revert, rebuild, or redeployment is authorized by this review.

## Hosted dashboard

The hosted dashboard was not deployed from MMF `46b08d9`.

Therefore users of the hosted inventory viewer are not currently being taught the prototype examples or `PreferredName` presentation changes. Its live UI remains on the earlier behavior even though local source contains the prototype commit.

This split means the current runtime cannot be evaluated as one coherent grammar release: Primary has the shared parser experiment, while the hosted teaching surface does not.

# Clarifications Still Required from Fran

These are not resolved by the supplied policy and should not be inferred during implementation.

1. **Exact operator:** Approve or reject `==` as whole-value exact equality.
2. **Negated matching:** Approve or reject `!=` as the logical negation of fuzzy `=`, with exact exclusion written `-field:==value`.
3. **Direct text qualifiers:** Decide whether `name:darksteel` is shorthand for fuzzy `name:=darksteel`, or whether text qualifiers require an explicit operator while bare text remains the shorthand.
4. **Named-value fuzzy matching:** Decide whether partial named values may resolve through `=` only when exactly one candidate matches, or whether `=` on finite enums should accept registered aliases only.
5. **Boolean aliases:** Approve the initial canonical `is:` vocabulary, particularly whether states such as HQ appear only as `quality:hq`, also as `is:hq`, or one as a hidden alias of the other.

No repository, branch, deployment, or runtime state was modified during this review.

