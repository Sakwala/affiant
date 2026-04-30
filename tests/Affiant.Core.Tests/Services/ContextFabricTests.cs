namespace Affiant.Core.Tests.Services;

using Affiant.Abstractions.Models;
using Affiant.Core.Services;
using Xunit;

public class ContextFabricTests
{
    [Fact]
    public void Upsert_Adds_New_Entity()
    {
        var fabric = new ContextFabric();
        var entity = new EntityRef("Customer", "customer-1", "Acme Corp",
            new Dictionary<string, object> { ["name"] = "Acme Corp", ["email"] = "contact@acme.com" });

        fabric.Upsert(entity);

        var retrieved = fabric.GetByKey("customer-1");
        Assert.NotNull(retrieved);
        Assert.Equal("customer-1", retrieved.EntityId);
        Assert.Equal("Acme Corp", retrieved.Fields["name"]);
    }

    [Fact]
    public void GetByKey_Returns_Null_If_Not_Found()
    {
        var fabric = new ContextFabric();

        var retrieved = fabric.GetByKey("nonexistent");

        Assert.Null(retrieved);
    }

    [Fact]
    public void Upsert_Merges_Existing_Entity_Fields()
    {
        var fabric = new ContextFabric();
        fabric.Upsert(new EntityRef("Customer", "customer-1", "Acme Corp",
            new Dictionary<string, object> { ["name"] = "Acme Corp", ["city"] = "New York" }));
        fabric.Upsert(new EntityRef("Customer", "customer-1", "Acme Corp",
            new Dictionary<string, object> { ["email"] = "contact@acme.com" }));

        var retrieved = fabric.GetByKey("customer-1");
        Assert.NotNull(retrieved);
        Assert.Equal("Acme Corp", retrieved.Fields["name"]);
        Assert.Equal("contact@acme.com", retrieved.Fields["email"]);
        Assert.Equal("New York", retrieved.Fields["city"]);
    }

    [Fact]
    public void Upsert_Incoming_Field_Overrides_Existing()
    {
        var fabric = new ContextFabric();
        fabric.Upsert(new EntityRef("Customer", "customer-1", "Acme Corp",
            new Dictionary<string, object> { ["status"] = "Active" }));
        fabric.Upsert(new EntityRef("Customer", "customer-1", "Acme Corp",
            new Dictionary<string, object> { ["status"] = "Inactive" }));

        var retrieved = fabric.GetByKey("customer-1");
        Assert.NotNull(retrieved);
        Assert.Equal("Inactive", retrieved.Fields["status"]);
    }

    [Fact]
    public void Snapshot_Returns_Copy_Of_All_Entities()
    {
        var fabric = new ContextFabric();
        fabric.Upsert(new EntityRef("Customer", "c1", "Acme", new Dictionary<string, object> { ["name"] = "Acme" }));
        fabric.Upsert(new EntityRef("Customer", "c2", "Beta", new Dictionary<string, object> { ["name"] = "Beta" }));

        var snapshot = fabric.Snapshot();

        Assert.Equal(2, snapshot.Count);
        Assert.True(snapshot.ContainsKey("c1"));
        Assert.True(snapshot.ContainsKey("c2"));
    }

    [Fact]
    public void Snapshot_Is_Independent_Copy()
    {
        var fabric = new ContextFabric();
        fabric.Upsert(new EntityRef("Customer", "c1", "Acme", new Dictionary<string, object>()));

        var snapshot = fabric.Snapshot();
        fabric.Upsert(new EntityRef("Customer", "c2", "Beta", new Dictionary<string, object>()));

        Assert.Single(snapshot);
    }

    [Fact]
    public void MergeFrom_Upserts_Multiple_Entities()
    {
        var fabric = new ContextFabric();
        var entities = new[]
        {
            new EntityRef("Aircraft", "a1", "N123", new Dictionary<string, object> { ["registration"] = "N123" }),
            new EntityRef("Aircraft", "a2", "N456", new Dictionary<string, object> { ["registration"] = "N456" })
        };

        fabric.MergeFrom(entities);

        Assert.NotNull(fabric.GetByKey("a1"));
        Assert.NotNull(fabric.GetByKey("a2"));
    }

    [Fact]
    public void Clear_Removes_All_Entities()
    {
        var fabric = new ContextFabric();
        fabric.Upsert(new EntityRef("Customer", "c1", "Acme", new Dictionary<string, object> { ["name"] = "Acme" }));

        fabric.Clear();

        Assert.Null(fabric.GetByKey("c1"));
        Assert.Empty(fabric.Snapshot());
    }
}
