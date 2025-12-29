using System.Reflection;

namespace GraamFlows.Objects.DataObjects;

public interface IPayRuleAssemblyStore
{
    Assembly RuleAssembly { get; set; }
}