using Aiursoft.Nopkg.Entities;
using Aiursoft.Nopkg.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
namespace Aiursoft.Nopkg.Tests;

[TestClass]
public class PublishingPersistenceTests
{
    [TestMethod]
    public async Task DeletingOwnerRevokesKeysAndRetainsReservedNameAndReleases()
    {
        var directory=Directory.CreateTempSubdirectory("nopkg-persistence-");
        try
        {
            var options=new DbContextOptionsBuilder<SqliteContext>().UseSqlite("Data Source="+Path.Combine(directory.FullName,"test.db")).Options;
            string userId;
            await using(var db=new SqliteContext(options))
            {
                await db.Database.MigrateAsync();
                var user=new User {DisplayName="Publisher",UserName="publisher"};userId=user.Id;
                var family=new PackageFamily {Name="owned",OwnerUserId=user.Id,IsLocallyPublished=true};
                db.Users.Add(user);db.PackageFamilies.Add(family);
                db.UserApiKeys.Add(new UserApiKey {UserId=user.Id,Name="Key",KeyHash=new string('a',64),KeyPrefix="aaaaaaaa"});
                db.PublishedPackages.Add(new PublishedPackage {Family=family,Version="1.0.0",Sha256=new string('b',64),ManifestJson="{}",UploadedByUserId=user.Id});
                await db.SaveChangesAsync();
            }
            await using(var db=new SqliteContext(options))
            {
                db.Users.Remove(await db.Users.SingleAsync(x=>x.Id==userId));await db.SaveChangesAsync();
            }
            await using(var db=new SqliteContext(options))
            {
                Assert.AreEqual(0,await db.UserApiKeys.CountAsync());
                var family=await db.PackageFamilies.SingleAsync();Assert.IsNull(family.OwnerUserId);Assert.IsTrue(family.IsLocallyPublished);
                Assert.IsNull((await db.PublishedPackages.SingleAsync()).UploadedByUserId);
            }
        }
        finally {directory.Delete(true);}
    }
    [TestMethod]
    public async Task ExistingIdentityDataSurvivesUpgradeRollbackAndReupgrade()
    {
        var directory=Directory.CreateTempSubdirectory("nopkg-upgrade-");
        try
        {
            var options=new DbContextOptionsBuilder<SqliteContext>().UseSqlite("Data Source="+Path.Combine(directory.FullName,"test.db")).Options;
            string id;string previous;
            await using(var db=new SqliteContext(options))
            {
                var migrations=db.Database.GetMigrations().ToArray();previous=migrations[^2];
                await db.GetService<IMigrator>().MigrateAsync(previous);
                var user=new User {DisplayName="Existing",UserName="existing"};id=user.Id;db.Users.Add(user);await db.SaveChangesAsync();
                await db.Database.MigrateAsync();Assert.AreEqual(id,(await db.Users.SingleAsync()).Id);
                await db.GetService<IMigrator>().MigrateAsync(previous);Assert.AreEqual(id,(await db.Users.SingleAsync()).Id);
                await db.Database.MigrateAsync();Assert.AreEqual(id,(await db.Users.SingleAsync()).Id);
                Assert.AreEqual(0,await db.PublishedPackages.CountAsync());
                Assert.IsFalse(db.Database.HasPendingModelChanges());
            }
        }
        finally {directory.Delete(true);}
    }
}
