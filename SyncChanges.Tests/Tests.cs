using NPoco;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using NUnit;
using NUnit.Framework;
using SyncChanges;
using System.Threading;

namespace SyncChanges.Tests
{
    [TestFixture]
    public class Tests
    {
        const string ConnectionString = @"Server=(localdb)\MSSQLLocalDB;Integrated Security=true";
        const string SourceDatabaseName = "SyncChangesTestSource";
        const string DestinationDatabaseName = "SyncChangesTestDestination";

        static string GetConnectionString(string db = "") => ConnectionString + (db.Length > 0 ? $";Initial Catalog={db}" : "");
        static Database GetDatabase(string db = "") => new(GetConnectionString(db), DatabaseType.SqlServer2012, System.Data.SqlClient.SqlClientFactory.Instance);

        static void DropDatabase(string name)
        {
            using var db = GetDatabase();
            var sql = $@"if (exists(select name from master.dbo.sysdatabases where name = '{name}'))
                begin
                    alter database [{name}]
                    set single_user with rollback immediate
                    drop database [{name}]
                end";
            db.Execute(sql);
        }

        private static void CreateDatabase(string name)
        {
            using var db = GetDatabase();
            db.Execute($"create database [{name}]");
            db.Execute($"alter database [{name}] set COMPATIBILITY_LEVEL = 100");
            db.Execute($"alter database [{name}] set CHANGE_TRACKING = ON (CHANGE_RETENTION = 2 DAYS, AUTO_CLEANUP = ON)");
            if ((string)TestContext.CurrentContext.Test.Properties.Get("snapshot") != "off")
            {
                db.Execute($"ALTER DATABASE [{name}] SET ALLOW_SNAPSHOT_ISOLATION ON");
                db.Execute($"ALTER DATABASE [{name}] SET READ_COMMITTED_SNAPSHOT ON");
            }
        }

        [SetUp]
        public void Setup()
        {
            DropDatabase(SourceDatabaseName);
            DropDatabase(DestinationDatabaseName);
            CreateDatabase(SourceDatabaseName);
            CreateDatabase(DestinationDatabaseName);
        }

        [TearDown]
        public void TearDown()
        {
            DropDatabase(SourceDatabaseName);
            DropDatabase(DestinationDatabaseName);
        }

        static void CreateUsersTable(string dbName)
        {
            using var db = GetDatabase(dbName);
            db.Execute(@"if not exists (select * from sys.tables where name = 'Users') create table Users (
                    UserId int identity(1,1) primary key not null,
                    Name nvarchar(200) null,
                    Age int null,
                    DateOfBirth datetime null,
                    Savings decimal null
                )");
            db.Execute(@"alter table Users add constraint Users_Name_Age_UQ unique (Name, Age)");
            db.Execute(@"alter table Users
                    enable CHANGE_TRACKING
                    with (TRACK_COLUMNS_UPDATED = OFF)");
        }

        static void CreateOrdersTable(string dbName)
        {
            using var db = GetDatabase(dbName);
            db.Execute(@"if not exists (select * from sys.tables where name = 'Orders') create table Orders (
                    OrderId int primary key not null,
                    UserId int not null
                )");
            db.Execute(@"alter table Orders
                    enable CHANGE_TRACKING
                    with (TRACK_COLUMNS_UPDATED = OFF)");
        }

        static void CreateOrdersForeignKey(string dbName)
        {
            using var db = GetDatabase(dbName);
            db.Execute(@"alter table Orders add constraint Orders_UserId_FK foreign key (UserId) references Users(UserId)");
        }

        static void DropTable(string dbName, string tableName)
        {
            using var db = GetDatabase(dbName);
            db.Execute($@"if exists (select * from sys.tables where name = '{tableName}') drop table {tableName}");
        }

        static void CreateUsersTable()
        {
            DropTable("Users");
            CreateUsersTable(SourceDatabaseName);
            CreateUsersTable(DestinationDatabaseName);
        }

        static void CreateOrdersTable()
        {
            DropTable("Orders");
            CreateOrdersTable(SourceDatabaseName);
            CreateOrdersTable(DestinationDatabaseName);
        }

        static void DropTable(string tableName)
        {
            DropTable(SourceDatabaseName, tableName);
            DropTable(DestinationDatabaseName, tableName);
        }

        [TableName("Users")]
        [PrimaryKey("UserId")]
        class User
        {
            public int UserId { get; set; }
            public string Name { get; set; }
            public int Age { get; set; }
            public DateTime DateOfBirth { get; set; }
            public decimal Savings { get; set; }

            public override bool Equals(Object obj)
            {
                if (obj == null || GetType() != obj.GetType())
                    return false;

                User u = (User)obj;
                return UserId == u.UserId && Name == u.Name
                    && Age == u.Age && DateOfBirth == u.DateOfBirth && Savings == u.Savings;
            }

            public override int GetHashCode()
            {
                return UserId;
            }
        }

        [TableName("Orders")]
        [PrimaryKey("OrderId", AutoIncrement = false)]
        class Order
        {
            public int OrderId { get; set; }
            public int UserId { get; set; }

            public override bool Equals(Object obj)
            {
                if (obj == null || GetType() != obj.GetType())
                    return false;

                Order o = (Order)obj;
                return OrderId == o.OrderId && UserId == o.UserId;
            }

            public override int GetHashCode()
            {
                return OrderId;
            }
        }

        readonly ReplicationSet TestReplicationSet = new()
        {
            Name = "Test",
            Source = new DatabaseInfo { Name = "Source", ConnectionString = GetConnectionString(SourceDatabaseName) },
            Destinations = { new DatabaseInfo { Name = "Destination", ConnectionString = GetConnectionString(DestinationDatabaseName) } },
            Tables = { "Users", "dbo.Orders" }
        };

        Config TestConfig { get; set; }

        public Tests()
        {
            TestConfig = new Config { ReplicationSets = { TestReplicationSet } };
        }

        [Test]
        public void InsertTest()
        {
            try
            {
                CreateUsersTable();

                var sourceUser = new User { Name = "Michael Jordan", Age = 54, DateOfBirth = new DateTime(1963, 2, 17), Savings = 1.31m * 1e9m };

                using (var db = GetDatabase(SourceDatabaseName))
                {
                    db.Insert(sourceUser);
                    sourceUser.Name = "Michael Jeffrey Jordan";
                    db.Update(sourceUser);
                }

                var synchronizer = new Synchronizer(TestConfig);
                var success = synchronizer.Sync();

                Assert.That(success, Is.True);

                using (var db = GetDatabase(DestinationDatabaseName))
                {
                    var user = db.Single<User>("select * from Users");
                    Assert.That(user.Name, Is.EqualTo(sourceUser.Name));
                    Assert.That(user.Age, Is.EqualTo(sourceUser.Age));
                    Assert.That(user.DateOfBirth, Is.EqualTo(sourceUser.DateOfBirth));
                    Assert.That(user.Savings, Is.EqualTo(sourceUser.Savings));
                }
            }
            finally
            {
                DropTable("Users");
            }
        }

        [TestCase(true)]
        [TestCase(false)]
        public void UpdateDeleteTest(bool dryRun)
        {
            try
            {
                CreateUsersTable();

                var sourceUsers = new List<User>
                {
                    new User { Name = "Michael Jordan", Age = 54, DateOfBirth = new DateTime(1963, 2, 17), Savings = 1.31m * 1e9m },
                    new User { Name = "Larry Bird", Age = 60, DateOfBirth = new DateTime(1956, 12, 7), Savings = 45m * 1e6m },
                    new User { Name = "Karl Malone", Age = 53, DateOfBirth = new DateTime(1963, 7, 24), Savings = 75m * 1e6m }
                };

                using (var db = GetDatabase(SourceDatabaseName))
                {
                    foreach (var user in sourceUsers)
                        db.Insert(user);
                }

                var synchronizer = new Synchronizer(TestConfig) { DryRun = dryRun };
                var success = synchronizer.Sync();

                Assert.That(success, Is.True);

                if (!dryRun)
                {
                    using var db = GetDatabase(DestinationDatabaseName);
                    var users = db.Fetch<User>("select * from Users");
                    Assert.That(users, Is.EquivalentTo(sourceUsers));
                }

                using (var db = GetDatabase(SourceDatabaseName))
                {
                    sourceUsers[0].Name = "Michael Jeffrey Jordan";
                    sourceUsers[1].Name = "Larry Joe Bird";
                    db.Update(sourceUsers[0]);
                    db.Update(sourceUsers[1]);
                    db.Delete(sourceUsers[2]);
                    sourceUsers.Remove(sourceUsers[2]);
                }

                success = synchronizer.Sync();

                Assert.That(success, Is.True);

                if (!dryRun)
                {
                    using var db = GetDatabase(DestinationDatabaseName);
                    var users = db.Fetch<User>("select * from Users");
                    Assert.That(users, Is.EquivalentTo(sourceUsers));
                }
            }
            finally
            {
                DropTable("Users");
            }
        }

        [Test, Property("snapshot", "off")]
        public void NoSnapshotTest()
        {
            UpdateDeleteTest(false);
        }

        [Test]
        public void NoChangeTrackingInDestinationTest()
        {
            try
            {
                CreateUsersTable();

                using (var db = GetDatabase(DestinationDatabaseName))
                {
                    db.Execute(@"alter table Users disable CHANGE_TRACKING");
                    db.Execute($"alter database [{DestinationDatabaseName}] set CHANGE_TRACKING = OFF");
                }

                var sourceUser = new User { Name = "Michael Jordan", Age = 54, DateOfBirth = new DateTime(1963, 2, 17), Savings = 1.31m * 1e9m };

                using (var db = GetDatabase(SourceDatabaseName))
                {
                    db.Insert(sourceUser);
                    sourceUser.Name = "Michael Jeffrey Jordan";
                    db.Update(sourceUser);
                }

                var synchronizer = new Synchronizer(TestConfig);
                var success = synchronizer.Sync();

                Assert.That(success, Is.True);

                using (var db = GetDatabase(DestinationDatabaseName))
                {
                    var user = db.Single<User>("select * from Users");
                    Assert.That(user.Name, Is.EqualTo(sourceUser.Name));
                    Assert.That(user.Age, Is.EqualTo(sourceUser.Age));
                    Assert.That(user.DateOfBirth, Is.EqualTo(sourceUser.DateOfBirth));
                    Assert.That(user.Savings, Is.EqualTo(sourceUser.Savings));
                }
            }
            finally
            {
                DropTable("Users");
            }
        }

        [Test]
        public void MinimumVersionTest()
        {
            try
            {
                CreateUsersTable();

                var sourceUser = new User { Name = "Michael Jordan", Age = 54, DateOfBirth = new DateTime(1963, 2, 17), Savings = 1.31m * 1e9m };

                using (var db = GetDatabase(SourceDatabaseName))
                    db.Insert(sourceUser);

                var synchronizer = new Synchronizer(TestConfig);
                var success = synchronizer.Sync();

                Assert.That(success, Is.True);

                sourceUser.Name = "Michael Jeffrey Jordan";

                using (var db = GetDatabase(SourceDatabaseName))
                {
                    db.Update(sourceUser);
                    db.Execute(@"alter table Users disable CHANGE_TRACKING");
                    db.Execute(@"alter table Users enable CHANGE_TRACKING with (TRACK_COLUMNS_UPDATED = OFF)");
                }

                success = synchronizer.Sync();

                Assert.That(success, Is.False);

                using (var db = GetDatabase(DestinationDatabaseName))
                {
                    var user = db.Single<User>("select * from Users");
                    Assert.That(user.Name, Is.EqualTo("Michael Jordan"));
                }
            }
            finally
            {
                DropTable("Users");
            }
        }

        [Test]
        public void ForeignKeyTest()
        {
            try
            {
                CreateUsersTable();
                CreateOrdersTable();
                CreateOrdersForeignKey(SourceDatabaseName);
                CreateOrdersForeignKey(DestinationDatabaseName);

                var sourceOrder = new Order { OrderId = 1 };
                var sourceUser = new User { Name = "Michael Jordan", Age = 54, DateOfBirth = new DateTime(1963, 2, 17), Savings = 1.31m * 1e9m };

                using (var db = GetDatabase(SourceDatabaseName))
                {
                    db.Insert(sourceUser);
                    sourceOrder.UserId = sourceUser.UserId;
                    db.Insert(sourceOrder);
                }

                var synchronizer = new Synchronizer(TestConfig);
                var success = synchronizer.Sync();

                Assert.That(success, Is.True);

                using (var db = GetDatabase(DestinationDatabaseName))
                {
                    var user = db.Single<User>("select * from Users");
                    Assert.That(user, Is.EqualTo(sourceUser));

                    var order = db.Single<Order>("select * from Orders");
                    Assert.That(order, Is.EqualTo(sourceOrder));
                }
            }
            finally
            {
                DropTable("Orders");
                DropTable("Users");
            }
        }

        [Test]
        public void IntermediateTest()
        {
            try
            {
                CreateUsersTable();
                CreateOrdersTable();
                CreateOrdersForeignKey(SourceDatabaseName);
                CreateOrdersForeignKey(DestinationDatabaseName);

                var sourceOrder = new Order { OrderId = 1 };
                var sourceOrder2 = new Order { OrderId = 2 };
                var sourceUser = new User { Name = "Michael Jordan", Age = 54, DateOfBirth = new DateTime(1963, 2, 17), Savings = 1.31m * 1e9m };
                var sourceUser2 = new User { Name = "Larry Bird", Age = 60, DateOfBirth = new DateTime(1956, 12, 7), Savings = 45m * 1e6m };
                var sourceUser3 = new User { Name = "Karl Malone", Age = 53, DateOfBirth = new DateTime(1963, 7, 24), Savings = 75m * 1e6m };

                using (var db = GetDatabase(SourceDatabaseName))
                {
                    db.Insert(sourceUser);
                    sourceOrder.UserId = sourceUser.UserId;
                    sourceOrder2.UserId = sourceUser.UserId;
                    db.Insert(sourceOrder);
                    db.Insert(sourceOrder2);
                    db.Insert(sourceUser2);
                    sourceOrder.UserId = sourceUser2.UserId;
                    db.Update(sourceOrder);
                    db.Insert(sourceUser3);
                    sourceOrder2.UserId = sourceUser3.UserId;
                    db.Update(sourceOrder2);
                }

                var synchronizer = new Synchronizer(TestConfig);
                var success = synchronizer.Sync();

                Assert.That(success, Is.True);

                using (var db = GetDatabase(DestinationDatabaseName))
                {
                    var orders = db.Fetch<Order>("select * from Orders");
                    Assert.That(orders.Count, Is.EqualTo(2));
                    Assert.That(orders[0], Is.EqualTo(sourceOrder));
                    Assert.That(orders[1], Is.EqualTo(sourceOrder2));
                }
            }
            finally
            {
                DropTable("Orders");
                DropTable("Users");
            }
        }

        [Test]
        public void UniqueTest()
        {
            try
            {
                CreateUsersTable();

                var sourceUser = new User { Name = "Michael Jordan", Age = 54, DateOfBirth = new DateTime(1963, 2, 17), Savings = 1.31m * 1e9m };
                var sourceUser2 = new User { Name = "Michael Jordan", Age = 55, DateOfBirth = new DateTime(1963, 2, 17), Savings = 1.31m * 1e9m };

                using (var db = GetDatabase(SourceDatabaseName))
                {
                    db.Insert(sourceUser);
                }

                var synchronizer = new Synchronizer(TestConfig);
                var success = synchronizer.Sync();

                Assert.That(success, Is.True);

                using (var db = GetDatabase(SourceDatabaseName))
                {
                    db.Insert(sourceUser2);
                    sourceUser.Age = 56;
                    db.Update(sourceUser);
                    sourceUser2.Age = 54;
                    db.Update(sourceUser2);
                }

                success = synchronizer.Sync();

                Assert.That(success, Is.True);

                using (var db = GetDatabase(DestinationDatabaseName))
                {
                    var users = db.Fetch<User>("select * from Users");
                    Assert.That(users.Count, Is.EqualTo(2));
                    Assert.That(users[0].Age, Is.EqualTo(56));
                    Assert.That(users[1].Age, Is.EqualTo(54));
                }
            }
            finally
            {
                DropTable("Users");
            }
        }

        [Test]
        public void NullConfigTest()
        {
            Assert.Throws<ArgumentException>(() => new Synchronizer(null));
        }

        [Test]
        public void NoTablesTest()
        {
            var rs = new ReplicationSet
            {
                Name = "Test",
                Source = new DatabaseInfo { Name = "Source", ConnectionString = GetConnectionString(SourceDatabaseName) },
                Destinations = { new DatabaseInfo { Name = "Destination", ConnectionString = GetConnectionString(DestinationDatabaseName) } },
                Tables = new List<string> { "Test" }
            };
            var config = new Config { ReplicationSets = { rs } };

            var synchronizer = new Synchronizer(config)
            {
                Timeout = 1000
            };
            var success = synchronizer.Sync();

            Assert.That(success, Is.True);
        }

        [Test]
        public void FalseSourceTest()
        {
            var rs = new ReplicationSet
            {
                Name = "Test",
                Source = new DatabaseInfo { Name = "Source", ConnectionString = GetConnectionString("Error") },
                Destinations = { new DatabaseInfo { Name = "Destination", ConnectionString = GetConnectionString(DestinationDatabaseName) } },
                Tables = { }
            };
            var config = new Config { ReplicationSets = { rs } };

            var synchronizer = new Synchronizer(config)
            {
                Timeout = 1000
            };
            Assert.Throws<System.Data.SqlClient.SqlException>(() => synchronizer.Sync());
        }

        [Test]
        public void LoopTest()
        {
            try
            {
                CreateUsersTable();

                var sourceUsers = new List<User>
                {
                    new User { Name = "Michael Jordan", Age = 54, DateOfBirth = new DateTime(1963, 2, 17), Savings = 1.31m * 1e9m },
                    new User { Name = "Larry Bird", Age = 60, DateOfBirth = new DateTime(1956, 12, 7), Savings = 45m * 1e6m },
                    new User { Name = "Karl Malone", Age = 53, DateOfBirth = new DateTime(1963, 7, 24), Savings = 75m * 1e6m }
                };

                using (var db = GetDatabase(SourceDatabaseName))
                {
                    foreach (var user in sourceUsers)
                        db.Insert(user);
                }

                var synchronizer = new Synchronizer(TestConfig) { Interval = 2 };
                var auto = new AutoResetEvent(false);
                synchronizer.Synced += (s, e) => auto.Set();
                var src = new CancellationTokenSource();
                var t = Task.Factory.StartNew(() => synchronizer.SyncLoop(src.Token));

                auto.WaitOne();

                using (var db = GetDatabase(DestinationDatabaseName))
                {
                    var users = db.Fetch<User>("select * from Users");
                    Assert.That(users, Is.EquivalentTo(sourceUsers));
                }

                using (var db = GetDatabase(SourceDatabaseName))
                {
                    sourceUsers[0].Name = "Michael Jeffrey Jordan";
                    sourceUsers[1].Name = "Larry Joe Bird";
                    db.Update(sourceUsers[0]);
                    db.Update(sourceUsers[1]);
                    db.Delete(sourceUsers[2]);
                    sourceUsers.Remove(sourceUsers[2]);
                }

                auto.WaitOne();

                using (var db = GetDatabase(DestinationDatabaseName))
                {
                    var users = db.Fetch<User>("select * from Users");
                    Assert.That(users, Is.EquivalentTo(sourceUsers));
                }

                src.Cancel();
                t.Wait();
            }
            finally
            {
                DropTable("Users");
            }
        }
    }
}
