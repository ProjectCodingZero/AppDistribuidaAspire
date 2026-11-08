using System.Data;
using Microsoft.EntityFrameworkCore;

namespace AppDistribuidaAspire.ApiService.Data;

public static class DatabaseSchemaInitializer
{
    public static async Task InitializeAsync(ThisContext database, CancellationToken cancellationToken = default)
    {
        await database.Database.EnsureCreatedAsync(cancellationToken);

        // EnsureCreated does not add Identity tables to an existing SQLite file. Create
        // missing tables and add nullable order columns so existing purchase rows survive.
        await database.Database.ExecuteSqlRawAsync("""
            CREATE TABLE IF NOT EXISTS "AspNetRoles" (
                "Id" TEXT NOT NULL CONSTRAINT "PK_AspNetRoles" PRIMARY KEY,
                "Name" TEXT NULL,
                "NormalizedName" TEXT NULL,
                "ConcurrencyStamp" TEXT NULL
            );
            CREATE TABLE IF NOT EXISTS "AspNetUsers" (
                "Id" TEXT NOT NULL CONSTRAINT "PK_AspNetUsers" PRIMARY KEY,
                "UserName" TEXT NULL,
                "NormalizedUserName" TEXT NULL,
                "Email" TEXT NULL,
                "NormalizedEmail" TEXT NULL,
                "EmailConfirmed" INTEGER NOT NULL,
                "PasswordHash" TEXT NULL,
                "SecurityStamp" TEXT NULL,
                "ConcurrencyStamp" TEXT NULL,
                "PhoneNumber" TEXT NULL,
                "PhoneNumberConfirmed" INTEGER NOT NULL,
                "TwoFactorEnabled" INTEGER NOT NULL,
                "LockoutEnd" TEXT NULL,
                "LockoutEnabled" INTEGER NOT NULL,
                "AccessFailedCount" INTEGER NOT NULL
            );
            CREATE TABLE IF NOT EXISTS "AspNetRoleClaims" (
                "Id" INTEGER NOT NULL CONSTRAINT "PK_AspNetRoleClaims" PRIMARY KEY AUTOINCREMENT,
                "RoleId" TEXT NOT NULL,
                "ClaimType" TEXT NULL,
                "ClaimValue" TEXT NULL,
                CONSTRAINT "FK_AspNetRoleClaims_AspNetRoles_RoleId" FOREIGN KEY ("RoleId") REFERENCES "AspNetRoles" ("Id") ON DELETE CASCADE
            );
            CREATE TABLE IF NOT EXISTS "AspNetUserClaims" (
                "Id" INTEGER NOT NULL CONSTRAINT "PK_AspNetUserClaims" PRIMARY KEY AUTOINCREMENT,
                "UserId" TEXT NOT NULL,
                "ClaimType" TEXT NULL,
                "ClaimValue" TEXT NULL,
                CONSTRAINT "FK_AspNetUserClaims_AspNetUsers_UserId" FOREIGN KEY ("UserId") REFERENCES "AspNetUsers" ("Id") ON DELETE CASCADE
            );
            CREATE TABLE IF NOT EXISTS "AspNetUserLogins" (
                "LoginProvider" TEXT NOT NULL,
                "ProviderKey" TEXT NOT NULL,
                "ProviderDisplayName" TEXT NULL,
                "UserId" TEXT NOT NULL,
                CONSTRAINT "PK_AspNetUserLogins" PRIMARY KEY ("LoginProvider", "ProviderKey"),
                CONSTRAINT "FK_AspNetUserLogins_AspNetUsers_UserId" FOREIGN KEY ("UserId") REFERENCES "AspNetUsers" ("Id") ON DELETE CASCADE
            );
            CREATE TABLE IF NOT EXISTS "AspNetUserRoles" (
                "UserId" TEXT NOT NULL,
                "RoleId" TEXT NOT NULL,
                CONSTRAINT "PK_AspNetUserRoles" PRIMARY KEY ("UserId", "RoleId"),
                CONSTRAINT "FK_AspNetUserRoles_AspNetUsers_UserId" FOREIGN KEY ("UserId") REFERENCES "AspNetUsers" ("Id") ON DELETE CASCADE,
                CONSTRAINT "FK_AspNetUserRoles_AspNetRoles_RoleId" FOREIGN KEY ("RoleId") REFERENCES "AspNetRoles" ("Id") ON DELETE CASCADE
            );
            CREATE TABLE IF NOT EXISTS "AspNetUserTokens" (
                "UserId" TEXT NOT NULL,
                "LoginProvider" TEXT NOT NULL,
                "Name" TEXT NOT NULL,
                "Value" TEXT NULL,
                CONSTRAINT "PK_AspNetUserTokens" PRIMARY KEY ("UserId", "LoginProvider", "Name"),
                CONSTRAINT "FK_AspNetUserTokens_AspNetUsers_UserId" FOREIGN KEY ("UserId") REFERENCES "AspNetUsers" ("Id") ON DELETE CASCADE
            );
            CREATE TABLE IF NOT EXISTS "PaymentOrders" (
                "Id" INTEGER NOT NULL CONSTRAINT "PK_PaymentOrders" PRIMARY KEY AUTOINCREMENT,
                "ExternalReference" TEXT NOT NULL,
                "PreferenceId" TEXT NOT NULL,
                "PaymentId" TEXT NULL,
                "Status" TEXT NOT NULL,
                "StatusDetail" TEXT NULL,
                "UserId" TEXT NULL,
                "Amount" TEXT NOT NULL,
                "Currency" TEXT NOT NULL,
                "CreatedAtUtc" TEXT NOT NULL,
                "ApprovedAtUtc" TEXT NULL,
                "IsDevelopmentSample" INTEGER NOT NULL DEFAULT 0
            );
            CREATE TABLE IF NOT EXISTS "PaymentOrderItems" (
                "Id" INTEGER NOT NULL CONSTRAINT "PK_PaymentOrderItems" PRIMARY KEY AUTOINCREMENT,
                "PaymentOrderId" INTEGER NOT NULL,
                "ProductId" INTEGER NOT NULL,
                "ProductName" TEXT NOT NULL,
                "Quantity" INTEGER NOT NULL,
                "UnitPrice" TEXT NOT NULL,
                CONSTRAINT "FK_PaymentOrderItems_PaymentOrders_PaymentOrderId" FOREIGN KEY ("PaymentOrderId") REFERENCES "PaymentOrders" ("Id") ON DELETE CASCADE
            );
            CREATE UNIQUE INDEX IF NOT EXISTS "IX_PaymentOrders_ExternalReference" ON "PaymentOrders" ("ExternalReference");
            CREATE UNIQUE INDEX IF NOT EXISTS "RoleNameIndex" ON "AspNetRoles" ("NormalizedName") WHERE "NormalizedName" IS NOT NULL;
            CREATE INDEX IF NOT EXISTS "EmailIndex" ON "AspNetUsers" ("NormalizedEmail");
            CREATE UNIQUE INDEX IF NOT EXISTS "UserNameIndex" ON "AspNetUsers" ("NormalizedUserName") WHERE "NormalizedUserName" IS NOT NULL;
            CREATE INDEX IF NOT EXISTS "IX_AspNetRoleClaims_RoleId" ON "AspNetRoleClaims" ("RoleId");
            CREATE INDEX IF NOT EXISTS "IX_AspNetUserClaims_UserId" ON "AspNetUserClaims" ("UserId");
            CREATE INDEX IF NOT EXISTS "IX_AspNetUserLogins_UserId" ON "AspNetUserLogins" ("UserId");
            CREATE INDEX IF NOT EXISTS "IX_AspNetUserRoles_RoleId" ON "AspNetUserRoles" ("RoleId");
            CREATE INDEX IF NOT EXISTS "IX_AspNetUserRoles_UserId" ON "AspNetUserRoles" ("UserId");
            CREATE INDEX IF NOT EXISTS "IX_AspNetUserTokens_UserId" ON "AspNetUserTokens" ("UserId");
            CREATE INDEX IF NOT EXISTS "IX_PaymentOrderItems_PaymentOrderId" ON "PaymentOrderItems" ("PaymentOrderId");
            """, cancellationToken);

        await EnsureColumnAsync(database, "UserId", "TEXT NULL", cancellationToken);
        await EnsureColumnAsync(database, "IsDevelopmentSample", "INTEGER NOT NULL DEFAULT 0", cancellationToken);
        await database.Database.ExecuteSqlRawAsync(
            "CREATE INDEX IF NOT EXISTS \"IX_PaymentOrders_UserId\" ON \"PaymentOrders\" (\"UserId\")",
            cancellationToken);
    }

    private static async Task EnsureColumnAsync(
        ThisContext database,
        string columnName,
        string columnDefinition,
        CancellationToken cancellationToken)
    {
        var connection = database.Database.GetDbConnection();
        var closeConnection = connection.State != ConnectionState.Open;
        if (closeConnection)
        {
            await connection.OpenAsync(cancellationToken);
        }

        try
        {
            var exists = false;
            await using (var command = connection.CreateCommand())
            {
                command.CommandText = "PRAGMA table_info(\"PaymentOrders\")";
                await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                {
                    if (string.Equals(reader.GetString(1), columnName, StringComparison.OrdinalIgnoreCase))
                    {
                        exists = true;
                        break;
                    }
                }
            }

            if (!exists)
            {
                await using var command = connection.CreateCommand();
                command.CommandText = $"ALTER TABLE \"PaymentOrders\" ADD COLUMN \"{columnName}\" {columnDefinition}";
                await command.ExecuteNonQueryAsync(cancellationToken);
            }
        }
        finally
        {
            if (closeConnection)
            {
                await connection.CloseAsync();
            }
        }
    }
}