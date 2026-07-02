namespace Iris.Tests.Integration;

/// <summary>
/// Shares ONE ApiTestFactory (and therefore one Postgres Testcontainer) across every
/// test class in this collection, instead of spinning up a fresh container per class.
/// xUnit runs tests within the same collection SEQUENTIALLY, trading class-level
/// parallelism for container startup savings — see Phase 5 plan doc for the measured
/// trade-off. Data isolation across classes relies on every test generating its own
/// Guid-scoped userId/conversationId/personaId (verified in the Phase 5 audit).
/// </summary>
[CollectionDefinition("ApiTestFactory collection")]
public class ApiTestFactoryCollection : ICollectionFixture<ApiTestFactory>;

/// <summary>
/// Shares ONE IntegrationTestFactory (and therefore one Postgres Testcontainer)
/// across every test class in this collection. See ApiTestFactoryCollection for the
/// sequential-execution trade-off this implies.
/// </summary>
[CollectionDefinition("IntegrationTestFactory collection")]
public class IntegrationTestFactoryCollection : ICollectionFixture<IntegrationTestFactory>;
