using System;
using APIExample;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace APIExample.Migrations
{
    [DbContext(typeof(TaskDbContext))]
    [Migration("20261015100000_Initial")]
    public partial class Initial
    {
        protected override void BuildTargetModel(ModelBuilder modelBuilder)
        {
#pragma warning disable 612, 618
            modelBuilder
                .HasAnnotation("ProductVersion", "8.0.11")
                .HasAnnotation("Relational:MaxIdentifierLength", 63);

            NpgsqlModelBuilderExtensions.UseIdentityByDefaultColumns(modelBuilder);

            modelBuilder.Entity("APIExample.TaskItem", b =>
            {
                b.Property<Guid>("Id")
                    .ValueGeneratedOnAdd()
                    .HasColumnType("uuid");

                b.Property<DateTimeOffset?>("CompletedAt")
                    .HasColumnType("timestamp with time zone");

                b.Property<DateTimeOffset>("CreatedAt")
                    .HasColumnType("timestamp with time zone");

                b.Property<bool>("IsCompleted")
                    .HasColumnType("boolean");

                b.Property<string>("Priority")
                    .IsRequired()
                    .HasMaxLength(16)
                    .HasColumnType("character varying(16)");

                b.Property<string>("Title")
                    .IsRequired()
                    .HasMaxLength(200)
                    .HasColumnType("character varying(200)");

                b.Property<uint>("RowVersion")
                    .IsConcurrencyToken()
                    .ValueGeneratedOnAddOrUpdate()
                    .HasColumnType("xid")
                    .HasColumnName("xmin");

                b.HasKey("Id");

                b.ToTable("TaskItems", (string)null);
            });
#pragma warning restore 612, 618
        }
    }
}
