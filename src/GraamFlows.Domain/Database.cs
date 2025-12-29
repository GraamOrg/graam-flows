namespace GraamFlows.Domain;

public class DatabaseAttribute : Attribute
{
    public DatabaseAttribute(string name)
    {
        Name = name;
    }

    public string Name { get; set; }
}