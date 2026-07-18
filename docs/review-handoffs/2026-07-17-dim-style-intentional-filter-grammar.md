# DIM-style intentional filter grammar response

Source task: `019f5f39-2b1a-7721-bb68-20a1a75940e8`

Status: Investigation only. No implementation or repository changes were authorized by this response.

## Full task response
# Proposed Intentional Filter Grammar

Fran’s core distinction is sound: bare text and structured filtering should be visibly different operations.

Today, a word such as `equipped` can become a boolean predicate because it happens to resolve as a field alias. That is powerful, but surprising—the user may believe they are searching item names. Requiring a colon for every structured predicate establishes a clean rule:

> No colon means text search. A colon means intentional filtering.

DIM demonstrates the usability of this contract with expressions such as `-is:equipped is:haspower`; even without knowing every available predicate, the user can recognize the repeated `qualifier:specifier` shape. [DIM FAQ](https://github.com/DestinyItemManager/DIM/wiki/FAQ)

## Proposed grammar

```ebnf
query          = expression , { whitespace , expression } ;
expression     = [ "-" ] , structured
               | free-text
               | "(" , query , ")"
               | "NOT" , expression ;

structured     = qualifier , ":" , specifier ;

specifier      = symbolic-value
               | comparison ;

comparison     = comparison-operator , value ;

comparison-operator
               = "=" | "!=" | "~=" | "<" | "<=" | ">" | ">=" ;
```

The schema behind each qualifier determines whether it accepts symbolic values, comparisons, or both. This lets the parser reject `quantity:banana` immediately while accepting `location:armoury`.

Examples:

```text
darksteel
"augmented credendum"

quality:hq
location:retainer
-location:saddlebag
is:equipped
-is:equipped

quantity:>=20
condition:<100
itemlevel:=660
name:~=darksteel

darksteel quality:hq -location:armoury quantity:>=20
```

Spaces continue to mean AND, while `OR`, parentheses, and explicit `NOT` remain available.

## Bare text versus structured filtering

Bare text should only search configured default text fields:

```text
equipped
```

Under the proposed contract, that searches for an item whose name contains “equipped.” It does not inspect equipment state.

The intentional filter is:

```text
is:equipped
```

This is the largest semantic improvement and the largest compatibility break. It makes accidental filtering far less likely, gives autocomplete a reliable syntactic trigger, and teaches users one recognizable language across inventory, market listings, Outfitter candidates, and future ecosystem surfaces.

Quoted bare text remains useful for phrases:

```text
"augmented credendum"
```

A colon inside a literal item name would require quoting. That is acceptable because otherwise the parser cannot distinguish the literal colon from the structured-filter boundary.

## The role of each operator

| Syntax | Proposed meaning | Applicable values |
|---|---|---|
| `:` | Separates a qualifier from its specifier; never performs comparison itself | All structured filters |
| `=` | Exact equality after type normalization | Text, numbers, enums, identities |
| `!=` | Exact inequality; unknown evidence remains unknown | Text, numbers, enums, identities |
| `~=` | Deterministic textual or alias match | Text and named values |
| `<`, `<=`, `>`, `>=` | Ordered comparison | Numbers, durations, timestamps, percentages |
| `-` | Negates the complete structured predicate that follows | Structured predicates |
| `NOT` | Explicitly negates a predicate or parenthesized group | Advanced composition |

### `:` is structural

The colon should not also mean “match.” It marks the boundary:

```text
quantity:>=20
name:~=darksteel
location:armoury
```

This removes the current conceptual overlap between `:` and `=`. The operator, when one is needed, lives inside the specifier.

### `=` is exact

Exact comparison should be unsurprising:

```text
quantity:=20
condition:=100
name:="Darksteel Ingot"
```

For names, equality should follow ordinary normalization rules—case-insensitive and whitespace-normalized—but should not silently become substring or fuzzy matching.

### `~=` is matching, not regex

`~=` should mean deterministic normalized matching, not arbitrary regex and not edit-distance “fuzziness.” A sensible definition is case-insensitive normalized containment plus registered aliases:

```text
name:~=darksteel
```

This would match “Darksteel Ingot,” while:

```text
name:=darksteel
```

would not.

Keeping `~=` deterministic matters because filters drive counts and future actions. A fuzzy ranking algorithm whose threshold changes could silently alter an actionable selection.

Named-value aliases can participate in `~=` where useful, but canonical symbolic qualifiers should usually not need it:

```text
quality:hq
location:armoury
```

### Ordered comparisons remain explicit

Numeric filters become structurally uniform:

```text
quantity:>20
quantity:>=20
condition:<100
price:<=2500
age:<10m
```

A naked numeric specifier such as `quantity:20` could technically default to equality, but requiring `quantity:=20` would better reinforce that numeric qualifiers perform comparisons. Autocomplete can insert the operator, so the extra character costs little.

## Symbolic qualifiers

Some qualifiers naturally select from a vocabulary rather than perform an open-ended comparison:

```text
is:equipped
quality:hq
location:armoury
slot:mainhand
job:paladin
retainer:Belladonna
```

For these, `qualifier:value` should mean exact symbolic selection. The value is resolved through that qualifier’s registered canonical names and aliases.

Explicit equality may remain accepted:

```text
location:=armoury
```

But autocomplete should prefer the cleaner symbolic form:

```text
location:armoury
```

This is not the old ambiguity between `:` and `=`. The colon still only separates the qualifier; the qualifier schema defines a symbolic specifier as an exact member selection.

## `is:` as a deliberate namespace

`is:` should contain stable boolean or state-like predicates:

```text
is:equipped
is:tradable
is:unique
is:owned
is:hq
```

The important boundary is conceptual, not architectural. These predicates must not be assigned according to whichever module currently implements them. They belong under `is:` only when users naturally read them as “this item is X.”

This also permits symmetric negation:

```text
-is:equipped
-is:tradable
```

`is:hq` could be a friendly alias for `quality:hq`, but aliases need one documented canonical destination. Autocomplete should avoid presenting both as competing primary representations.

Evidence availability could eventually receive a parallel namespace:

```text
has:price
has:condition
-has:price
```

That reads more naturally than `known(offer.price)` while preserving the existing three-valued evidence model. It should be considered separately rather than smuggled into the first grammar migration.

## Negation

The preferred shorthand should negate a complete structured term:

```text
-location:saddlebag
-is:equipped
-name:~=darksteel
```

The parser should not treat every leading hyphen as negation of bare text. Hyphens occur in legitimate names, and allowing `-darksteel` to change selection semantics weakens the “structured syntax is intentional” rule.

For compound expressions:

```text
NOT (location:saddlebag OR location:armoury)
```

Unary `!` should not be taught. It competes visually and lexically with `!=`, while `-qualifier:value` and `NOT (...)` cover the ergonomic and explicit cases cleanly.

## Autocomplete behavior

Autocomplete becomes much easier to explain under this grammar:

1. At an empty token, suggest qualifiers such as `is:`, `location:`, `quality:`, and `quantity:`.
2. After `is:`, suggest only registered state predicates.
3. After `location:`, suggest only semantic storage locations.
4. After `quantity:`, suggest numeric operators before accepting a value.
5. After `name:`, suggest `~=` and `=` with short semantic descriptions.
6. When the user types `-loc`, complete it to `-location:` without replacing the negation.
7. Canonical dotted paths appear only when a short qualifier is genuinely ambiguous.

The parser, autocomplete, generated reference, and formatter must use one shared alias-resolution authority. An alias must always outrank another field’s coincidental leaf name, and a short name must never be advertised if it resolves elsewhere.

## Compatibility consequences

### Queries that remain naturally compatible

These already resemble the proposed language:

```text
quality:HQ
location:retainer
-location:saddlebag
retainer:Belladonna
```

Their parser representation may change—colon becomes a delimiter rather than a match operator—but their user-visible meaning can remain identical.

### Ordered comparisons acquire a colon

Current:

```text
quantity>=20
condition<100
price<=2500
```

Proposed canonical form:

```text
quantity:>=20
condition:<100
price:<=2500
```

The old syntax can remain accepted indefinitely as legacy syntax. Autocomplete and formatting should emit the new form, but persisted filters must not be rewritten unless semantic equivalence is proven.

### Bare booleans are a breaking semantic change

Current:

```text
equipped
-equipped
```

Proposed:

```text
is:equipped
-is:equipped
```

Silently changing `equipped` from a predicate into text search would alter saved queries. Migration therefore needs an explicit policy:

- Existing persisted filters can be parsed under a legacy grammar version and migrated to `is:equipped`.
- New interactive input follows the intentional grammar.
- URLs or unsaved transient searches may accept legacy syntax temporarily and offer the canonical replacement.
- No expression should switch meaning merely because additional fields become available.

### Existing colon-match text filters need mapping

If `item:darksteel` currently means field-specific matching, the new equivalents are:

```text
name:~=darksteel
```

or simply:

```text
darksteel
```

The compatibility parser could preserve `item:darksteel` as a qualifier-defined legacy default, but documentation should stop teaching an invisible difference between `item:value` and `item=value`.

### `~=` is new syntax

The tokenizer currently has no established meaning for `~`. Introducing `~=` is unambiguous, but standalone `~` should produce a focused diagnostic such as “Use `~=` for text matching.”

A future regex operator should not reuse `~=`. If regex is ever warranted, it deserves separate, unmistakable syntax and explicit opt-in.

### Unknown evidence must remain three-valued

For a missing price:

```text
price:!=2500
```

must not evaluate true merely because the price is unknown. Exact inequality, ordered comparisons, matching, and their negations should all preserve unknown evidence. Queries that intentionally test evidence should use the eventual `has:` vocabulary or the existing `known(...)` functions.

### Aliases must be stable across surfaces

A qualifier alias cannot mean `instance.quantity` in Inventory and silently become `offer.quantity` in Listings. The choices are:

- Give the concepts distinct stable public names, such as `quantity:` and `offerquantity:`.
- Require qualified names when both coexist.
- Reject the ambiguous query and offer both replacements.

Context may determine availability, but it must never change identity.

## Decisions requiring approval

1. **Colon requirement:** Whether every structured filter must contain `:`, with operator-only forms such as `quantity>20` retained solely for compatibility.
2. **Bare boolean migration:** Whether `equipped` becomes pure text search and `is:equipped` becomes the only canonical predicate.
3. **Numeric shorthand:** Whether `quantity:20` is accepted as equality or users must write `quantity:=20`.
4. **Text-match semantics:** Whether `~=` means normalized substring matching, token matching, alias matching, or some explicitly bounded combination.
5. **Symbolic equality:** Whether `location:armoury` is the preferred exact form while `location:=armoury` remains an optional explicit equivalent.
6. **Negation boundary:** Whether `-` is restricted to structured predicates and bare-text exclusion requires an explicit `name:` predicate.
7. **Boolean vocabulary:** Which predicates belong canonically under `is:` and which remain dedicated qualifiers such as `quality:`.
8. **Evidence vocabulary:** Whether `has:`/`-has:` should eventually replace user-facing `known(...)`/`unknown(...)`.
9. **Legacy lifetime:** Whether old syntax remains permanently accepted, receives deprecation diagnostics, or is versioned and migrated in persisted filters.
10. **Canonical paths:** Whether dotted paths remain documented advanced syntax or become diagnostics-only implementation vocabulary.

No implementation or repository changes were made for this analysis.

