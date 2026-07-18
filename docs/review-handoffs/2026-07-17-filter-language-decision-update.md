# Filter language decision update

Source task: `019f5f39-2b1a-7721-bb68-20a1a75940e8`

Status: Review only. No repository or runtime changes were authorized.

## Full task response
# Filter Language Decision Update

## Settled operator algebra

Fran’s proposed `!==` is defensible. There is no sufficiently strong parsing or usability reason to reject it.

The clean algebra is to treat `!` as a negating modifier on an equality operator:

| Operator | Meaning |
|---|---|
| `=` | Deterministic fuzzy/matching equality |
| `!=` | Negated fuzzy/matching equality |
| `==` | Normalized whole-value exact equality |
| `!==` | Negated normalized whole-value exact equality |
| `<`, `<=`, `>`, `>=` | Typed ordered comparison |

This preserves the equality mode represented by the base operator:

```text
=    fuzzy equality
!=   not fuzzy equality

==   exact equality
!==  not exact equality
```

Although `!==` is visually associated with JavaScript’s strict inequality, the grammar remains internally coherent, parses unambiguously through longest-token matching, and can be explained in one sentence. Autocomplete also means users will rarely need to remember or manually construct the operator.

### Fuzzy equality: `=`

```text
name:=darksteel
retainer:=bella
```

The match must be deterministic:

- Unicode-normalize and case-fold.
- Collapse repeated whitespace.
- Use normalized substring matching for open text.
- Resolve registered aliases for named values.
- Never use edit distance, stemming, phonetics, regex, or ranking thresholds.
- For finite vocabularies, a partial value may resolve only when exactly one candidate matches.
- Multiple finite-vocabulary candidates produce an ambiguity diagnostic.
- For collections, match when any member matches.
- For numeric and other non-text scalar types, fuzzy equality collapses to typed equality.
- Unknown evidence remains unknown.

Thus:

```text
name:=darksteel
```

matches “Darksteel Ingot,” while:

```text
name:==darksteel
```

does not.

### Negated fuzzy equality: `!=`

```text
name:!=darksteel
```

This means the known name does not contain the normalized term `darksteel`.

It is the logical negation of the `=` relation. It does not become exact merely because `!` was added.

Unknown evidence remains unknown rather than passing the exclusion.

### Exact equality: `==`

```text
name:=="Darksteel Ingot"
retainer:==Belladonna
quantity:==20
```

Exact equality means normalized whole-value equality:

- Text compares the complete case-folded, Unicode-normalized, whitespace-normalized value.
- Named values and their registered aliases resolve to semantic identity before comparison.
- Numbers, durations, timestamps, and percentages compare typed values.
- Collections match when any member has exact semantic identity.
- Unknown evidence remains unknown.

It does not mean case-sensitive or byte-for-byte equality. Those distinctions are user-hostile and irrelevant to the intended inventory semantics.

### Exact inequality: `!==`

```text
name:!=="Darksteel Ingot"
retainer:!==Belladonna
quantity:!==20
```

This is the logical negation of `==` for known evidence.

The equivalent predicate-level form remains valid:

```text
-name:=="Darksteel Ingot"
```

Both should produce the same three-valued result. `!==` is useful because it keeps a simple comparison local, while prefix negation remains necessary for compound predicates and direct symbolic forms.

### Direct-value shorthand

Direct values remain qualifier-sensitive, but their behavior is now settled for the important categories:

```text
name:darksteel
```

means fuzzy name matching, equivalent to:

```text
name:=darksteel
```

Meanwhile:

```text
location:armoury
quality:hq
quantity:20
retainer:Belladonna
```

select exact semantic values. These qualifiers describe a finite identity or typed scalar rather than open text.

Consequently:

```text
quantity:20
quantity:=20
quantity:==20
```

all produce numeric equality, because fuzzy and exact equality collapse to the same typed relation for numbers.

The concise direct form remains the taught form.

## Complete examples

```text
darksteel
-darksteel

name:darksteel
name:=darksteel
name:=="Darksteel Ingot"
name:!=ore
name:!=="Darksteel Ingot"

quality:hq
location:armoury
-location:saddlebag

quantity:20
quantity:>=20
condition:<100
price:<=2500

is:equipped
-is:equipped
is:hq

has:price
-has:price
```

Legacy forms such as `quantity>=20` and dotted canonical paths remain valid advanced syntax, but primary autocomplete and reference UI should emit colon-delimited qualifiers.

# Nested Qualifier Investigation

## DIM precedent

DIM uses both flat `is:` predicates and nested parameterized qualifiers.

Flat state/category predicates include forms such as:

```text
is:handcannon
is:arc
is:tagged
-is:inloadout
```

Parameterized properties use nesting:

```text
stat:range:>=50
stat:custom:>=60
basestat:resilience+discipline:>=18
```

DIM therefore treats nesting as a way to identify a property inside a parameterized domain, while `is:` remains a flat vocabulary of state-like predicates. [DIM Item Search](https://github.com/DestinyItemManager/DIM/wiki/Item-Search)

That precedent favors `is:hq`, not `is:quality:hq`.

## Option A: `is:hq`

```text
is:hq
```

This reads naturally: “is high quality.”

It behaves as a friendly alias for:

```text
quality:hq
```

### Advantages

- Matches DIM’s flat `is:` vocabulary.
- Reads like human-language pseudocode.
- Keeps the common predicate short.
- Does not require variable-depth parsing.
- Lets autocomplete after `is:` present one flat list of states.
- Does not imply that all ordinary property filters belong under `is:`.
- Maps cleanly to the same semantic predicate as `quality:hq`.
- Preserves `quality:` as the canonical dimension when users want to inspect or select quality explicitly.

### Consequences

The alias catalog must represent predicate aliases, not merely field-name aliases. `is:hq` maps to the combined semantic predicate:

```text
field = instance.quality
value = HQ
relation = direct exact selection
```

That is richer than mapping one textual field name to another, but it is the correct abstraction for human vocabulary.

Autocomplete should avoid displaying `quality:hq` and `is:hq` as two competing primary choices in the same list:

- After `quality:`, offer `HQ` and `NQ`.
- After `is:`, offer `hq` alongside other state predicates.
- General reference material identifies `quality:hq` as canonical and lists `is:hq` as an alias.
- Both compile to one semantic identity.

## Option B: `is:quality:hq`

```text
is:quality:hq
```

This reads as “is quality high quality,” which is grammatically awkward.

It treats `is:` as a namespace router, `quality` as a nested qualifier, and `hq` as the final specifier.

### Advantages

- Explicitly records the underlying dimension.
- Avoids placing every possible state token in one flat `is:` vocabulary.
- Creates a general path shape that could theoretically support forms such as `is:location:armoury`.

### Problems

- It is redundant with `quality:hq`.
- It weakens the human-language benefit of `is:`.
- It invites arbitrary nesting under `is:`:
  
  ```text
  is:quality:hq
  is:location:armoury
  is:slot:mainhand
  is:job:paladin
  ```

  At that point `is:` adds no meaning; it becomes ceremonial syntax.
- Autocomplete needs another hierarchy level despite there being only one useful choice after `is:quality:`.
- Canonical formatting becomes contentious: should `is:quality:hq` normalize to `quality:hq`, remain nested, or depend on where it was entered?
- Alias identity becomes more complicated because the alias is now a syntax subtree rather than one qualifier/specifier predicate.
- It commits the grammar to arbitrary-depth qualifier paths before a general model for nested namespaces exists.
- It makes future parsing ambiguity worse when nested values and comparison operators coexist.

## Option C: general nested qualifiers, but not under `is:`

A future grammar can support parameterized domains where nesting carries real information:

```text
stat:criticalhit:>=2500
price:world:Siren:<=2000
source:vendor:city:Gridania
```

Whether those exact examples are desirable is not settled, but they demonstrate the legitimate role of nesting: each segment narrows a meaningful parameterized domain.

This can be represented as:

```ebnf
structured = qualifier , { ":" , qualifier-parameter } , ":" , specifier ;
```

Such nesting should be introduced only when a concrete domain needs it. It should not be forced into HQ merely to exercise the capability.

## Recommendation: `is:hq`

Use:

```text
quality:hq
```

as the canonical quality predicate and:

```text
is:hq
```

as its human-language alias.

Do not adopt `is:quality:hq`.

Nested qualifiers should remain a separately designed future capability for parameterized domains. DIM provides good precedent for both concepts, but it keeps flat `is:` predicates distinct from nested stat selectors.

## Canonical formatting and alias identity

The semantic canonical identity should be one predicate:

```text
instance.quality == HQ
```

Its primary public spelling is:

```text
quality:hq
```

Its friendly alias is:

```text
is:hq
```

Recommended formatting behavior:

- Preserve the user’s entered spelling in the active editor and saved human-authored query.
- Store or expose a separate normalized semantic form for caching, diagnostics, equality checks, and telemetry.
- Generated documentation marks `quality:hq` canonical and `is:hq` an alias.
- Do not visibly rewrite `is:hq` into `quality:hq` merely because a background normalization pass occurred.

This preserves user intent while ensuring both spellings share one semantic identity.

# Updated Prototype Judgments

## Franthropy `51a29ec` — MODIFY

The new answers do not change the overall judgment, but they sharpen which portions conflict.

### KEEP

- Support for colon-prefixed ordered comparisons such as `quantity:>20`.
- Compatibility with operator-only legacy forms.
- AST/formatter ability to preserve a colon before an explicit comparator, as transitional groundwork.
- Unary-negation-aware completion replacement spans.
- Context-safe short-name resolution.
- Alias-versus-leaf shadowing protection.
- Shared generated `PreferredName`.
- Ambiguity and compatibility tests.

### MODIFY

- The parser/tokenizer must eventually recognize longest-match `==` and `!==`.
- Direct text qualifiers such as `name:darksteel` must compile through fuzzy matching.
- Completion after text fields must distinguish fuzzy `=`, exact `==`, fuzzy-negative `!=`, and exact-negative `!==`.
- Operator descriptions must explain normalized substring matching versus normalized whole-value equality.
- Named-value fuzzy matching must diagnose multiple partial candidates.
- Short-name completion must grow into predicate-alias completion capable of representing `is:hq`.
- The current optional-separator AST should become a proper qualifier/specifier model if nested qualifiers are later introduced.
- Tests must distinguish `-darksteel`, `-is:equipped`, `name:!=darksteel`, and `name:!=="Darksteel Ingot"`.

### SCRAP

- Current `=` exact semantics.
- Current `!=` exact-inequality semantics.
- The assumption that colon itself is the `Match` comparison operator.
- The current grammar documentation’s old comparator model.
- Any taught bare boolean predicate such as `equipped`.
- Any future attempt to model `is:hq` as merely another field-name alias; it is a predicate alias containing both field and value.

## MarketMafioso `46b08d9` — MODIFY

The overall judgment remains MODIFY.

### KEEP

- Rendering a shared generated preferred name rather than duplicating resolution logic.
- `location:Armoire` as a taught example.
- `quantity:>20` as a taught example.
- End-to-end coverage combining bare text, location exclusion, and ordered comparisons.
- Caret-aware completion tests for negative qualifier prefixes.

### MODIFY

- The reference surface needs the settled operator vocabulary.
- Completion should insert full qualifier shapes such as `location:` and `is:`.
- Generated field reference data must accommodate predicate aliases such as `is:hq`.
- Tests must cover fuzzy and exact operators distinctly.
- The examples should eventually demonstrate bare-text exclusion and structured state exclusion side by side:
  
  ```text
  -darksteel
  -is:equipped
  ```

### SCRAP

- `-equipped` as an equipment-state example. Under the settled grammar it excludes item names containing “equipped.”
- Any UI copy implying `=` is exact.
- Any reference treatment that presents `is:hq` and `quality:hq` as unrelated filters.

# Current Runtime State

Primary still contains the shared parser prototype from Franthropy `51a29ec` through the MMF build at `68b0162`.

It therefore supports colon-prefixed ordered comparisons but still carries the obsolete operator semantics:

- `=` exact
- `!=` exact inequality
- no `==`
- no `!==`
- no `is:` predicate namespace

The hosted dashboard was not deployed from MMF `46b08d9`, so its live autocomplete/reference UI does not teach the prototype examples.

No runtime, repository, branch, deployment, or hosted state was changed during this investigation.

# Genuinely Unresolved Questions for Fran

1. Should `is:nq` exist alongside `is:hq`, or is HQ the only quality state important enough to receive an `is:` alias?
2. Should saved human-authored filters preserve aliases such as `is:hq` exactly, while a separate normalized semantic form uses `quality:hq`, as recommended?
3. Should general nested qualifiers be explicitly deferred until a concrete parameterized domain needs them, or should the first grammar version reserve and document the nested shape despite not using it for HQ?

