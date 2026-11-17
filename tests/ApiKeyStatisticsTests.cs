using Aiursoft.Nopkg.Entities;
using Aiursoft.Nopkg.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Aiursoft.Nopkg.Tests;

[TestClass]
public class ApiKeyStatisticsTests
{
    [TestMethod]
    public async Task ExistingKeySurvivesMigrationAndConcurrentUsagePersistsAcrossContexts()
    {
        var directory = Directory.CreateTempSubdirectory("registry-key-statistics-");
        try
        {
            var options = new DbContextOptionsBuilder<SqliteContext>()
                .UseSqlite("Data Source=" + Path.Combine(directory.FullName, "statistics.db")).Options;
            var hash = new string('a', 64);
            const string prefix = "aaaaaaaa";
            var created = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            var expires = created.AddYears(10);
            int id;
            string previous;
            await using (var db = new SqliteContext(options))
            {
                previous = db.Database.GetMigrations().ToArray()[^2];
                await db.GetService<IMigrator>().MigrateAsync(previous);
                var user = new User { UserName = "publisher", DisplayName = "Publisher" };
                db.Users.Add(user);
                await db.SaveChangesAsync();
                await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO UserApiKeys (UserId, Name, KeyHash, KeyPrefix, CreatedAt, ExpiresAt) VALUES ({user.Id}, {"Original key"}, {hash}, {prefix}, {created}, {expires})");
                await db.Database.MigrateAsync();
                var key = await db.UserApiKeys.SingleAsync();
                id = key.Id;
                Assert.AreEqual(hash, key.KeyHash);
                Assert.AreEqual(expires, key.ExpiresAt);
                Assert.AreEqual(0L, key.TotalUses);
                Assert.AreEqual(0L, key.PublishedPackagesCount);
                Assert.IsNull(key.LastPublishedPackage);
                Assert.IsFalse(db.Database.HasPendingModelChanges());
            }

            // A previously opened edit form must not overwrite newer usage counters.
            await using (var edit = new SqliteContext(options))
            {
                var key = await edit.UserApiKeys.SingleAsync(k => k.Id == id);
                await Task.WhenAll(Enumerable.Range(0, 24).Select(async _ =>
                {
                    await using var request = new SqliteContext(options);
                    Assert.IsTrue(await request.RecordUseAsync(id, default));
                    await request.RecordPublicationAsync(id, "example.package", "1.2.3", default);
                }));
                key.Name = "Renamed key";
                await edit.SaveChangesAsync();
            }
            await using (var db = new SqliteContext(options))
            {
                var key = await db.UserApiKeys.SingleAsync(k => k.Id == id);
                Assert.AreEqual(24L, key.TotalUses);
                Assert.AreEqual(24L, key.PublishedPackagesCount);
                Assert.AreEqual("example.package", key.LastPublishedPackage);
                Assert.AreEqual("1.2.3", key.LastPublishedVersion);
                Assert.AreEqual("Renamed key", key.Name);
                Assert.AreEqual(hash, key.KeyHash);
                Assert.AreEqual(prefix, key.KeyPrefix);
                Assert.AreEqual(created, key.CreatedAt);
                Assert.IsNotNull(key.LastUsedAt);
                Assert.IsNotNull(key.LastPublishedAt);
                await db.GetService<IMigrator>().MigrateAsync(previous);
                await db.Database.MigrateAsync();
                db.ChangeTracker.Clear();
                key = await db.UserApiKeys.SingleAsync(k => k.Id == id);
                Assert.AreEqual(hash, key.KeyHash);
                Assert.AreEqual(expires, key.ExpiresAt);
                key.ExpiresAt = created;
                await db.SaveChangesAsync();
                Assert.IsFalse(await db.RecordUseAsync(id, default));
                db.UserApiKeys.Remove(key);
                await db.SaveChangesAsync();
                Assert.IsFalse(await db.RecordUseAsync(id, default));
                await db.RecordPublicationAsync(id, "example.package", "1.2.4", default);
                Assert.AreEqual(0, await db.UserApiKeys.CountAsync());
            }
        }
        finally { directory.Delete(true); }
    }
}
