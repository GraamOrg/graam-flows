namespace GraamFlows.Objects.DataObjects;

public class InputField
{
    public InputField(string fieldName)
    {
        FieldName = fieldName;
    }

    public InputField()
    {
    }

    public InputField(string fieldName, IEnumerable<string> possibleValues)
    {
        FieldName = fieldName;
        PossibleValues = possibleValues.ToList();
    }

    public string FieldName { get; set; }
    public List<string> PossibleValues { get; set; }
}