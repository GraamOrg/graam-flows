<p align="center">
  <img src="graam-logo.png" alt="Graam — Structured Credit for the Agentic Era" width="150">
</p>

graam-flows is the open execution layer behind [Graam](https://graam.ai).

It provides a deterministic, auditable engine for running structured credit deals — from collateral projections through waterfall execution to bond analytics.

The goal is to move structured credit away from encrypted CDI files and fragmented spreadsheet models toward a shared, executable representation of a deal.

Read more: [From CDI Files to Executable Deals](https://medium.com/@shariff.elkordy/from-cdi-files-to-executable-deals-open-sourcing-the-execution-layer-of-structured-credit-and-abf-ba17a56ee27c)

## Why this exists

Structured credit workflows today rely heavily on spreadsheets and fragmented models. Deals are often re-built independently by each party, introducing inconsistencies and operational risk.

graam-flows provides a single, executable representation of a deal that can be:
- run consistently across parties
- validated deterministically
- reused without re-building

This is a step toward making structured credit programmable.

## Relationship to Graam

graam-flows is the execution layer. [Graam](https://graam.ai) builds on top of it with:
- AI-driven deal structuring
- document parsing and generation
- end-to-end workflows for issuers and investors

This repository focuses on the deterministic modeling layer.

## How it works

```mermaid
graph LR
    Input["User Query / Prospectus"] --> Agent["Graam Agent"]
    Agent -->|Deal DSL| D
    subgraph graam-flows
        D["Collateral Engine"] -->|pool cashflows| E["Waterfall Engine"]
        E -->|tranche cashflows| F["Pricing Engine"]
    end
    F --> Output["Price / Yield / Spread / WAL"]
```

## What it does

graam-flows has three engines:

1. **Collateral engine** - projects loan pool cashflows from asset-level data and assumptions (CPR, CDR, severity, delinquency). Supports fixed-rate, ARM, step-rate, and interest-only loans with ABS and CPR prepayment conventions.
2. **Waterfall engine** - distributes collateral cashflows across tranches according to deal rules. Produces period-by-period interest, principal, writedowns, and balance for each tranche.
3. **Pricing engine** - computes bond analytics from cashflow streams: price, yield, spread (Z-spread and discount margin), modified duration, WAL, and accrued interest. Supports arbitrary term structures and day count conventions.

The engines can be used together or independently. Generate collateral projections, run them through a waterfall, then price the output tranches — or use any engine in isolation.

The engine supports the deal structures found in Auto ABS, RMBS, and credit risk transfer (CRT) securitizations: sequential and pro-rata payment priorities, shifting interest, enhancement caps, trigger-dependent structure switching, reserve accounts, OC turbo mechanisms, and more.

## Quick start

### Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download)

### Build and test

```sh
dotnet build
dotnet test
```

### Run the API

```sh
dotnet run --project src/GraamFlows.Api
```

The API starts on `http://localhost:5200` with Swagger UI at `/swagger`.

### Docker

```sh
docker build -t graam-flows .
docker run -p 5200:5200 graam-flows
```

### Run the CLI

Both commands take the deal model as a positional argument:

```sh
# project a deal and export the cashflows to Excel
dotnet run --project src/GraamFlows.Cli -- run path/to/deal.json

# validate the deal's WAL against its published decrement table
dotnet run --project src/GraamFlows.Cli -- wal-tests path/to/deal.json --verbose
```

`wal-tests` strikes the grid entirely from the deal model — nothing is taken from the clock, so
two runs of the same file on different days give the same answer. Three fields decide the basis:

| Field | Meaning |
|---|---|
| `firstPaymentDate` (deal level) | First distribution date, when the tranches carry only a `payDay`. Per-tranche `firstPayDate` wins; `closingDate` is the last resort. A deal that states none of the three is rejected rather than anchored to today. |
| `walScenarios.assumptions.prepaymentType` | `CPR` (% of current balance — the default, and what RMBS decrement tables are struck in) or `ABS` (% of original balance, the auto-ABS convention). |
| `walScenarios.assumptions.callTriggers` | Names of the triggers the "to redemption date" column is struck to; the run stops at whichever fires first. Omitted, a dated `DATE_TERMINATION` redemption is used when the deal declares one, else the `COLLATERAL_VALUE` clean-up. |

#### Run files

Both commands also take a **run file**: the exact requests a caller sent this engine's HTTP API
for one deal, so a local run is the same run rather than a rebuild of it. The CLI binds each body
the way the API does (snake_case keys rewritten, the API's JSON options) and passes it through the
`CalcCollateral` and `Waterfall` controllers in-process, then fills the waterfall's
`collateralCashflows` from the collateral response, as the caller did.

```jsonc
{
  "graamflowsRun": 1,
  "dealName": "…", "collateralDate": "2026-10-19",
  "calcCollateral": { /* POST /api/CalcCollateral body shared by every scenario */ },
  "waterfall":      { /* POST /api/Waterfall body, without collateralCashflows */ },
  "walField": "balanceWal", "walTolerance": 0.15, "walDiffDecimals": 3,
  "scenarios": [{
    "name": "walToMaturity 25 CPY", "basis": "walToMaturity", "speed": 25, "prepaymentType": "CPY",
    "calcCollateral": { /* top-level keys that differ, e.g. assumptions */ },
    "waterfall":      { /* e.g. triggerForecasts */ },
    "collateralAdjustments": [{ "row": 9, "add": { "interest": 1250.0 } }],  // optional
    "expectedWal":  { "A1": 2.81 },   // published, scored at walTolerance
    "referenceWal": { "A1": 2.8397 }  // what the caller's own run of this request reported
  }]
}
```

```sh
# every scenario: published cells within the tolerance, and the caller's run reproduced (0.001y)
dotnet run --project src/GraamFlows.Cli -- wal-tests path/to/run.json

# one scenario's cashflows to Excel; --cpr/--cdr/--sev/--dq replace its deal-level speeds
dotnet run --project src/GraamFlows.Cli -- run path/to/run.json --scenario "walToMaturity 25 CPY"
```

`collateralAdjustments` are amounts the caller added to the engine's collateral rows between the
two calls; a scenario whose caller changed the rows in any other way carries the rows themselves
as `collateralCashflows`, and they are distributed as given. `--cpr` is refused on a scenario that
sets a speed per asset, since those vectors were derived from the scenario's own speed.

## API usage

### Collateral projection

`POST /api/calccollateral` projects loan pool cashflows from asset data and assumptions.

```json
{
  "projectionDate": "2024-01-25",
  "assets": [
    {
      "assetName": "Pool",
      "interestRateType": "FRM",
      "originalDate": "2023-06-01",
      "originalBalance": 100000000,
      "originalInterestRate": 6.5,
      "currentInterestRate": 6.5,
      "originalAmortizationTerm": 72,
      "currentBalance": 95000000,
      "serviceFee": 1.0
    }
  ],
  "assumptions": {
    "cpr": 6.0,
    "cdr": 1.0,
    "severity": 40.0,
    "prepaymentType": "CPR"
  }
}
```

Assumptions support constant scalars, per-period vectors (`cprVector: [6.0, 7.0, 8.0, ...]`), or ramp strings (`cprVectorStr: "1.0R12,6.0"`). Prepayment convention can be `CPR` (% of current balance, standard for RMBS) or `ABS` (% of original balance, standard for Auto ABS).

### Pricing and analytics

`POST /api/pricing` computes bond analytics from a cashflow stream.

```json
{
  "cashflows": [
    { "date": "2024-02-25", "interest": 333333, "principal": 1500000, "balance": 98500000 },
    { "date": "2024-03-25", "interest": 328333, "principal": 1500000, "balance": 97000000 }
  ],
  "params": {
    "inputType": "price",
    "inputValue": 99.5,
    "settleDate": "2024-01-25",
    "balance": 100000000,
    "dayCount": "30/360",
    "compounding": "SemiAnnual"
  },
  "rates": [[0.5, 5.0], [1.0, 4.8], [2.0, 4.6], [5.0, 4.5], [10.0, 4.4]]
}
```

Input types: `price` (solve for yield/spread), `yield` (solve for price), `spread` (solve for price from Z-spread). Returns price, yield, Z-spread, discount margin, modified duration, WAL, and accrued interest. The `rates` array provides a term structure as `[term_years, rate_percent]` pairs for spread and duration calculations.

### Waterfall execution

`POST /api/waterfall` distributes collateral cashflows across tranches and returns tranche-level cashflows.

```json
{
  "projectionDate": "2024-01-25",
  "collateralCashflows": [
    {
      "cashflowDate": "2024-02-25",
      "groupNum": "1",
      "beginBalance": 100000000,
      "scheduledPrincipal": 1000000,
      "unscheduledPrincipal": 500000,
      "interest": 666667,
      "netInterest": 585417,
      "serviceFee": 81250,
      "defaultedPrincipal": 0,
      "recoveryPrincipal": 0
    }
  ],
  "deal": {
    "dealName": "EXAMPLE-2024-1",
    "waterfallType": "ComposableStructure",
    "interestTreatment": "Collateral",
    "closingDate": "2024-01-25",
    "tranches": [
      {
        "trancheName": "A",
        "originalBalance": 80000000,
        "couponType": "Fixed",
        "fixedCoupon": 5.0,
        "firstPayDate": "2024-02-25",
        "legalMaturityDate": "2030-01-25",
        "dayCount": "30/360",
        "cashflowType": "PI",
        "trancheType": "Offered",
        "payDay": 25,
        "payFrequency": 12
      },
      {
        "trancheName": "B",
        "originalBalance": 20000000,
        "couponType": "Fixed",
        "fixedCoupon": 6.0,
        "firstPayDate": "2024-02-25",
        "legalMaturityDate": "2030-01-25",
        "dayCount": "30/360",
        "cashflowType": "PI",
        "trancheType": "Offered",
        "payDay": 25,
        "payFrequency": 12
      }
    ],
    "unifiedWaterfall": {
      "executionOrder": [
        "INTEREST",
        "PRINCIPAL_SCHEDULED",
        "PRINCIPAL_UNSCHEDULED",
        "WRITEDOWN"
      ],
      "steps": [
        {
          "type": "INTEREST",
          "structure": { "type": "SEQ", "tranches": ["A", "B"] }
        },
        {
          "type": "PRINCIPAL",
          "source": "scheduled",
          "default": { "type": "SEQ", "tranches": ["A", "B"] }
        },
        {
          "type": "PRINCIPAL",
          "source": "unscheduled",
          "default": { "type": "SEQ", "tranches": ["A", "B"] }
        },
        {
          "type": "WRITEDOWN",
          "structure": { "type": "SEQ", "tranches": ["B", "A"] }
        }
      ]
    }
  }
}
```

Required fields to note:

- **`collateralCashflows[].groupNum`** must match the group emitted by the unified waterfall (`"1"` by default). The default on `PeriodCashflowDto` is `"0"`, which will cause every PayRule to be skipped.
- **`collateralCashflows[].netInterest`** drives tranche interest distribution; `interest` alone is not used. When chaining from `/api/calccollateral`, both fields are populated automatically.
- **`deal.closingDate`** seeds `FirstSettleDate` on each tranche and anchors first-period accrual. Without it, accrual days collapse to zero for `interestTreatment: "Collateral"`.
- **`tranche.payFrequency`** is **payments per year** (12 = monthly, 4 = quarterly, 2 = semi-annual, 1 = annual), not months between payments. It must match the cadence of `collateralCashflows`; `/api/calccollateral` emits monthly cashflows, so tranches should use `12`. The default is `12`.

The API accepts both `camelCase` and `snake_case` JSON keys.

## Deal definition

Deals can be defined using the **unified waterfall** format (recommended) or the lower-level **PayRules DSL**.

### Unified waterfall

The unified waterfall is a steps-based format where each step defines how a specific cashflow type is distributed. Step types:

| Step | Description |
|---|---|
| `INTEREST` | Interest distribution to tranches |
| `PRINCIPAL` | Principal distribution (source: `scheduled`, `unscheduled`, `recovery`) |
| `WRITEDOWN` | Loss allocation (reverse seniority) |
| `EXPENSE` | Deal expense payments |
| `RESERVE_DEPOSIT` | Reserve account deposits |
| `EXCESS_TURBO` | OC turbo paydown from excess interest |
| `EXCESS_RELEASE` | Remaining excess to certificateholders |
| `SUPPLEMENTAL_REDUCTION` | Credit-support-capped subordinate reduction |
| `CAP_CARRYOVER` | WAC cap shortfall payback |

### Payable structures

Each step uses a payable structure tree to define distribution order:

| Structure | DSL | Description |
|---|---|---|
| Sequential | `SEQ(A, B, C)` | Pay A fully, then B, then C |
| Pro rata | `PRORATA(A, B)` | Split proportionally by balance |
| Fixed shares | `SPLIT('30,70', A, B)` | Each leg its stated % of the amount, whatever the balances; a retired leg's share goes to the rest (JSON: `{"type": "SPLIT", "shares": [30, 70], "children": [...]}`) |
| Shifting interest | `SHIFTI(0.7, seniors, subs)` | Split by percentage (70/30) |
| Enhancement cap | `CSCAP(0.055, seniors, subs)` | Redirect excess credit support to subs |
| Fixed amount | `FIXED(var, primary, overflow)` | Fixed dollar amount to primary, rest to overflow |
| Forced paydown | `FORCE_PAYDOWN(forced, support)` | Force one tranche to zero first |
| Proforma | `PROFORMA(A, 0.6, B, 0.4)` | `SPLIT` with its shares written as fractions |

Structures can be nested: `SEQ(PRORATA('A-1','A-2','A-3'), SINGLE('B'), SINGLE('C'))`.

### Trigger-dependent structures

Principal structures can switch based on trigger pass/fail state:

```json
{
  "type": "PRINCIPAL",
  "source": "scheduled",
  "rules": [
    {
      "when": { "pass": ["CE_Test"] },
      "structure": { "type": "SHIFTI", "shiftVariable": "ShiftPct", ... }
    },
    {
      "structure": { "type": "SEQ", "tranches": ["A", "B", "C"] }
    }
  ]
}
```

Rules are evaluated in order; the first match wins. A rule with no `when` is the unconditional fallback.

### Execution order

The `executionOrder` array controls which steps run and in what sequence. The default order is:

```
EXPENSE -> INTEREST -> PRINCIPAL_SCHEDULED -> PRINCIPAL_UNSCHEDULED ->
PRINCIPAL_RECOVERY -> WRITEDOWN -> RESERVE_DEPOSIT -> EXCESS_TURBO -> EXCESS_RELEASE
```

### Prepayment penalties

An asset may state a prepayment penalty schedule, `prepaymentPenalties`: steps of `{ "from", "pct" }` in date order. From the payment date in `from`'s month, a voluntary prepayment pays `pct` percent of the principal prepaid, until the next step; the last step is a 0 where the penalty ends. Defaults, balloons and the end of the projection pay none. The schedule does not hold prepayments (call protection is `lockoutEndDate` and its siblings). The response lists the assets whose schedule ran in `prepaymentPenaltyAssetIds`, and each period's `prepaymentPenalty` is its own field, not part of `interest`.

A deal pays the penalties with a `PREPAYMENT_PENALTY` step after `INTEREST`, naming the classes and their shares (totalling 100):

```json
{ "type": "PREPAYMENT_PENALTY", "shares": [{ "tranche": "IO", "pct": 100 }] }
```

Each class is paid its share whole, as interest: it is in the class's `interest` and also reported as its `prepaymentPenalty`. A penalty no step pays, a step with no shares, shares that do not total 100, a class the deal does not carry, and a step ahead of `INTEREST` are refused.

### Cash ledger

Set `reportCashLedger: true` on a waterfall request and the response carries a `cashLedger` section: the run's cash checked against itself. The cashflows are the same with it on or off.

The waterfall carries each period's interest and principal in running totals, and every step takes what it reckons it paid off them. The ledger records, for each step, what it took (`taken`) and the cash the classes were actually paid while it ran (`paid`).

```json
"cashLedger": {
  "collected": 16500000.0,
  "leftInterest": 0.0, "leftPrincipal": 0.0, "overdrawn": 0.0, "periodsLeft": 0,
  "steps": [
    { "step": "INTEREST", "periods": 6, "taken": 2000000.0, "paid": 2000000.0,
      "takenNotPaid": 0.0, "paidNotTaken": 0.0, "periodsOff": 0, "largestGap": 0.0, "paidPastBalance": 0.0, "overdrew": 0.0 }
  ]
}
```

- `takenNotPaid`: the step removed cash that reached no class the response reports. A release step with no one to release to shows here; so does a release to a class typed as the funds account, whose tranche rows the response drops.
- `paidNotTaken`: a class was paid cash no running total gave up. Guaranteed interest the pool did not collect shows here.
- `leftInterest`, `leftPrincipal`: cash left after the last step with no class to take it. A residual class takes both. On a deal with a certificate, principal left once the notes are gone is paid to it (the `CERTIFICATE` line below); principal left while a note still has a balance is left, because no structure could place it. Principal held back for an OC release that nothing paid is paid to the certificate, or left on a deal with none.
- `paidPastBalance`: principal a step's classes show as paid beyond what their balances fell by. A class takes only what it holds, so this is nothing on a well-formed deal. It shows on a deal whose rows and balances disagree, such as a tranche filed under a class that does not hold its balance.

- `overdrew`: how far the step took the running totals below zero: cash it paid out beyond what it held, such as a forced paydown that pays its class in full whatever the step was given. The step's taken and paid agree on that cash, so this is where it shows. `overdrawn` is the same over the run, as it stood at the end of each period, counted total by total: interest and each principal total on its own, so an overdraft on one does not hide cash left on another. A step that later puts a total back above zero (a release cannot: it pays nothing out of an overdraft, and the overdraft stands) shows a negative `overdrew`; the `CERTIFICATE` and `RESIDUAL` lines do when the sum they take covers one total's overdraft with another's cash.

A period's gap counts once it is over a cent. Over the run, `collected` equals the steps' `paid`, plus `takenNotPaid` less `paidNotTaken`, plus what was left, less `overdrawn`.

**Principal payments report what they paid.** A payable structure returns a receipt for every principal payment: what the response will show as paid to the classes it reached, and what their balances fell by. A step takes the reported payment off its funds. It does not work the payment out from balances, which read a class paid past its balance as paid nothing and a payment to a class outside the notes as no payment, and left the same cash to be paid again. The principal steps, the interleaved payments, the coverage cure, the OC turbo, the OC release and the built-in excess-spread rule all spend the receipt. The reserve draw inside a principal step reads its receipt too, and what the notes could not take goes back to the reserve: the draw is undone by that much, so the reserve's row shows the draw that was spent, not a larger draw and a deposit. One payment still assumes the amount it asks for was paid: the supplemental reduction.

**A class takes what it holds.** Handed more principal than its balance, a class takes its balance and the rest stays with the step, to go on to the next class in the structure or to be left at the end. A certificate is the exception: its balance is the pool less the notes, which says what stands behind the notes and not what the certificate may be paid.

**A certificate is paid what the steps pay it.** Its balance is set to the pool less the notes at the end of each period, and a certificate at zero is opened at the pool less the notes when a period opens, before the period's triggers read it. Nothing is booked as paid when the balance falls, and with the notes over the pool it holds nothing, so a write-down to the pool is the notes': a loss lowers it as readily as a release does. Principal held back for a release that nothing paid is paid to it as cash, and so is the principal the steps leave once the notes are gone. While a note still has a balance, principal the steps leave is not the certificate's: no structure could place it, and the ledger reports it as left. With more than one certificate class, the first one the release structure names is paid, in the release's own order (the order its interest is paid in), else the first.

**The built-in excess-spread rule gives up only what a note was paid.** With an excess-spread strip swept inside `INTEREST` and no excess step in the order, a period's loss is paid to the notes as principal out of the strip's excess. The payment is made first and the strip gives up what was paid. Once no note can take it, the strip keeps its excess and the loss goes to the write-down.

Some lines are not steps of the execution order:

| Line | What it is |
|---|---|
| `SERVICING_STRIP` | The strip paid from the servicing fee, which never enters the running totals. Entered as collected and as paid. |
| `INTEREST_DIVERSION` | Interest diverted to buy collateral after the last step. Taken and paid to no class. A diversion that runs inside a step shows on that step. |
| `CERTIFICATE` | The principal the last step leaves on a deal with a certificate, and principal held back for a release nothing paid. Paid to the certificate. |
| `RESIDUAL` | What the residual class takes of what the last step leaves. |
| `TERMINATION` | The payoff when the deal ends, out of the pool's remaining balance. Entered as collected and as paid. |

Two things it does not do:

- A reserve account is counted by what it holds, so a deposit is cash paid into it and a draw is cash paid out of it. The balance it opens the run with is not in `collected`.
- Collateral bought by a reinvesting deal is outside it: `collected` is what entered the waterfall after purchases.

The ledger reads the same rows a receipt reads, so the two cannot disagree. What the response pays in total against what was collected is a separate check, and the tests make it.

It is known to be wrong on three deal shapes:

- **A class the run never pays whose pay date cannot be formed** (pay day 30 in February, on 30/360): asking for the ledger returns an error where the same request without it succeeds.
- **A residual class typed as a certificate:** the leftover it is credited is on no line.
- **A termination payoff that differs from the pool's remaining balance:** `TERMINATION` shows no gap.

### Excess spread covering losses

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
| Pays them from the period's collections | `INTEREST`, `PRINCIPAL_SCHEDULED`, `PRINCIPAL_UNSCHEDULED`, `PRINCIPAL_RECOVERY`, `SUPPLEMENTAL_REDUCTION`, `MODIFICATION_LOSS` |
| Pays them principal out of the interest left | `EXCESS_TURBO` |
| Writes them down | `WRITEDOWN` |
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

### Interleaved waterfall ordering

The `waterfallOrder` field controls how interest and principal interact:

- `standard` (default): all interest paid, then all principal
- `interestFirst`: per seniority level, pay interest then principal
- `principalFirst`: per seniority level, pay principal then interest

## Project structure

```
src/
  GraamFlows.Api/          REST API (ASP.NET Core)
  GraamFlows.Cli/          Command-line interface
  GraamFlows.Core/         Waterfall engine, rules engine, triggers
  GraamFlows.Domain/       Domain models (Deal, Tranche, PayRule, etc.)
  GraamFlows.Objects/      Interfaces, enums, data contracts
  GraamFlows.Util/         Pricing analytics, calendar, day counters, term structure, solvers
tests/
  GraamFlows.Tests/        Unit and integration tests (xUnit)
```

### Core components

- **Amortizer** (`Core/AssetCashflowEngine/Amortizer.cs`) - High-performance collateral cashflow generator. Uses a struct-of-arrays asset layout (`AssetDataArrays`) and tight, allocation-free inner loops for cache locality — execution itself is single-threaded. Projects scheduled principal, prepayments, defaults, recoveries, and delinquencies for fixed-rate, ARM, step-rate, and IO loans.
- **ComposableStructure** (`Core/Waterfall/Structures/ComposableStructure.cs`) - The waterfall execution engine. Runs step-based waterfall periods, tracking available funds through each step.
- **Payable structures** (`Core/Waterfall/Structures/PayableStructures/`) - Composable payment distribution trees (Sequential, Prorata, ShiftingInterest, EnhancementCap, etc.).
- **RulesEngine** (`Core/RulesEngine/`) - Compiles PayRule DSL formulas into executable C# at runtime using Roslyn. Supports trigger conditions, variable lookups, balance queries, and structure-building functions.
- **UnifiedWaterfallBuilder** (`Api/Transformers/UnifiedWaterfallBuilder.cs`) - Transforms the steps-based JSON format into PayRule DSL for the engine.
- **Cashflow** (`Util/Finance/Cashflow.cs`) - Bond analytics calculator. Computes price, yield, Z-spread, discount margin, modified duration, WAL, and accrued interest from any cashflow stream against a term structure.
- **Solvers** (`Util/Solvers1D/`) - Numerical root-finding (Brent, Newton-Raphson, bisection, etc.) used by the pricing engine to solve for yield and spread.

## Supported deal types

The engine has been used to model:

- **Auto ABS** - sequential/turbo structures with OC targets (e.g., Exeter, Ford, Ally)
- **Private-label RMBS** - shifting interest with WAC caps and trigger-dependent structures (e.g., NQM, Angel Oak)
- **Credit risk transfer (CRT)** - guaranteed interest, supplemental reduction, computed variables (e.g., STACR)

## License

Business Source License 1.1 — converts to Apache 2.0 on April 3, 2029. See [LICENSE](LICENSE).
