# Recipe ingredient roles and a shared combobox

Status: implemented (2026-08-04).

## Why

`/recipes` had one lever over what Claude cooked with: a **"Use these up"**
checkbox grid listing the *entire* inventory, grouped by category. It did not
survive a real kitchen — every row on screen at once, unfiltered — and it could
only ever express one wish, never the opposite ("not that, I'm sick of it").

This replaces the grid with the fuzzy-matching combobox the Inventory page
already had, and widens `mustUse` into three roles.

## The roles

The generator returns 2–3 recipes as **alternative choices for one meal** — the
household cooks exactly one of them. That framing is what makes the semantics
fall out:

| Role | Meaning |
| --- | --- |
| **Use up** | In **every** recipe, and each recipe consumes the whole stocked amount |
| **Include** | In **every** recipe, any quantity |
| **Exclude** | In **no** recipe, in any form |

"In every one" rather than "in at least one" because the household picks one
recipe: a constraint honoured in only one of three is a coin flip. Use up is the
stronger of the first two — a later PR will have MCP Claude deduct those rows
from the inventory after a confirmation step. **No groundwork for that here**:
no column, no migration, nothing speculative.

## Decisions

- **Picks are inventory-only.** Keeps the prune-on-inventory-change rule
  working, and keeps an exclusion checkable against a real row. Things the
  kitchen doesn't stock go in the brief instead.
- **One item, one role.** The page holds `Dictionary<string, IngredientRole>`,
  so picking something already picked *moves* it. The suggestion row says
  "currently Use up" when that is about to happen.
- **The role selector is sticky.** Filing three exclusions is three picks, not
  six clicks. It is the one piece of modal state on the page, and it is
  labelled ("Add as") and always visible.
- **The combobox is a shared component.** `Components/Shared/IngredientCombobox.razor`
  owns the widget — input, listbox, highlight, aria, `@onmousedown:preventDefault`,
  blur dismissal. Each page owns what a match *means*. `Inventory.razor.css`
  had anticipated the move in a comment since the matching work landed.
- **Element ids derive from an `Id` parameter.** Two comboboxes on one page
  would otherwise put duplicate ids in the document and aim both boxes'
  `aria-activedescendant` at the same rows.
- **Exact matches are composed in at the Recipes call site**, not switched on
  with a flag. `IngredientMatcher.Suggest` skips an exact match for a reason
  that belongs to *Inventory's* Enter rule ("Enter adds what I typed"); a
  parameter would bake "which page is asking" into a pure function. Recipes
  prepends `ExactMatch` itself, because an exactly-typed name is the likeliest
  pick and inheriting the rule would make it the one thing you cannot choose.
- **A free-text brief.** `MealType` is an enum precisely to avoid a second
  injection surface, so the brief owes an argument: `--json-schema` pins the
  answer's shape, AGENTS.md's measured results bound the blast radius at exactly
  this length (500 chars), and the prompt's data-not-instructions paragraph is
  widened to name it. It is clamped in `ClaudeRecipeGenerator`, not at the
  textarea — `maxlength` is a convenience, the same way `[MaxLength]` is for EF
  while SQLite ignores it.
- **Delete asks first.** A saved recipe was one misclick from gone under a `✕` —
  the bug the inventory table's draft editor exists to close. The trash (`🗑`,
  never `✕`, which means "cancel" everywhere else) arms one row at a time, held
  in an `int?` field rather than in the DOM, and focus moves to Confirm and back
  to the trash on Cancel. No `window.confirm`: `PageHarness` runs bUnit in
  Strict JSInterop mode and an unmatched call throws.

## Verifying an exclusion

`ParseRecipes` already refuses to take the model's word for "have". Exclusion
gets the same treatment from the other side: a recipe whose ingredient *claims*
an excluded inventory row is dropped **whole** — stripping one ingredient would
leave the steps still calling for it, and with alternatives, losing one still
leaves two. The counts stay separate in `problem` because "the model ignored the
exclusion" says the prompt is not landing, which is different news from
malformed JSON.

**What it cannot catch, and the prompt carries alone:** a paraphrase ("petits
pois" for an excluded "Peas") and a mention buried in a step's prose. A
substring scan there false-positives instantly — "pea" is inside "peanut",
"peach" and "appears" — and nothing separates a synonym from an unrelated
ingredient without another model call. A claim is checkable because the model
said which row it meant; prose is not.
`A_paraphrase_of_an_excluded_row_is_not_caught` asserts the paraphrase
**survives**, and says in a comment that it cannot be broken to prove it bites.

## Signals that are not colour

Three roles, and Bootstrap's `.active` on an outline button is a fill swap —
state by colour alone. So: the row label names the role, the chip carries its
own shape (**Use up** an inset bar, **Include** plain, **Exclude** a dashed edge
plus a struck-through name), each `✕`'s `aria-label` names the role, and the
selector carries `aria-pressed`. Exclude gets the strikethrough deliberately —
it is the one that has to survive greyscale and a red/green deficit.

## Verified in a browser (2026-08-04)

Chrome on another machine against `0.0.0.0:5299`, real clicks and keypresses:

- typing opens the list; a **mouse click on a row lands** (what
  `@onmousedown:preventDefault` is for, and the one thing bUnit cannot see);
  arrows cycle −1 → 0 → 1 → −1; Enter picks; the box clears and keeps focus;
- the same still holds on `/inventory` after the extraction;
- the sticky role survives several picks; picking a row held under another role
  moves it, and the row said "currently Use up" first;
- one real generation with **Use up: Chilli oil**, **Exclude: Peas**, brief
  "something warming": all three recipes listed Chilli oil at *"half a bottle
  (all of it)"*, none contained peas, and all three descriptions were warming
  ones. 42s, three recipes;
- arming a delete moves focus to Confirm; Cancel puts it back on the trash;
- every new element's colours follow the theme (chip `#e9ecef` → `#343a40`,
  text inverts, label follows).

**Two traps worth writing down**, both of which produced confident wrong
answers before being run down:

- **a stale `dotnet run` looks exactly like a broken feature.** The first server
  instance served markup from the new build but behaved as though `@bind-Value:after`
  never fired — no suggestions, `Add` stuck disabled. Restarting fixed it with
  no code change. Restart before believing an interaction is broken;
- **reading the DOM straight after a keypress races the WebSocket round trip.**
  `ArrowDown` then an immediate `aria-activedescendant` read returns the state
  *before* the patch, which reads as "arrow keys don't work". Wait between the
  key and the read.

## Known, not fixed here

`.role-choice.active`'s inset bar sits at **1.04:1** against Bootstrap's
`btn-outline-secondary` active fill — effectively invisible. It is copied
faithfully from `ThemeToggle`'s `.theme-choice.active`, which measures the same
and has since dark mode landed, so this is inherited rather than introduced.
`font-weight: 600` and `aria-pressed` are what actually carry the state on both.
Worth fixing in both places at once, not one of them.

## Out of scope

Recipe-count knob; chat surface; shopping lists from missing ingredients;
persisting which rows a saved recipe was meant to finish; live cross-circuit
sync of the saved list.
