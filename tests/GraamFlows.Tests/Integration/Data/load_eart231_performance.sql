-- Query to extract EART-2023-1 actual performance for test fixtures
-- Run against BigQuery: auto_loans.fact_loans

SELECT
    reporting_period_ending_date as period_date,
    sum(beginning_balance)/1e6 as begin_bal_mm,
    sum(ending_balance)/1e6 as ending_bal_mm,

    -- Delinquency
    100*sum(case when days_delinquent > 30 then 1 else 0 end * ending_balance)/sum(ending_balance) as dq_pct,

    -- Annualized CDR (defaults + chargeoffs)
    100*(1-power(1-sum(case when zero_balance_reason in ('2','4') then 1 else 0 end * beginning_balance) / sum(beginning_balance),12)) as cdr_pct,

    -- Annualized VPR (voluntary prepays)
    100*(1-power(1-sum(case when zero_balance_reason in ('1') then 1 else 0 end * beginning_balance) / sum(beginning_balance),12)) as vpr_pct,

    -- Loss severity
    100*(1-sum(recovered_amount)/NULLIF(sum(charged_off_amount), 0)) as sev_pct,

    -- Raw amounts for debugging
    sum(charged_off_amount)/1e6 as chargeoffs_mm,
    sum(recovered_amount)/1e6 as recoveries_mm,
    count(*) as loan_count

FROM auto_loans.fact_loans
WHERE issuer_name = 'Exeter Automobile Receivables Trust 2023-1'
GROUP BY reporting_period_ending_date
ORDER BY reporting_period_ending_date
LIMIT 24  -- First 2 years
