namespace Affiant.SemanticKernel.Tests.Integration;

using Xunit;

/// <summary>
/// Serializes all L2 integration tests so each test's InMemoryExporterHelper captures only
/// its own OTel events. Multiple TracerProvider instances subscribed to the same ActivitySource
/// share the global OTel listener — parallel execution causes cross-contamination where one
/// test's events leak into another test's exporter list. [Collection] prevents this.
/// </summary>
[CollectionDefinition("AffiantL2IntegrationTests")]
public class AffiantL2IntegrationTestsCollection { }
