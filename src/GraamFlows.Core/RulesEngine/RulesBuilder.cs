using System.Reflection;
using System.Runtime.Loader;
using System.Text;
using System.Text.RegularExpressions;
using GraamFlows.Objects.DataObjects;
using GraamFlows.Objects.TypeEnum;
using GraamFlows.Util.Calender;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace GraamFlows.RulesEngine;

public static class RulesBuilder
{
    private static readonly Regex RuleNameRegex = new("[^0-9a-zA-Z]+");

    public static Assembly CompileRules(IDeal deal)
    {
        if (deal.EncodedRules != null)
        {
            var array = Decode(deal.EncodedRules);
            var assem = AssemblyLoadContext.Default.LoadFromStream(new MemoryStream(array));
            return assem;
        }

        var code = BuildCode(deal);
        var ms = BuildAssembly(code);

        ms.Seek(0, SeekOrigin.Begin);
        var assembly = AssemblyLoadContext.Default.LoadFromStream(ms);
        return assembly;
    }

    public static string Encode(byte[] rules)
    {
        return Encoding.GetEncoding("iso-8859-1").GetString(rules);
    }

    public static byte[] Decode(string encode)
    {
        return Encoding.GetEncoding("iso-8859-1").GetBytes(encode);
    }

    public static string BuildCode(IList<IPayRule> payRules, IList<IDealTrigger> triggers, IList<ITranche> tranches)
    {
        string codeResource;
        using (var stream = Assembly.GetExecutingAssembly()
                   .GetManifestResourceStream("GraamFlows.Core.RulesEngine.RulesHost.cs"))
        {
            using (var streamReader = new StreamReader(stream))
            {
                codeResource = streamReader.ReadToEnd();
            }
        }

        // pay rules
        var rulesHostCode = new StringBuilder(codeResource);
        rulesHostCode.Append("\n");

        foreach (var rule in payRules)
        {
            var ruleName = GetRuleName(rule);
            rulesHostCode.Append($"public void {ruleName}() {{ {rule.Formula.Replace("'", "\"")}; }}\n");
        }

        // triggers
        foreach (var trigger in triggers)
        {
            var triggerName = GetTriggerName(trigger);
            if (trigger.TriggerType == "FORMULA_VOID")
                rulesHostCode.Append(
                    $"public void {triggerName}() {{ {trigger.TriggerFormula.Replace("'", "\"")}; }}\n");
            else if (trigger.TriggerType == "FORMULA_CONDITION" || trigger.TriggerType == "FORMULA_CONDITION_STICKY")
                rulesHostCode.Append(
                    $"public bool {triggerName}() {{ return {trigger.TriggerFormula.Replace("'", "\"")}; }}\n");
            else if (trigger.TriggerType == "FORMULA_VALUE")
                rulesHostCode.Append(
                    $"public double {triggerName}() {{ return {trigger.TriggerFormula.Replace("'", "\"")}; }}\n");
            else if (trigger.TriggerType == "FORMULA_VALUE_STR")
                rulesHostCode.Append(
                    $"public string {triggerName}() {{ return {trigger.TriggerFormula.Replace("'", "\"")}; }}\n");
        }

        // tranche coupon formula
        foreach (var tranche in tranches.Where(tran => tran.CouponTypeEnum == CouponType.Formula))
        {
            var cpnFormulaName = GetTrancheCpnFormulaName(tranche);
            rulesHostCode.Append(
                $"public double {cpnFormulaName}() {{ return {tranche.CouponFormula.Replace("'", "\"")}; }}\n");
        }

        rulesHostCode.Append("}}");
        return rulesHostCode.ToString();
    }

    public static string BuildCode(IDeal deal)
    {
        return BuildCode(deal.PayRules, deal.DealTriggers, deal.Tranches);
    }

    public static MemoryStream BuildAssembly(string code)
    {
        var syntaxTree = CSharpSyntaxTree.ParseText(code);
        var assemblyName = Path.GetRandomFileName();

        var references = new List<MetadataReference>();

        // Add references from the current assembly's dependencies
        var referencedAssemblies = Assembly.GetExecutingAssembly().GetReferencedAssemblies();
        foreach (var referencedAssembly in referencedAssemblies)
            try
            {
                var rfas = Assembly.Load(referencedAssembly);
                if (!string.IsNullOrEmpty(rfas.Location))
                    references.Add(MetadataReference.CreateFromFile(rfas.Location));
            }
            catch
            {
                // Skip assemblies that can't be loaded
            }

        // Add core references
        references.Add(MetadataReference.CreateFromFile(typeof(RulesBuilder).Assembly.Location));
        references.Add(MetadataReference.CreateFromFile(typeof(Enumerable).Assembly.Location));
        references.Add(MetadataReference.CreateFromFile(typeof(ITranche).Assembly.Location));
        references.Add(MetadataReference.CreateFromFile(typeof(object).Assembly.Location));
        references.Add(MetadataReference.CreateFromFile(typeof(Calendar).Assembly.Location));

        // Add .NET runtime references for .NET 8
        var runtimePath = Path.GetDirectoryName(typeof(object).Assembly.Location);
        if (runtimePath != null)
        {
            var systemRuntimePath = Path.Combine(runtimePath, "System.Runtime.dll");
            if (File.Exists(systemRuntimePath))
                references.Add(MetadataReference.CreateFromFile(systemRuntimePath));

            var systemCollectionsPath = Path.Combine(runtimePath, "System.Collections.dll");
            if (File.Exists(systemCollectionsPath))
                references.Add(MetadataReference.CreateFromFile(systemCollectionsPath));

            var systemLinqPath = Path.Combine(runtimePath, "System.Linq.dll");
            if (File.Exists(systemLinqPath))
                references.Add(MetadataReference.CreateFromFile(systemLinqPath));

            var netstandard = Path.Combine(runtimePath, "netstandard.dll");
            if (File.Exists(netstandard))
                references.Add(MetadataReference.CreateFromFile(netstandard));
        }

        var compilation = CSharpCompilation.Create(
            assemblyName,
            new[] { syntaxTree },
            references.Distinct(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var ms = new MemoryStream();

        var result = compilation.Emit(ms);
        if (!result.Success)
        {
            var failures = result.Diagnostics.Where(diagnostic =>
                diagnostic.IsWarningAsError ||
                diagnostic.Severity == DiagnosticSeverity.Error);

            var message = new StringBuilder("CSharpCompilation error: ");
            foreach (var diagnostic in failures)
            {
                if (message.Length > 0)
                    message.Append(", ");
                message.Append($"{diagnostic.Id}: {diagnostic.GetMessage()}");
            }

            throw new Exception(message.ToString());
        }

        return ms;
    }


    public static string GetRuleName(IPayRule payRule)
    {
        // remove non-chars
        var dealName = RuleNameRegex.Replace(payRule.DealName, "");
        var ruleName = RuleNameRegex.Replace(payRule.RuleName, "");
        var classGroupName = RuleNameRegex.Replace(payRule.ClassGroupName, "_");
        var funcName = $"rule_{dealName}_{classGroupName}_{ruleName}";
        return funcName;
    }

    public static string GetTriggerName(IDealTrigger trigger)
    {
        // remove non-chars
        var dealName = RuleNameRegex.Replace(trigger.DealName, "");
        var triggerName = RuleNameRegex.Replace(trigger.TriggerName, "");
        var funcName = $"trigger_{dealName}_{trigger.GroupNum}_{triggerName}";
        return funcName;
    }

    public static string GetTrancheCpnFormulaName(ITranche tranche)
    {
        var dealName = RuleNameRegex.Replace(tranche.DealName, "");
        var trancheName = RuleNameRegex.Replace(tranche.TrancheName, "");
        var funcName = $"trancheCpnFormula_{dealName}_{trancheName}";
        return funcName;
    }

    public static dynamic CreateRulesInstance(IDeal deal)
    {
        if (deal.RuleAssembly == null)
            deal.RuleAssembly = CompileRules(deal);
        var rulesInstance = deal.RuleAssembly.CreateInstance("GraamFlows.RulesEngine.RulesHost");
        return rulesInstance;
    }
}