using NetArchTest.Rules;
using Xunit;

namespace Habita.ArchitectureTests;

public class LayerDependencyTests
{
    [Fact]
    public void Domain_does_not_depend_on_other_layers()
    {
        var result = Types.InAssembly(typeof(Habita.Domain.AssemblyMarker).Assembly)
            .ShouldNot().HaveDependencyOnAny("Habita.Application", "Habita.Infrastructure", "Habita.Api")
            .GetResult();
        Assert.True(result.IsSuccessful);
    }

    [Fact]
    public void Application_does_not_depend_on_infrastructure_or_api()
    {
        var result = Types.InAssembly(typeof(Habita.Application.AssemblyMarker).Assembly)
            .ShouldNot().HaveDependencyOnAny("Habita.Infrastructure", "Habita.Api")
            .GetResult();
        Assert.True(result.IsSuccessful);
    }
}
