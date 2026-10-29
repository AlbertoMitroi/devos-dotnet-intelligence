namespace Aspire.Hosting
{
    using Aspire.Hosting.ApplicationModel;

    public interface IDistributedApplicationBuilder { }

    public static class DistributedApplication
    {
        public static IDistributedApplicationBuilder CreateBuilder(string[] args) => null!;
    }

    public static class ProjectResourceBuilderExtensions
    {
        public static IResourceBuilder<ProjectResource> AddDotnetProject(this IDistributedApplicationBuilder builder, string name, string projectPath) => null!;
    }

    public static class ResourceBuilderExtensions
    {
        public static IResourceBuilder<T> WithReference<T>(this IResourceBuilder<T> builder, IResourceBuilder<ProjectResource> source) => builder;
    }
}

namespace Aspire.Hosting.ApplicationModel
{
    public interface IResourceBuilder<out T> { }

    public sealed class ProjectResource { }
}
