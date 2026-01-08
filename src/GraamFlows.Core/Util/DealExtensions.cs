using GraamFlows.Objects.DataObjects;
using GraamFlows.Objects.Util;

namespace GraamFlows.Util;

public static class DealExtensions
{
    public static ITranche? TrancheByName(this IDeal deal, string trancheName)
    {
        return deal.Tranches.SingleOrDefault(tran => tran.TrancheName == trancheName);
    }

    public static IDealVariables? DealVarByName(this IDeal deal, string varGroupName, string varName, string groupNum)
    {
        return deal.DealVariables.SingleOrDefault(dv =>
            dv.VariableName.Equals(varName, StringComparison.InvariantCultureIgnoreCase) &&
            dv.GroupNum == groupNum &&
            dv.VariableGroupName.Equals(varGroupName, StringComparison.InvariantCultureIgnoreCase));
    }

    public static IList<IDealVariables> DealVarByGroup(this IDeal deal, string varGroupName, string groupNum)
    {
        return deal.DealVariables.Where(dv =>
            dv.GroupNum == groupNum &&
            dv.VariableGroupName.Equals(varGroupName, StringComparison.InvariantCultureIgnoreCase)).ToList();
    }

    public static IDealFieldValue? DealFieldFieldValueByName(this IDeal deal, string group, string fieldName)
    {
        return deal.DealFieldValues.SingleOrDefault(field => field.FieldName == fieldName && field.GroupNum == group);
    }

    public static double GetDoubleVariableValue(this IEnumerable<IDealVariables> vars, string varName)
    {
        var dealVar =
            vars.SingleOrDefault(v => v.VariableName.Equals(varName, StringComparison.InvariantCultureIgnoreCase));
        if (dealVar == null)
            return double.NaN;
        return Convert.ToDouble(dealVar.VariableValue);
    }

    public static double GetDoubleVariableValue2(this IEnumerable<IDealVariables> vars, string varName)
    {
        var dealVar =
            vars.SingleOrDefault(v => v.VariableName.Equals(varName, StringComparison.InvariantCultureIgnoreCase));
        if (dealVar == null)
            return double.NaN;
        return Convert.ToDouble(dealVar.VariableValue2);
    }

    public static DateTime GetDateVariableValue(this IEnumerable<IDealVariables> vars, string varName)
    {
        var dealVar =
            vars.SingleOrDefault(v => v.VariableName.Equals(varName, StringComparison.InvariantCultureIgnoreCase));
        if (dealVar == null)
            return DateTime.MinValue;

        return DateUtil.TryParseDate(dealVar.VariableValue);
    }

    public static DateTime GetDateVariableValue2(this IEnumerable<IDealVariables> vars, string varName)
    {
        var dealVar =
            vars.SingleOrDefault(v => v.VariableName.Equals(varName, StringComparison.InvariantCultureIgnoreCase));
        if (dealVar == null)
            return DateTime.MinValue;

        return DateUtil.TryParseDate(dealVar.VariableValue2);
    }

    public static bool ContainsClassTag(this IDealStructure dealStructure, string tagName)
    {
        if (string.IsNullOrEmpty(dealStructure.ClassTags))
            return false;

        var classTags = dealStructure.ClassTags.Split(',').Select(ct => ct.ToLower().Trim());
        return classTags.Contains(tagName.ToLower());
    }
}
