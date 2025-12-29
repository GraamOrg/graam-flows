using GraamFlows.Objects.DataObjects;

namespace GraamFlows.Domain;

public class DealStructure : IDealStructure
{
    [Database("cfe_deal_name")] public string DealName { get; set; }

    [Database("Class_Group_Name")] public string ClassGroupName { get; set; }

    [Database("Subordination_Order")] public int SubordinationOrder { get; set; }

    [Database("Exchangable_Tranche")] public string ExchangableTranche { get; set; }

    [Database("Group_Num")] public string GroupNum { get; set; }

    [Database("Pay_From")] public string PayFrom { get; set; }

    [Database("Class_Tags")] public string ClassTags { get; set; }

    public PayFromEnum PayFromEnum
    {
        get
        {
            if (string.IsNullOrEmpty(PayFrom))
                throw new ArgumentException($"PayFrom is required! ${DealName} - {ClassGroupName}");

            if (!Enum.TryParse<PayFromEnum>(PayFrom, out var payFrom))
            {
                if (PayFrom.ToLower().Contains("seq"))
                    return PayFromEnum.Sequential;
                if (PayFrom.ToLower().Contains("prorata"))
                    return PayFromEnum.ProRata;
                if (PayFrom.ToLower().Contains("rule"))
                    return PayFromEnum.Rule;
                if (PayFrom.ToLower().Contains("excessservicing") || PayFrom.ToLower().Contains("xsio"))
                    return PayFromEnum.ExcessServicing;
                if (PayFrom.ToLower().Contains("resid"))
                    return PayFromEnum.Residual;
                if (PayFrom.ToLower().StartsWith("exp") || PayFrom.ToLower().StartsWith("fee"))
                    return PayFromEnum.Expense;
                if (PayFrom.ToLower().StartsWith("exch"))
                    return PayFromEnum.Exchange;
                if (PayFrom.ToLower().StartsWith("z"))
                    return PayFromEnum.Accrual;

                throw new ArgumentException($"PayFrom is invalid! ${DealName} - {ClassGroupName}");
            }

            return payFrom;
        }
    }
}