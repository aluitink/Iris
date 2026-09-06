using Iris.Server.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace Iris.Server.Data;

/// <summary>
/// The EF Core <see cref="DbContext"/> for the Iris production persistence provider. Owns the
/// relational index (identity, edges, timestamps) while the full ActivityStreams documents live in
/// <c>jsonb</c> payload columns (the hybrid schema that keeps migrations rare).
/// </summary>
/// <remarks>
/// The relational shape is driven by the stable <c>Iris.Server.Stores</c> interfaces — not by
/// ActivityStreams vocabulary — so adding a new AP field or <c>iris:</c> extension lands inside an
/// existing <c>jsonb</c> column and needs no migration. Only a new store interface / indexed query
/// changes this model.
/// </remarks>
public sealed class IrisDbContext : DbContext
{
    /// <summary>
    /// Initializes a new context over the given options.
    /// </summary>
    /// <param name="options">The EF Core options (the Npgsql provider is configured by the caller).</param>
    public IrisDbContext(DbContextOptions<IrisDbContext> options)
        : base(options)
    {
    }

    /// <inheritdoc/>
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        ConfigureActor(modelBuilder);
        ConfigureObject(modelBuilder);
        ConfigureActivity(modelBuilder);
        ConfigureBoxItem(modelBuilder);
        ConfigureKey(modelBuilder);
        ConfigureEdge(modelBuilder);
        ConfigureCreateIndex(modelBuilder);
        ConfigureMedia(modelBuilder);
    }

    /// <summary>
    /// Configures the actor entity.
    /// </summary>
    private static void ConfigureActor(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ActorEntity>(entity =>
        {
            entity.ToTable("Actors");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).HasMaxLength(1024);
            entity.Property(e => e.Handle).HasMaxLength(255);
            entity.Property(e => e.Type).HasMaxLength(128);
            entity.Property(e => e.Document).HasColumnType("jsonb");
            entity.HasIndex(e => e.Handle);
        });
    }

    /// <summary>
    /// Configures the object entity.
    /// </summary>
    private static void ConfigureObject(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ObjectEntity>(entity =>
        {
            entity.ToTable("Objects");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).HasMaxLength(1024);
            entity.Property(e => e.AttributedTo).HasMaxLength(1024);
            entity.Property(e => e.ObjectType).HasMaxLength(128);
            entity.Property(e => e.Document).HasColumnType("jsonb");
            entity.HasIndex(e => e.AttributedTo);
        });
    }

    /// <summary>
    /// Configures the activity entity.
    /// </summary>
    private static void ConfigureActivity(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ActivityEntity>(entity =>
        {
            entity.ToTable("Activities");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).HasMaxLength(1024);
            entity.Property(e => e.ActivityType).HasMaxLength(128);
            entity.Property(e => e.Document).HasColumnType("jsonb");
            entity.HasIndex(e => e.ActivityType);
        });
    }

    /// <summary>
    /// Configures the outbox/inbox item entity. The item references its activity by IRI (<see cref="BoxItemEntity.ItemIri"/>,
    /// the activity's primary key) — a plain data column, not a mapped foreign-key relationship — so a
    /// collection can hold an item whose activity has been removed without a delete constraint.
    /// </summary>
    private static void ConfigureBoxItem(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<BoxItemEntity>(entity =>
        {
            entity.ToTable("BoxItems");
            entity.HasKey(e => new { e.Direction, e.ActorId, e.ItemIri });
            entity.Property(e => e.ActorId).HasMaxLength(1024);
            entity.Property(e => e.ItemIri).HasMaxLength(1024);
            // Idempotent add: a re-recorded item (same collection + actor + IRI) is a no-op (unique key).
        });
    }

    /// <summary>
    /// Configures the key entity.
    /// </summary>
    private static void ConfigureKey(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<KeyEntity>(entity =>
        {
            entity.ToTable("Keys");
            entity.HasKey(e => e.KeyId);
            entity.Property(e => e.KeyId).HasMaxLength(1024);
            entity.Property(e => e.Algorithm).HasMaxLength(32);
        });
    }

    /// <summary>
    /// Configures the generic directed edge entity (one table backs every relationship store).
    /// </summary>
    private static void ConfigureEdge(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<EdgeEntity>(entity =>
        {
            entity.ToTable("Edges");
            entity.HasKey(e => new { e.Kind, e.Source, e.Target });
            entity.Property(e => e.Source).HasMaxLength(1024);
            entity.Property(e => e.Target).HasMaxLength(1024);
            // One directed edge per (kind, source, target); the reverse index queries on (kind, target).
            entity.HasIndex(e => new { e.Kind, e.Source, e.Target }).IsUnique();
            entity.HasIndex(e => new { e.Kind, e.Target });
        });
    }

    /// <summary>
    /// Configures the object → Create index entity.
    /// </summary>
    private static void ConfigureCreateIndex(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<CreateIndexEntity>(entity =>
        {
            entity.ToTable("CreateIndex");
            entity.HasKey(e => e.ObjectId);
            entity.Property(e => e.ObjectId).HasMaxLength(1024);
            entity.Property(e => e.CreateActivityId).HasMaxLength(1024);
        });
    }

    /// <summary>
    /// Configures the media asset entity.
    /// </summary>
    private static void ConfigureMedia(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<MediaEntity>(entity =>
        {
            entity.ToTable("Media");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).HasMaxLength(128);
            entity.Property(e => e.ContentType).HasMaxLength(255);
            entity.Property(e => e.FileName).HasMaxLength(512);
            entity.Property(e => e.StorageKey).HasMaxLength(1024);
            entity.HasIndex(e => e.Id);
        });
    }
}
