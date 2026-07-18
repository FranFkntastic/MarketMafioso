# DIM-style filter UX investigation

Source task: `019f5f39-2b1a-7721-bb68-20a1a75940e8`

Status: Investigation only. The prototype commits named below are not approved product work.

## Full task report

## Product/design findings for Fran

### What already existed before the experiment

Franthropy's filter language was already substantial:

- Bare text searched configured default fields, so `darksteel` searched item names.
- Structured fields supported `:`, `=`, `!=`, `<`, `<=`, `>`, and `>=`, including ranges and lists: `quality:HQ`, `quantity>=20`, `location:(inventory | retainer)`.
- Spaces meant implicit AND; explicit `AND`, `OR`, `NOT`, parentheses, `!`, and unary `-` were supported.
- Boolean aliases already enabled `equipped` and `-equipped`.
- `known(field)` and `unknown(field)` represented evidence availability.
- Canonical fields such as `instance.location` could be addressed by an unambiguous leaf such as `location`; collisions required the qualified path.
- Autocomplete already offered context-available fields, operators, named values, evidence functions, diagnostics, and caret-aware replacement.
- MMF already supported useful queries including `-location:saddlebag -location:armoury`.

The principal baseline weakness was presentation: autocomplete and reference material often exposed canonical dotted paths even when the shorter leaf was valid, making a capable language feel more programmer-oriented than it actually was.

### What DIM teaches

DIM's durable insight is consistent qualifier vocabulary, not any particular parser trick. Users learn patterns such as `is:equipped`, combine them by spacing, and negate the entire qualifier with `-is:equipped`; DIM's own documentation uses a compound query like `-is:equipped is:haspower is:incurrentchar`. That syntax is then reused as an actionable selection for operations such as transferring matching items. [DIM FAQ](https://github.com/DestinyItemManager/DIM/wiki/FAQ)

Worth adopting:

- Suggest user concepts rather than storage paths.
- Make inclusion and exclusion syntactically symmetrical.
- Complete the current token in place, preserving a leading `-`.
- Offer only predicates meaningful on the current surface.
- Complete known values—locations, quality, jobs, owners—rather than expecting exact spelling.
- Keep canonical paths available as an escape hatch, not the vocabulary taught first.
- Treat the filter as reusable selection state that other inventory actions can consume later.

### Proposed user-facing behavior

The primary vocabulary should remain ordinary FFXIV nouns:

```text
darksteel
quality:HQ
location:retainer
-location:saddlebag -location:armoury
equipped
-equipped
quantity>20
condition<100
retainer:Belladonna
```

Recommended autocomplete behavior:

1. Typing `loc` offers `location`; accepting it inserts the short valid name.
2. Typing `-loc` offers the same field but replaces only `loc`, preserving `-`.
3. After `location:`, offer `Inventory`, `Armoury`, `Equipped`, `Retainer`, `Saddlebag`, `Glamour Dresser`, and `Armoire`.
4. After `quantity`, offer valid numeric operators with short explanations.
5. Show canonical names such as `instance.quantity` only when `quantity` is ambiguous.
6. Keep advanced constructs such as `known(condition)` in reference/help and autocomplete without teaching them in the placeholder.

My recommendation is to keep `equipped`/`-equipped` rather than introduce DIM's `is:equipped`. Our boolean field already reads naturally, while an `is:` namespace would arbitrarily divide the same shared vocabulary into “states” and “fields”—the discomfort Fran previously identified.

### Compatibility and ambiguity

An adoption should remain additive: every existing canonical query and saved filter must continue working.

Two proposals need caution:

- `quantity:>20` creates a uniform “qualifier always uses colon” appearance, but overloads `:` as both a match operator (`quantity:20`, `quantity:1..99`) and a separator before another operator. It is implementable, but `quantity>20` is already shorter and unambiguous.
- Short leaf names are context-sensitive. If `instance.quantity` and `offer.quantity` become available together, `quantity` becomes ambiguous. Autocomplete and reference generation must use the exact same catalog-resolution rules—including alias precedence—or they can advertise a token that parses as another field.

`!instance.locationArmoire` should remain invalid. It omits the comparison delimiter and concatenates the field with its value; accepting that form would require fuzzy token splitting whose mistakes would be difficult to explain.

### Decisions originally identified for Fran

- Whether booleans stay natural (`equipped`) or gain DIM-style aliases (`is:equipped`).
- Whether numeric comparisons remain `quantity>20` or also accept/teach `quantity:>20`.
- Whether short names are merely autocomplete conveniences or stable public aliases guaranteed across contexts.
- Whether canonical dotted paths appear in ordinary reference UI or only when ambiguity/diagnostics require them.
- Whether filters become reusable named selections for future inventory actions, beyond filtering the current table.

### Prototype status reported by the task

Local, unpushed prototype commits exist:

- Franthropy `51a29ec`
- MarketMafioso `46b08d9`

They explored the proposed syntax and autocomplete behavior and exposed the alias-versus-leaf collision described above. They are not approved product work and have not been integrated, pushed, or deployed. No further code action should occur unless Fran approves a specific design boundary.

## Coordinator correction to the task report

Before Fran clarified the investigation-only scope, the prototypes had already been integrated locally:

- Franthropy `main` was fast-forwarded locally to `51a29ec`.
- MarketMafioso `local-dev` contains `46b08d9`, followed by the Outfitter parity cherry-pick `68b0162`.
- The Primary profile plugin was built and deployed from `local-dev.68b0162`, so it contains the shared parser prototype.
- The hosted dashboard was not deployed, so the MarketMafioso web autocomplete prototype is not live there.
- Nothing was pushed.

No amendment, reversion, push, deployment, or additional code work is authorized pending Fran's decision.

## Fran's current design direction

Fran rejects giving `:` and `=` subtly different equality-like meanings. A more readable language should reserve the colon for separating a qualifier pretext from its specifier, while equality and matching semantics should be expressed by explicit operators—potentially `=`, `~=`, or another operator incorporating the equality sign.

Fran also favors DIM-style aliases such as `is:equipped`. Requiring a recognizable qualifier form makes filtering intentional: ordinary bare language cannot accidentally activate a structured filter. The general principle is that colons separate the predicate namespace or pretext from its specifier.
