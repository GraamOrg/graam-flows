# Cash ledger

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
