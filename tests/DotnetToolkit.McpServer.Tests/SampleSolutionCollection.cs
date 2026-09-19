using Xunit;

namespace DotnetToolkit.McpServer.Tests;

/// <summary>Lets every integration test class share one loaded sample solution.</summary>
[CollectionDefinition("SampleSolution")]
public sealed class SampleSolutionCollection : ICollectionFixture<SampleSolutionFixture>;
