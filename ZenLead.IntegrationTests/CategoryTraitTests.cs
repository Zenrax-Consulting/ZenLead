using System.Reflection;
using ZenLead.Tests.Support;

namespace ZenLead.IntegrationTests;

/// <summary>
/// Every test class in this project must carry <c>[Trait("Category", TestCategories.Integration)]</c>, so filters such as
/// <c>dotnet test ZenLead.slnx --filter Category!=Integration</c> (fast, offline) and the T09 packs select it correctly.
/// </summary>
[Trait("Category", TestCategories.Integration)]
public class CategoryTraitTests
{
    [Fact]
    public void EveryTestClass_IsTaggedIntegration()
    {
        var testClasses = typeof(CategoryTraitTests).Assembly.GetTypes()
            .Where(t => t is { IsClass: true, IsAbstract: false } && t.GetMethods().Any(m => m.GetCustomAttributes<FactAttribute>(inherit: true).Any()));

        var untagged = testClasses
            .Where(t => !t.GetCustomAttributes<TraitAttribute>().Any(a => IsIntegrationTrait(a, t)))
            .Select(t => t.FullName)
            .ToList();

        Assert.NotEmpty(testClasses);
        Assert.True(untagged.Count == 0, "Missing [Trait(\"Category\", TestCategories.Integration)]: " + string.Join(", ", untagged));
    }

    // TraitAttribute keeps its arguments only in metadata: read them from the attribute data
    private static bool IsIntegrationTrait(TraitAttribute _, Type type)
        => type.GetCustomAttributesData().Any(d => d.AttributeType == typeof(TraitAttribute)
            && d.ConstructorArguments.Count == 2
            && (string?)d.ConstructorArguments[0].Value == "Category"
            && (string?)d.ConstructorArguments[1].Value == TestCategories.Integration);
}
