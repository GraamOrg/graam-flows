# Excess spread covering losses

How excess spread meets losses is stated in two step fields. Neither is on unless stated.

```json
"executionOrder": ["INTEREST", "PRINCIPAL_SCHEDULED", "PRINCIPAL_UNSCHEDULED", "PRINCIPAL_RECOVERY",
                   "EXCESS_TURBO", "WRITEDOWN", "EXCESS_RELEASE"],
"steps": [
  { "type": "EXCESS_TURBO", "target": 0 },
  { "type": "WRITEDOWN", "size": "overPool", "structure": { "type": "SEQ", "tranches": ["B", "A"] } },
  { "type": "EXCESS_RELEASE", "structure": { "type": "SEQ", "tranches": ["XS"] } }
]
```

- `EXCESS_TURBO` with `target: 0` pays the notes principal out of the interest the steps ahead of it left, until the funded classes are down to the pool. It pays as recovery principal, through the recovery-principal structure as that period's rules leave it, which is what the built-in excess-spread rule pays through; it states no structure of its own (`structure`, `default`, `rules`, `useStructure` or `onTriggerFail`) and is refused if it does. It spends what the structure reports it paid, so what the classes cannot take stays with the interest and goes on to the next step. Zero is the only value; an overcollateralization target is stated in `ocTarget`, and a step states one or the other.
- `WRITEDOWN` with `size: "overPool"` writes down what the funded classes still stand above the pool by after the steps ahead of it, in place of the period's loss. `size: "periodLoss"` (defaulted principal less recoveries) is the default.

A loss smaller than the period's excess writes nothing down and the release step pays the excess less the loss. A larger loss writes down only the remainder. A deal whose documents pay the excess-spread class ahead of any loss cover states neither field and names a release step in its order: the notes take the period's loss and the release step pays the whole excess. (With neither field and no excess step in the order, the built-in excess-spread rule still runs.)

A deal that states either field is checked three ways, because a mis-stated deal still runs and returns a plausible grid with principal or a loss counted wrongly.

**1. The execution order.** An order can be wrong three ways (a step is missing, a step is named twice, a step is out of place), and the rule has a part for each, all read off what each step does to the notes:

| What the step does to the notes | Steps |
|---|---|
| Pays them from the period's collections | `INTEREST`, `PRINCIPAL_SCHEDULED`, `PRINCIPAL_UNSCHEDULED`, `PRINCIPAL_RECOVERY`, `SUPPLEMENTAL_REDUCTION`, `ACCRUAL`, `MODIFICATION_LOSS` |
| Pays them principal out of the interest left | `EXCESS_TURBO` |
| Writes them down | `WRITEDOWN` |
| Writes them back up to the pool (needs an `overPool` write-down ahead of it) | `WRITEUP` |
| Pays out all the interest left | `EXCESS`, `EXCESS_RELEASE` (one step under two names) |
| Never pays them principal | `EXPENSE`, `RESERVE_DEPOSIT`, `CAP_CARRYOVER` (which pays them carried-over interest), `PREPAYMENT_PENALTY` (which pays the collateral's prepayment penalties as interest) |

- **Presence.** The order names `INTEREST` and the three `PRINCIPAL_*` steps. A step that states a field is in the order too: `WRITEDOWN` for `overPool`, `EXCESS_TURBO` for `target: 0`.
- **Count.** No step in the first four rows is named twice.
- **Position.** The steps that pay the notes from collections run first, then `EXCESS_TURBO`. No step that pays the notes runs after an `overPool` write-down or after the release.

Steps that never pay the notes principal sit anywhere, any number of times; one placed after the turbo or the release is paid out of what those left. The default execution order runs `EXCESS_TURBO` after `WRITEDOWN`, so a deal using `overPool` states its order.

**2. The deal's shape.** The fields are supported on the shape they are tested on, and a deal outside it is refused as not supported with these fields:

| Not supported | Why |
|---|---|
| An interleaved `waterfallOrder` | `INTEREST` pays the principal through the principal structures' children, and what it cannot place is left unpaid. |
| No structure for unscheduled or for recovery principal | The step returns without paying, and the notes stand above the pool by the principal it did not pay. |
| More than one collateral group | The notes are measured against one group's pool. |
| Deferrable notes | Deferred interest is added to a note's balance and would be written down the month it is capitalised. |
| A forbearance balance in the collateral | The pool's balance leaves it out, so notes sized to include it would be written down by it. |
| `firstPeriodCollateralPolicy: Drop` with a period to drop | The dropped principal leaves the pool and never reaches the notes. |
| A zero-target turbo with a structure of its own | It pays through the recovery structure, so its own would be ignored. |
| `overPool` with a `MODIFICATION_LOSS` step | That step reserves capacity for the write-down before `INTEREST`. |
| A zero-target turbo ahead of a `periodLoss` write-down | The loss would be paid as principal and written down as well. |

**3. What the two measuring steps find as they run.** Whatever the order and the structures are:

- A zero-target turbo and an `overPool` write-down stop the run if the period's principal is collected and not paid while the notes still have a balance (the pool fell by that principal and the notes did not, so it would be read as a loss), or if the principal steps paid out more principal than the period collected (the notes would stand under the pool by cash the period does not have).
- A zero-target turbo stops the run if it pays a class that is not a note, records a negative payment to a note (a structure that nets between classes), or if the structure pays out more than the turbo gave it (a forced paydown; a tranche filed under another class). A recovery structure may name a class that is not a note, a zero-balance residual say, as long as the turbo never pays it.
- An `overPool` write-down stops the run if it writes down a class that is not a note (an IO class heading the write-down order): the notes would still stand above the pool and the same loss would be written down again.
- Each check works to one unit, not one cent, and each principal total (scheduled, unscheduled, recovery) is held to it on its own: a pro-rata split drops up to a unit when its last class retires, each total has its own split, and sub-cent payments are not recorded.

**Limits.**

- Notes that start above the pool are written down by the gap in the first period (less what excess covers). The engine cannot tell an earlier loss from balances that left something out.
- With only one of the two fields stated, the order rule is stricter than it needs to be: for example `overPool` alone refuses principal steps after the release, and an `EXCESS_TURBO` with no target and no OC target is still placed as if it paid. State the canonical order.
- A waterfall request carries the period's forbearance flows and not the accumulated balance, so the forbearance refusal sees a standing balance only on collateral the engine built itself.

A deal that states neither field is not checked.
