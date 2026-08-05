using Affiant.TestInfrastructure;
using Xunit;

namespace Affiant.EntityFramework.Tests.Infrastructure;

/// <summary>
/// xUnit collection marker for tests that share a <see cref="PostgresFixture"/>.
/// Decorate test classes with <c>[Collection("Postgres")]</c> to opt in.
/// </summary>
[CollectionDefinition("Postgres")]
public sealed class PostgresCollection : ICollectionFixture<PostgresFixture>
{
}
