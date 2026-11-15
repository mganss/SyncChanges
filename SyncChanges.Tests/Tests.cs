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
using System.Xml.Linq;

namespace SyncChanges.Tests
{
    [TestFixture]
    public class Tests
    {
        const string ConnectionString = @"Server=(localdb)\MSSQLLocalDB;Integrated Security=true";
        const string SourceDatabaseName = "SyncChangesTestSource";
        const string DestinationDatabaseName = "SyncChangesTestDestination";

        static string GetConnectionString(string db = "") => ConnectionString + (db.Length > 0 ? $";Initial Catalog={db}" : "");
        static Database GetDatabase(string db = "") => new(GetConnectionString(db), DatabaseType.SqlServer2012, Microsoft.Data.SqlClient.SqlClientFactory.Instance);

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

        [TableName("Users")]
        [PrimaryKey("NUM_TRF", AutoIncrement = false)]
        class VarBinaryUser
        {
            public int Num_Trf { get; set; }
            public string Name { get; set; }
            public string Description { get; set; }
            public byte[] Report { get; set; }
        }

        [Test]
        public void PlanReuseTest()
        {
            try
            {
                CreateUsersTable();

                // LocalDB closes databases when their last connection closes, which drops their plans
                using (var db = GetDatabase())
                    db.Execute($"alter database [{DestinationDatabaseName}] set AUTO_CLOSE OFF");

                var sourceUsers = Enumerable.Range(1, 50).Select(i => new User
                {
                    Name = $"Plan User {i}",
                    Age = i,
                    DateOfBirth = new DateTime(1970, 1, 1).AddDays(i),
                    Savings = i
                }).ToList();

                using (var db = GetDatabase(SourceDatabaseName))
                    foreach (var user in sourceUsers)
                        db.Insert(user);

                Assert.That(new Synchronizer(TestConfig) { BatchSize = 10 }.Sync(), Is.True);

                using (var db = GetDatabase(DestinationDatabaseName))
                {
                    Assert.That(db.Fetch<User>("select * from Users"), Is.EquivalentTo(sourceUsers));

                    // the inserts' text is the same in all five commands, so they share one plan
                    // char(64) is @, which NPoco would take for a parameter
                    var stats = db.Single<string>(@"select concat(count(*), ' ', sum(cp.usecounts))
                        from sys.dm_exec_cached_plans cp cross apply sys.dm_exec_sql_text(cp.plan_handle) t
                        where t.text like '(' + char(64) + '0 int%' and t.text like '%insert into \[dbo\].\[Users\]%' escape '\'").Split(' ');
                    Assert.That(int.Parse(stats[0]), Is.EqualTo(1));
                    Assert.That(long.Parse(stats[1]), Is.GreaterThanOrEqualTo(50));
                }
            }
            finally
            {
                DropTable("Users");
            }
        }

        [Test]
        public void ColumnTypesTest()
        {
            const string columns = @"
                        Id int primary key not null,
                        Ascii varchar(20) null, AsciiMax varchar(max) null, Unicode nvarchar(30) null, UnicodeMax nvarchar(max) null,
                        FixedAscii char(5) null, FixedUnicode nchar(5) null, OldText text null, OldUnicodeText ntext null,
                        Bin binary(4) null, VarBin varbinary(16) null, VarBinMax varbinary(max) null, OldImage image null,
                        Small smallint null, Tiny tinyint null, Big bigint null, Flag bit null,
                        Dec decimal(18, 4) null, Num numeric(5, 2) null, Money money null, Real real null, Float float null,
                        Date date null, Time time(3) null, DateTime datetime null, DateTime2 datetime2(7) null, SmallDateTime smalldatetime null,
                        DateTimeOffset datetimeoffset(2) null, Guid uniqueidentifier null, Xml xml null";
            const string values = @"
                        'abc', replicate(cast('x' as varchar(max)), 9000), N'äöü€', replicate(cast(N'ü' as nvarchar(max)), 5000),
                        'ab', N'äö', 'old text', N'old ünicode',
                        0x01020304, 0x0A0B, cast(replicate(cast(0x0102 as varbinary(max)), 5000) as varbinary(max)), 0xFF,
                        -32768, 255, 9223372036854775807, 1,
                        12345678901234.5678, 123.45, 922337203685477.5807, 1.5, 3.141592653589793,
                        '2026-11-15', '12:34:56.789', '2026-11-15 12:34:56.997', '2026-11-15 12:34:56.1234567', '2026-11-15 12:34',
                        '2026-11-15 12:34:56.12 +01:00', newid(), N'<a b=""c"">ä</a>'";
            const string compare = @"select concat_ws('|', Id, Ascii, len(AsciiMax), Unicode, len(UnicodeMax), FixedAscii, FixedUnicode,
                        cast(OldText as varchar(max)), cast(OldUnicodeText as nvarchar(max)), convert(varchar(max), Bin, 1), convert(varchar(max), VarBin, 1),
                        datalength(VarBinMax), convert(varchar(max), cast(OldImage as varbinary(max)), 1), Small, Tiny, Big, Flag, Dec, Num, Money,
                        Real, Float, Date, Time, convert(varchar(30), DateTime, 121), convert(varchar(30), DateTime2, 121), SmallDateTime,
                        DateTimeOffset, Guid, cast(Xml as nvarchar(max)), '#') from Users order by Id";

            try
            {
                DropTable("Users");
                foreach (var dbName in new[] { SourceDatabaseName, DestinationDatabaseName })
                {
                    using var db = GetDatabase(dbName);
                    db.Execute($"create table Users ({columns})");
                    db.Execute("alter table Users enable CHANGE_TRACKING with (TRACK_COLUMNS_UPDATED = OFF)");
                }

                using (var db = GetDatabase(SourceDatabaseName))
                {
                    db.Execute($"insert into Users values (1, {values})");
                    db.Execute($"insert into Users values (2, {values})");
                    db.Execute("insert into Users (Id) values (3)"); // all nulls
                    db.Execute($"insert into Users values (4, {values})");
                }

                var (success, log) = SyncWithLog(new Synchronizer(TestConfig));
                Assert.That(success, Is.True, () => string.Join(Environment.NewLine, log.Where(l => l.Contains("rror"))));

                using (var db = GetDatabase(SourceDatabaseName))
                {
                    // values to nulls, nulls to values, a delete
                    db.Execute("delete from Users where Id = 2");
                    db.Execute("update Users set Id = Id where Id = 1"); // an update with the same values
                    db.Execute($"delete from Users where Id = 3; insert into Users values (3, {values})");
                    db.Execute("update Users set Ascii = null, Unicode = null, VarBin = null, OldImage = null, Dec = null, DateTime2 = null, Guid = null, Xml = null where Id = 4");
                }

                // with batches of several statements, so that statements of different kinds share a command
                (success, log) = SyncWithLog(new Synchronizer(TestConfig) { BatchSize = 3 });
                Assert.That(success, Is.True, () => string.Join(Environment.NewLine, log.Where(l => l.Contains("rror"))));

                using var source = GetDatabase(SourceDatabaseName);
                using var destination = GetDatabase(DestinationDatabaseName);
                var expected = source.Fetch<string>(compare);
                Assert.That(expected, Has.Count.EqualTo(3));
                Assert.That(destination.Fetch<string>(compare), Is.EqualTo(expected));
            }
            finally
            {
                DropTable("Users");
            }
        }

        [Test]
        public void VarBinaryTest()
        {
            try
            {
                static void CreateUsersTable(string dbName)
                {
                    using var db = GetDatabase(dbName);
                    db.Execute(@"if not exists (select * from sys.tables where name = 'Users') create table Users (
                        NUM_TRF int primary key not null,
                        NAME varchar(255),
                        DESCRIPTION varchar(1000),
                        REPORT varbinary(max)
                    )");
                    db.Execute(@"alter table Users
                        enable CHANGE_TRACKING
                        with (TRACK_COLUMNS_UPDATED = OFF)");
                }

                DropTable("Users");
                CreateUsersTable(SourceDatabaseName);
                CreateUsersTable(DestinationDatabaseName);

                var sourceUser = new VarBinaryUser { Num_Trf = 1, Name = "Test", Description = "Test Description", Report = [1, 2, 3, 4] };

                using (var db = GetDatabase(SourceDatabaseName))
                {
                    db.Insert(sourceUser);
                    sourceUser.Report = null;
                    db.Update(sourceUser);
                }

                var synchronizer = new Synchronizer(TestConfig);
                var success = synchronizer.Sync();

                Assert.That(success, Is.True);

                using (var db = GetDatabase(DestinationDatabaseName))
                {
                    var user = db.Single<VarBinaryUser>("select * from Users");
                }
            }
            finally
            {
                DropTable("Users");
            }
        }

        [TestCase(1)]
        [TestCase(7)]
        [TestCase(100)]
        [TestCase(1000)] // more parameters than fit in one command of exec sp_executesql calls
        public void BatchTest(int batchSize)
        {
            try
            {
                CreateUsersTable();
                CreateOrdersTable();
                CreateOrdersForeignKey(SourceDatabaseName);
                CreateOrdersForeignKey(DestinationDatabaseName);

                var sourceUsers = Enumerable.Range(1, 600).Select(i => new User
                {
                    Name = $"User {i}",
                    Age = 20 + i % 50,
                    DateOfBirth = new DateTime(1960, 1, 1).AddDays(i),
                    Savings = i * 1000m
                }).ToList();
                var sourceOrders = new List<Order>();

                using (var db = GetDatabase(SourceDatabaseName))
                {
                    // one transaction, so users and the orders referencing them share a version
                    db.BeginTransaction();
                    foreach (var user in sourceUsers)
                        db.Insert(user);
                    for (int i = 0; i < sourceUsers.Count; i += 2)
                    {
                        var order = new Order { OrderId = i + 1, UserId = sourceUsers[i].UserId };
                        db.Insert(order);
                        sourceOrders.Add(order);
                    }
                    db.CompleteTransaction();
                }

                void AssertReplicated()
                {
                    var synchronizer = new Synchronizer(TestConfig) { BatchSize = batchSize };
                    var success = synchronizer.Sync();

                    Assert.That(success, Is.True);

                    using var db = GetDatabase(DestinationDatabaseName);
                    Assert.That(db.Fetch<User>("select * from Users"), Is.EquivalentTo(sourceUsers));
                    Assert.That(db.Fetch<Order>("select * from Orders"), Is.EquivalentTo(sourceOrders));
                }

                AssertReplicated();

                using (var db = GetDatabase(SourceDatabaseName))
                {
                    foreach (var user in sourceUsers.Where((u, i) => i % 3 == 0))
                    {
                        user.Savings += 1;
                        db.Update(user);
                    }

                    foreach (var order in sourceOrders.Where((o, i) => i % 4 == 0).ToList())
                    {
                        db.Delete(order);
                        sourceOrders.Remove(order);
                    }

                    foreach (var user in sourceUsers.Where((u, i) => i % 2 == 1).ToList()) // users without orders
                    {
                        db.Delete(user);
                        sourceUsers.Remove(user);
                    }
                }

                AssertReplicated();
            }
            finally
            {
                DropTable("Orders");
                DropTable("Users");
            }
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
                    new() { Name = "Michael Jordan", Age = 54, DateOfBirth = new DateTime(1963, 2, 17), Savings = 1.31m * 1e9m },
                    new() { Name = "Larry Bird", Age = 60, DateOfBirth = new DateTime(1956, 12, 7), Savings = 45m * 1e6m },
                    new() { Name = "Karl Malone", Age = 53, DateOfBirth = new DateTime(1963, 7, 24), Savings = 75m * 1e6m }
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

        [TestCase(1)]
        [TestCase(100)]
        public void ReferencedRowUpdatedLaterTest(int batchSize)
        {
            try
            {
                CreateUsersTable();
                CreateOrdersTable();
                CreateOrdersForeignKey(SourceDatabaseName);
                CreateOrdersForeignKey(DestinationDatabaseName);

                var sourceUser = new User { Name = "Michael Jordan", Age = 54, DateOfBirth = new DateTime(1963, 2, 17), Savings = 1.31m * 1e9m };
                var sourceUser2 = new User { Name = "Larry Bird", Age = 60, DateOfBirth = new DateTime(1956, 12, 7), Savings = 45m * 1e6m };
                var sourceOrder = new Order { OrderId = 1 };
                var sourceOrder2 = new Order { OrderId = 2 };

                using (var db = GetDatabase(SourceDatabaseName))
                {
                    // The users are updated after the orders referencing them were inserted, so their inserts
                    // are replicated at the version of that update, after the orders.
                    db.Insert(sourceUser);
                    sourceOrder.UserId = sourceUser.UserId;
                    db.Insert(sourceOrder);

                    db.BeginTransaction(); // user and order inserted in the same version
                    db.Insert(sourceUser2);
                    sourceOrder2.UserId = sourceUser2.UserId;
                    db.Insert(sourceOrder2);
                    db.CompleteTransaction();

                    sourceUser.Savings += 1;
                    db.Update(sourceUser);
                    sourceUser2.Savings += 1;
                    db.Update(sourceUser2);
                }

                var (success, log) = SyncWithLog(new Synchronizer(TestConfig) { BatchSize = batchSize });

                Assert.That(success, Is.True);
                Assert.That(log, Has.Some.StartsWith("Deferring insert into [dbo].[Orders]"));
                Assert.That(log, Has.None.StartsWith("Disabling foreign key constraint"));

                using (var db = GetDatabase(DestinationDatabaseName))
                {
                    Assert.That(db.Fetch<User>("select * from Users"), Is.EquivalentTo([sourceUser, sourceUser2]));
                    Assert.That(db.Fetch<Order>("select * from Orders"), Is.EquivalentTo([sourceOrder, sourceOrder2]));
                    AssertForeignKeysTrusted(db);
                }
            }
            finally
            {
                DropTable("Orders");
                DropTable("Users");
            }
        }

        [Test]
        public void ReferencedRowUpdatedLaterUniqueTest()
        {
            try
            {
                CreateUsersTable();
                CreateOrdersTable();
                CreateOrdersForeignKey(SourceDatabaseName);
                CreateOrdersForeignKey(DestinationDatabaseName);

                var otherUser = new User { Name = "Michael Jordan", Age = 54, DateOfBirth = new DateTime(1963, 2, 17), Savings = 1.31m * 1e9m };

                using (var db = GetDatabase(SourceDatabaseName))
                    db.Insert(otherUser);

                Assert.That(new Synchronizer(TestConfig).Sync(), Is.True);

                var sourceUser = new User { Name = "Michael Jordan", Age = 1, DateOfBirth = new DateTime(1963, 2, 17), Savings = 0 };
                var sourceOrder = new Order { OrderId = 1 };

                using (var db = GetDatabase(SourceDatabaseName))
                {
                    db.Insert(sourceUser);
                    sourceOrder.UserId = sourceUser.UserId;
                    db.Insert(sourceOrder);
                    otherUser.Age = 55; // frees (Name, Age) = (Michael Jordan, 54) of the unique constraint
                    db.Update(otherUser);
                    sourceUser.Age = 54; // and the new user takes it
                    db.Update(sourceUser);
                }

                // Inserting the user before the order would insert it before otherUser frees its unique values.
                var (success, log) = SyncWithLog(new Synchronizer(TestConfig));

                Assert.That(success, Is.True);
                Assert.That(log, Has.None.StartsWith("Disabling foreign key constraint"));

                using (var db = GetDatabase(DestinationDatabaseName))
                {
                    Assert.That(db.Fetch<User>("select * from Users"), Is.EquivalentTo([otherUser, sourceUser]));
                    Assert.That(db.Fetch<Order>("select * from Orders"), Is.EquivalentTo([sourceOrder]));
                    AssertForeignKeysTrusted(db);
                }
            }
            finally
            {
                DropTable("Orders");
                DropTable("Users");
            }
        }

        [Test]
        public void UpdateReferencingRowInsertedLaterTest()
        {
            try
            {
                CreateUsersTable();
                CreateOrdersTable();
                CreateOrdersForeignKey(SourceDatabaseName);
                CreateOrdersForeignKey(DestinationDatabaseName);

                var sourceUser = new User { Name = "Michael Jordan", Age = 54, DateOfBirth = new DateTime(1963, 2, 17), Savings = 1.31m * 1e9m };
                var sourceOrder = new Order { OrderId = 1 };

                using (var db = GetDatabase(SourceDatabaseName))
                {
                    db.Insert(sourceUser);
                    sourceOrder.UserId = sourceUser.UserId;
                    db.Insert(sourceOrder);
                }

                Assert.That(new Synchronizer(TestConfig).Sync(), Is.True);

                var sourceUser2 = new User { Name = "Larry Bird", Age = 60, DateOfBirth = new DateTime(1956, 12, 7), Savings = 45m * 1e6m };

                using (var db = GetDatabase(SourceDatabaseName))
                {
                    db.Insert(sourceUser2);
                    sourceOrder.UserId = sourceUser2.UserId; // an update, which is not deferred
                    db.Update(sourceOrder);
                    sourceUser2.Savings += 1;
                    db.Update(sourceUser2);
                }

                var (success, log) = SyncWithLog(new Synchronizer(TestConfig));

                Assert.That(success, Is.True);
                Assert.That(log, Has.Some.EqualTo("Disabling foreign key constraint Orders_UserId_FK"));

                using (var db = GetDatabase(DestinationDatabaseName))
                {
                    Assert.That(db.Fetch<User>("select * from Users"), Is.EquivalentTo([sourceUser, sourceUser2]));
                    Assert.That(db.Fetch<Order>("select * from Orders"), Is.EquivalentTo([sourceOrder]));
                    AssertForeignKeysTrusted(db);
                }
            }
            finally
            {
                DropTable("Orders");
                DropTable("Users");
            }
        }

        [TestCase(false)]
        [TestCase(true)]
        public void SelfReferenceTest(bool cycle)
        {
            try
            {
                CreateUsersTable();

                foreach (var dbName in new[] { SourceDatabaseName, DestinationDatabaseName })
                {
                    using var db = GetDatabase(dbName);
                    db.Execute("alter table Users add ParentId int null");
                    db.Execute("alter table Users add constraint Users_ParentId_FK foreign key (ParentId) references Users(UserId)");
                }

                using (var db = GetDatabase(SourceDatabaseName))
                {
                    // 1 <- 2 <- 3, then 1 and 2 are updated, so 3 comes first, then 1, then 2;
                    // with a cycle, 1 is updated to reference 3
                    db.Execute("insert into Users (Name, Age) values ('One', 1)");
                    db.Execute("insert into Users (Name, Age, ParentId) values ('Two', 2, 1)");
                    db.Execute("insert into Users (Name, Age, ParentId) values ('Three', 3, 2)");
                    db.Execute($"update Users set Age = 11{(cycle ? ", ParentId = 3" : "")} where UserId = 1");
                    db.Execute("update Users set Age = 12 where UserId = 2");
                }

                var (success, log) = SyncWithLog(new Synchronizer(TestConfig));

                Assert.That(success, Is.True);
                if (cycle)
                    Assert.That(log, Has.Some.EqualTo("Disabling foreign key constraint Users_ParentId_FK"));
                else
                    Assert.That(log, Has.None.StartsWith("Disabling foreign key constraint"));

                using (var db = GetDatabase(DestinationDatabaseName))
                {
                    var users = db.Fetch<string>("select concat(UserId, ':', Age, ':', ParentId) from Users order by UserId");
                    Assert.That(users, Is.EqualTo([cycle ? "1:11:3" : "1:11:", "2:12:1", "3:3:2"]));
                    AssertForeignKeysTrusted(db);
                }
            }
            finally
            {
                DropTable("Users");
            }
        }

        static void AssertForeignKeysTrusted(Database db)
        {
            var untrusted = db.ExecuteScalar<int>("select count(*) from sys.foreign_keys where is_not_trusted = 1 or is_disabled = 1");
            Assert.That(untrusted, Is.EqualTo(0));
        }

        /// <summary>
        /// Runs a synchronization and returns the messages it logged.
        /// </summary>
        static (bool Success, IList<string> Log) SyncWithLog(Synchronizer synchronizer)
        {
            var target = new NLog.Targets.MemoryTarget("memory") { Layout = "${message} ${exception:format=message}" };
            var config = NLog.LogManager.Configuration ?? new NLog.Config.LoggingConfiguration();
            config.AddRule(NLog.LogLevel.Debug, NLog.LogLevel.Fatal, target);
            NLog.LogManager.Configuration = config;

            try
            {
                var success = synchronizer.Sync();
                return (success, target.Logs.Select(l => l.Trim()).ToList());
            }
            finally
            {
                config.RemoveTarget("memory");
                NLog.LogManager.Configuration = config;
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

        static Config ConfigForTables(params string[] tables) => new()
        {
            ReplicationSets =
            {
                new ReplicationSet
                {
                    Name = "Test",
                    Source = new DatabaseInfo { Name = "Source", ConnectionString = GetConnectionString(SourceDatabaseName) },
                    Destinations = { new DatabaseInfo { Name = "Destination", ConnectionString = GetConnectionString(DestinationDatabaseName) } },
                    Tables = [.. tables]
                }
            }
        };

        [Test]
        public void KeyOnlyTableTest()
        {
            const string compare = "select concat(UserId, '-', RoleId) from UserRoles order by UserId, RoleId";

            try
            {
                DropTable("UserRoles");
                foreach (var dbName in new[] { SourceDatabaseName, DestinationDatabaseName })
                {
                    using var db = GetDatabase(dbName);
                    db.Execute("create table UserRoles (UserId int not null, RoleId int not null, primary key (UserId, RoleId))");
                    db.Execute("alter table UserRoles enable CHANGE_TRACKING with (TRACK_COLUMNS_UPDATED = OFF)");
                }

                using (var db = GetDatabase(SourceDatabaseName))
                    db.Execute("insert into UserRoles values (1, 1), (1, 2), (2, 1)");

                var config = ConfigForTables("UserRoles");
                Assert.That(new Synchronizer(config).Sync(), Is.True);

                using (var db = GetDatabase(SourceDatabaseName))
                {
                    // tracked as an update, although the table has no columns to update
                    db.Execute("update UserRoles set RoleId = RoleId where UserId = 1");
                    db.Execute("insert into UserRoles values (3, 3)");
                }

                var (success, log) = SyncWithLog(new Synchronizer(config));
                Assert.That(success, Is.True, () => string.Join(Environment.NewLine, log.Where(l => l.Contains("rror"))));

                using var source = GetDatabase(SourceDatabaseName);
                using var destination = GetDatabase(DestinationDatabaseName);
                Assert.That(destination.Fetch<string>(compare), Is.EqualTo(source.Fetch<string>(compare)));
            }
            finally
            {
                DropTable("UserRoles");
            }
        }

        [Test]
        public void GeneratedColumnsTest()
        {
            const string compare = "select concat(Id, '-', Name) from Users order by Id";

            try
            {
                DropTable("Users");
                foreach (var dbName in new[] { SourceDatabaseName, DestinationDatabaseName })
                {
                    using var db = GetDatabase(dbName);
                    db.Execute(@"create table Users (Id int primary key not null, Name nvarchar(50) null, RowVersion rowversion,
                        ValidFrom datetime2 generated always as row start hidden not null default sysutcdatetime(),
                        ValidTo datetime2 generated always as row end hidden not null default '9999-12-31 23:59:59.9999999',
                        period for system_time (ValidFrom, ValidTo))");
                    db.Execute("alter table Users enable CHANGE_TRACKING with (TRACK_COLUMNS_UPDATED = OFF)");
                }

                using (var db = GetDatabase(SourceDatabaseName))
                    db.Execute("insert into Users (Id, Name) values (1, N'Larry'), (2, N'Michael')");

                var config = ConfigForTables("Users");
                var (success, log) = SyncWithLog(new Synchronizer(config));
                Assert.That(success, Is.True, () => string.Join(Environment.NewLine, log.Where(l => l.Contains("rror"))));

                using (var db = GetDatabase(SourceDatabaseName))
                    db.Execute("update Users set Name = N'Larry Bird' where Id = 1");

                (success, log) = SyncWithLog(new Synchronizer(config));
                Assert.That(success, Is.True, () => string.Join(Environment.NewLine, log.Where(l => l.Contains("rror"))));

                using var source = GetDatabase(SourceDatabaseName);
                using var destination = GetDatabase(DestinationDatabaseName);
                Assert.That(destination.Fetch<string>(compare), Is.EqualTo(source.Fetch<string>(compare)));
            }
            finally
            {
                DropTable("Users");
            }
        }

        [Test]
        public void LoopWithFailingReplicationSetTest()
        {
            try
            {
                CreateUsersTable();

                var sourceUser = new User { Name = "Michael Jordan", Age = 54, DateOfBirth = new DateTime(1963, 2, 17), Savings = 1.31m * 1e9m };
                using (var db = GetDatabase(SourceDatabaseName))
                    db.Insert(sourceUser);

                // the first set cannot be initialized, which must not affect the second one
                var failing = new ReplicationSet
                {
                    Name = "Failing",
                    Source = new DatabaseInfo { Name = "Error", ConnectionString = GetConnectionString("Error") },
                    Destinations = { new DatabaseInfo { Name = "Destination", ConnectionString = GetConnectionString(DestinationDatabaseName) } },
                };
                var config = new Config { ReplicationSets = { failing, TestReplicationSet } };

                var synchronizer = new Synchronizer(config) { Interval = 600 };
                var synced = new ManualResetEventSlim(false);
                synchronizer.Synced += (s, e) => { if (e.ReplicationSet == TestReplicationSet) synced.Set(); };
                using var src = new CancellationTokenSource();
                var t = Task.Factory.StartNew(() => synchronizer.SyncLoop(src.Token), TaskCreationOptions.LongRunning);

                try
                {
                    Assert.That(synced.Wait(TimeSpan.FromSeconds(60)), Is.True, "second replication set was not replicated");
                }
                finally
                {
                    src.Cancel();
                    t.Wait(TimeSpan.FromSeconds(30));
                }

                using var destination = GetDatabase(DestinationDatabaseName);
                Assert.That(destination.Fetch<User>("select * from Users"), Is.EquivalentTo([sourceUser]));
            }
            finally
            {
                DropTable("Users");
            }
        }

        // creates the tables in both databases, with change tracking; constraints are created after all tables
        static void CreateTables(string[] tables, params string[] constraints)
        {
            foreach (var dbName in new[] { SourceDatabaseName, DestinationDatabaseName })
            {
                using var db = GetDatabase(dbName);
                foreach (var table in tables)
                {
                    db.Execute($"create table {table}");
                    db.Execute($"alter table {table[..table.IndexOf('(')].Trim()} enable CHANGE_TRACKING with (TRACK_COLUMNS_UPDATED = OFF)");
                }
                foreach (var constraint in constraints)
                    db.Execute(constraint);
            }
        }

        // drops the tables in both databases, in the given order
        static void DropTables(params string[] tables)
        {
            foreach (var table in tables)
                DropTable(table);
        }

        static void AssertSynced(Synchronizer synchronizer)
        {
            var (success, log) = SyncWithLog(synchronizer);
            Assert.That(success, Is.True, () => string.Join(Environment.NewLine, log.Where(l => l.Contains("rror"))));
        }

        static void AssertSameRows(string sql)
        {
            using var source = GetDatabase(SourceDatabaseName);
            using var destination = GetDatabase(DestinationDatabaseName);
            var expected = source.Fetch<string>(sql);
            Assert.That(expected, Is.Not.Empty);
            Assert.That(destination.Fetch<string>(sql), Is.EqualTo(expected));
        }

        const string ParentTable = "A_Parent (Id int primary key not null, Name nvarchar(20) null)";
        const string ChildTable = "B_Child (Id int primary key not null, ParentId int not null, Name nvarchar(20) null)";
        const string ChildForeignKey = "alter table B_Child add constraint B_Child_ParentId_FK foreign key (ParentId) references A_Parent(Id)";

        // the delete is moved after the changes of the rows that may reference it, without disabling a foreign key
        static void AssertDeferredDelete(Config config)
        {
            var (success, log) = SyncWithLog(new Synchronizer(config));
            Assert.That(success, Is.True, () => string.Join(Environment.NewLine, log.Where(l => l.Contains("rror"))));
            Assert.That(log, Has.Some.Contains("Deferred 1 delete"));
            Assert.That(log, Has.None.Contains("needs to be disabled"));
        }

        [Test]
        public void DeleteReferencedRowInSameTransactionTest()
        {
            try
            {
                DropTables("B_Child", "A_Parent");
                CreateTables([ParentTable, ChildTable], ChildForeignKey);
                var config = ConfigForTables("A_Parent", "B_Child");

                using (var db = GetDatabase(SourceDatabaseName))
                    db.Execute("insert into A_Parent values (1, N'one'), (2, N'two'); insert into B_Child values (1, 1, N'a'), (2, 2, N'b')");

                AssertSynced(new Synchronizer(config));

                // the parent's table sorts first, but the child has to be deleted first
                using (var db = GetDatabase(SourceDatabaseName))
                using (var transaction = db.GetTransaction())
                {
                    db.Execute("delete from B_Child where Id = 1");
                    db.Execute("delete from A_Parent where Id = 1");
                    transaction.Complete();
                }

                AssertDeferredDelete(config);
                AssertSameRows("select concat(Id, '-', Name) from A_Parent order by Id");
                AssertSameRows("select concat(Id, '-', ParentId, '-', Name) from B_Child order by Id");
            }
            finally
            {
                DropTables("B_Child", "A_Parent");
            }
        }

        [Test]
        public void DeleteReferencedRowThenUpdateReferencingRowTest()
        {
            try
            {
                DropTables("B_Child", "A_Parent");
                CreateTables([ParentTable, ChildTable], ChildForeignKey);
                var config = ConfigForTables("A_Parent", "B_Child");

                using (var db = GetDatabase(SourceDatabaseName))
                    db.Execute("insert into A_Parent values (1, N'one'), (2, N'two'); insert into B_Child values (1, 1, N'a')");

                AssertSynced(new Synchronizer(config));

                // the child's change comes after the parent's delete, as it was updated again later
                using (var db = GetDatabase(SourceDatabaseName))
                {
                    db.Execute("update B_Child set ParentId = 2 where Id = 1");
                    db.Execute("delete from A_Parent where Id = 1");
                    db.Execute("update B_Child set Name = N'a2' where Id = 1");
                }

                AssertDeferredDelete(config);
                AssertSameRows("select concat(Id, '-', Name) from A_Parent order by Id");
                AssertSameRows("select concat(Id, '-', ParentId, '-', Name) from B_Child order by Id");
            }
            finally
            {
                DropTables("B_Child", "A_Parent");
            }
        }

        [Test]
        public void DeleteReferencedRowWithUniqueValueReusedTest()
        {
            try
            {
                DropTables("B_Child", "A_Parent");
                CreateTables([ParentTable, ChildTable], ChildForeignKey,
                    "alter table A_Parent add constraint A_Parent_Name_UQ unique (Name)");
                var config = ConfigForTables("A_Parent", "B_Child");

                using (var db = GetDatabase(SourceDatabaseName))
                    db.Execute("insert into A_Parent values (1, N'one'), (2, N'two'); insert into B_Child values (1, 1, N'a')");

                AssertSynced(new Synchronizer(config));

                // moving the delete after the child's last update would put it after the insert that takes its name,
                // so the foreign key is disabled instead
                using (var db = GetDatabase(SourceDatabaseName))
                {
                    db.Execute("update B_Child set ParentId = 2 where Id = 1");
                    db.Execute("delete from A_Parent where Id = 1");
                    db.Execute("insert into A_Parent values (3, N'one')");
                    db.Execute("update B_Child set Name = N'a2' where Id = 1");
                }

                var (success, log) = SyncWithLog(new Synchronizer(config));
                Assert.That(success, Is.True, () => string.Join(Environment.NewLine, log.Where(l => l.Contains("rror"))));
                Assert.That(log, Has.Some.Contains("B_Child_ParentId_FK needs to be disabled"));
                AssertSameRows("select concat(Id, '-', Name) from A_Parent order by Id");
                AssertSameRows("select concat(Id, '-', ParentId, '-', Name) from B_Child order by Id");
            }
            finally
            {
                DropTables("B_Child", "A_Parent");
            }
        }

        [Test]
        public void SyncedFailureTest()
        {
            try
            {
                CreateUsersTable();

                using (var db = GetDatabase(SourceDatabaseName))
                    db.Insert(new User { Name = "Michael Jordan", Age = 54, DateOfBirth = new DateTime(1963, 2, 17), Savings = 1.31m * 1e9m });

                var replicationSet = new ReplicationSet
                {
                    Name = "Test",
                    Source = TestReplicationSet.Source,
                    Destinations = { new DatabaseInfo { Name = "Error", ConnectionString = GetConnectionString("Error") } },
                    Tables = ["Users"]
                };

                var synchronizer = new Synchronizer(new Config { ReplicationSets = { replicationSet } }) { Interval = 600 };
                SyncEventArgs args = null;
                var synced = new ManualResetEventSlim(false);
                synchronizer.Synced += (s, e) => { args = e; synced.Set(); };
                using var src = new CancellationTokenSource();
                var t = Task.Factory.StartNew(() => synchronizer.SyncLoop(src.Token), TaskCreationOptions.LongRunning);

                try
                {
                    Assert.That(synced.Wait(TimeSpan.FromSeconds(60)), Is.True);
                }
                finally
                {
                    src.Cancel();
                    t.Wait(TimeSpan.FromSeconds(30));
                }

                Assert.That(args.Success, Is.False);
            }
            finally
            {
                DropTable("Users");
            }
        }

        [Test]
        public void CaseInsensitiveForeignKeyTest()
        {
            try
            {
                DropTables("B_Child", "A_Parent");
                CreateTables(["A_Parent (Code nvarchar(10) primary key not null, Name nvarchar(20) null)",
                    "B_Child (Id int primary key not null, Code nvarchar(10) not null)"],
                    "alter table B_Child add constraint B_Child_Code_FK foreign key (Code) references A_Parent(Code)");
                var config = ConfigForTables("A_Parent", "B_Child");

                // the parent's insert comes after the child's, as the parent was updated later;
                // the child references it in other case, which the case-insensitive collation matches
                using (var db = GetDatabase(SourceDatabaseName))
                {
                    db.Execute("insert into A_Parent values (N'ABC', N'one')");
                    db.Execute("insert into B_Child values (1, N'abc')");
                    db.Execute("update A_Parent set Name = N'two'");
                }

                AssertSynced(new Synchronizer(config));
                AssertSameRows("select concat(Code, '-', Name) from A_Parent order by Code");
                AssertSameRows("select concat(Id, '-', Code) from B_Child order by Id");
            }
            finally
            {
                DropTables("B_Child", "A_Parent");
            }
        }

        [Test]
        public void IdentityNotKeyTest()
        {
            try
            {
                DropTables("Items");
                CreateTables(["Items (Id uniqueidentifier primary key not null default newid(), Seq int identity(1,1) not null, Name nvarchar(20) null)"]);
                var config = ConfigForTables("Items");

                using (var db = GetDatabase(SourceDatabaseName))
                    db.Execute("insert into Items (Name) values (N'a'), (N'b')");

                AssertSynced(new Synchronizer(config));

                using (var db = GetDatabase(SourceDatabaseName))
                    db.Execute("update Items set Name = N'c' where Name = N'a'");

                AssertSynced(new Synchronizer(config));
                AssertSameRows("select concat(Id, '-', Seq, '-', Name) from Items order by Seq");
            }
            finally
            {
                DropTables("Items");
            }
        }

        [Test]
        public void TableNameCaseTest()
        {
            try
            {
                DropTables("Items");
                CreateTables(["Items (Id int primary key not null, Name nvarchar(20) null)"]);

                using (var db = GetDatabase(SourceDatabaseName))
                    db.Execute("insert into Items values (1, N'a')");

                // SQL Server's names are not case-sensitive
                AssertSynced(new Synchronizer(ConfigForTables("items")));
                AssertSameRows("select concat(Id, '-', Name) from Items order by Id");

                using (var db = GetDatabase(SourceDatabaseName))
                    db.Execute("insert into Items values (2, N'b')");

                AssertSynced(new Synchronizer(ConfigForTables("DBO.ITEMS")));
                AssertSameRows("select concat(Id, '-', Name) from Items order by Id");
            }
            finally
            {
                DropTables("Items");
            }
        }

        [Test]
        public void DelimitedNamesTest()
        {
            // the databases are dropped after each test, with their tables
            CreateTables(["[Odd]]Parent] ([I]]d] int primary key not null, [Na]]me] nvarchar(20) null)",
                "[Odd]]Child] ([I]]d] int primary key not null, [Pa]]rent] int not null)"],
                "alter table [Odd]]Child] add constraint [Odd]]FK] foreign key ([Pa]]rent]) references [Odd]]Parent]([I]]d])");
            // a name with a ], as it is, and delimited
            var config = ConfigForTables("Odd]Parent", "[dbo].[Odd]]Child]");
            const string parents = "select concat([I]]d], '-', [Na]]me]) from [Odd]]Parent] order by 1";
            const string children = "select concat([I]]d], '-', [Pa]]rent]) from [Odd]]Child] order by 1";

            using (var db = GetDatabase(SourceDatabaseName))
                db.Execute("insert into [Odd]]Parent] values (1, N'a'); insert into [Odd]]Child] values (1, 1)");

            AssertSynced(new Synchronizer(config));
            AssertSameRows(children);

            // the child's update references a parent whose insert comes later, so the constraint is disabled
            using (var db = GetDatabase(SourceDatabaseName))
            {
                db.Execute("insert into [Odd]]Parent] values (2, N'b')");
                db.Execute("update [Odd]]Child] set [Pa]]rent] = 2");
                db.Execute("update [Odd]]Parent] set [Na]]me] = N'c' where [I]]d] = 2");
            }

            var (success, log) = SyncWithLog(new Synchronizer(config));
            Assert.That(success, Is.True, () => string.Join(Environment.NewLine, log.Where(l => l.Contains("rror"))));
            Assert.That(log, Has.Some.Contains("Odd]FK needs to be disabled"));
            AssertSameRows(parents);
            AssertSameRows(children);
        }

        [Test]
        public void NullConfigTest()
        {
            Assert.Throws<ArgumentException>(new Action(() => new Synchronizer(null)));
        }

        [Test]
        public void ConfigSettingsTest()
        {
            var config = Newtonsoft.Json.JsonConvert.DeserializeObject<Config>(@"{
                ""Timeout"": 300, ""Interval"": 10, ""BatchSize"": 500, ""DryRun"": true,
                ""ReplicationSets"": [ { ""Name"": ""Test"", ""Tables"": [ ""Users"" ] } ]
            }");

            var synchronizer = new Synchronizer(config);
            Assert.That(synchronizer.Timeout, Is.EqualTo(300));
            Assert.That(synchronizer.Interval, Is.EqualTo(10));
            Assert.That(synchronizer.BatchSize, Is.EqualTo(500));
            Assert.That(synchronizer.DryRun, Is.True);
            Assert.That(config.ReplicationSets.Single().Name, Is.EqualTo("Test"));

            // without settings, the defaults apply
            var defaults = new Synchronizer(Newtonsoft.Json.JsonConvert.DeserializeObject<Config>(@"{ ""ReplicationSets"": [] }"));
            Assert.That(defaults.Timeout, Is.EqualTo(0));
            Assert.That(defaults.Interval, Is.EqualTo(30));
            Assert.That(defaults.BatchSize, Is.EqualTo(100));
            Assert.That(defaults.DryRun, Is.False);
        }

        [Test]
        public void NoTablesTest()
        {
            var rs = new ReplicationSet
            {
                Name = "Test",
                Source = new DatabaseInfo { Name = "Source", ConnectionString = GetConnectionString(SourceDatabaseName) },
                Destinations = { new DatabaseInfo { Name = "Destination", ConnectionString = GetConnectionString(DestinationDatabaseName) } },
                Tables = ["Test"]
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
            Assert.Throws<Microsoft.Data.SqlClient.SqlException>(new Action(() => synchronizer.Sync()));
        }

        [Test]
        public void LoopTest()
        {
            try
            {
                CreateUsersTable();

                var sourceUsers = new List<User>
                {
                    new() { Name = "Michael Jordan", Age = 54, DateOfBirth = new DateTime(1963, 2, 17), Savings = 1.31m * 1e9m },
                    new() { Name = "Larry Bird", Age = 60, DateOfBirth = new DateTime(1956, 12, 7), Savings = 45m * 1e6m },
                    new() { Name = "Karl Malone", Age = 53, DateOfBirth = new DateTime(1963, 7, 24), Savings = 75m * 1e6m }
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

        [Test]
        public void LoopCancelDuringIntervalTest()
        {
            try
            {
                CreateUsersTable();

                using (var db = GetDatabase(SourceDatabaseName))
                    db.Insert(new User { Name = "Michael Jordan", Age = 54, DateOfBirth = new DateTime(1963, 2, 17), Savings = 1.31m * 1e9m });

                var synchronizer = new Synchronizer(TestConfig) { Interval = 600 };
                var synced = new ManualResetEventSlim(false);
                synchronizer.Synced += (s, e) => synced.Set();
                using var src = new CancellationTokenSource();
                var t = Task.Factory.StartNew(() => synchronizer.SyncLoop(src.Token), TaskCreationOptions.LongRunning);

                Assert.That(synced.Wait(TimeSpan.FromSeconds(60)), Is.True);

                src.Cancel();

                Assert.That(t.Wait(TimeSpan.FromSeconds(10)), Is.True, "loop did not stop while waiting for the next interval");
            }
            finally
            {
                DropTable("Users");
            }
        }

        [Test]
        public void CancelRunningCommandTest()
        {
            try
            {
                CreateUsersTable();

                var sourceUser = new User { Name = "Michael Jordan", Age = 54, DateOfBirth = new DateTime(1963, 2, 17), Savings = 1.31m * 1e9m };

                using (var db = GetDatabase(SourceDatabaseName))
                    db.Insert(sourceUser);

                using var src = new CancellationTokenSource();
                Task<bool> t;

                // a transaction holding an exclusive lock on the destination table blocks the replication's insert
                using (var blocker = GetDatabase(DestinationDatabaseName))
                {
                    blocker.BeginTransaction();
                    blocker.Execute("select count(*) from Users with (tablockx, holdlock)");

                    var synchronizer = new Synchronizer(TestConfig) { Timeout = 600 };
                    t = Task.Factory.StartNew(() => synchronizer.Sync(src.Token), TaskCreationOptions.LongRunning);

                    // wait until the replication is blocked on the lock
                    var deadline = DateTime.UtcNow.AddSeconds(60);
                    using (var monitor = GetDatabase(DestinationDatabaseName))
                    {
                        while (monitor.ExecuteScalar<int>("select count(*) from sys.dm_exec_requests where blocking_session_id <> 0 and database_id = DB_ID()") == 0)
                        {
                            Assert.That(DateTime.UtcNow, Is.LessThan(deadline), "replication did not block on the lock");
                            Assert.That(t.IsCompleted, Is.False, "replication finished without blocking");
                            Thread.Sleep(100);
                        }
                    }

                    src.Cancel();

                    var ex = Assert.Throws<AggregateException>(() => t.Wait(TimeSpan.FromSeconds(10)));
                    Assert.That(ex.InnerException, Is.InstanceOf<OperationCanceledException>());

                    blocker.AbortTransaction();
                }

                // the cancelled replication was rolled back
                using (var db = GetDatabase(DestinationDatabaseName))
                    Assert.That(db.Fetch<User>("select * from Users"), Is.Empty);

                // and a new replication starts from the same version
                Assert.That(new Synchronizer(TestConfig).Sync(), Is.True);

                using (var db = GetDatabase(DestinationDatabaseName))
                    Assert.That(db.Fetch<User>("select * from Users"), Is.EquivalentTo([sourceUser]));
            }
            finally
            {
                DropTable("Users");
            }
        }

        [Test]
        public void TransactionTest()
        {
            try
            {
                CreateUsersTable();
                CreateOrdersTable();
                CreateOrdersForeignKey(SourceDatabaseName);
                CreateOrdersForeignKey(DestinationDatabaseName);

                var sourceOrder = new Order { OrderId = 1 };
                var sourceUser = new User { Name = "Michael Jordan", Age = 54, DateOfBirth = new DateTime(1963, 2, 17), Savings = 1.31m * 1e9m };
                var sourceUser2 = new User { Name = "Larry Bird", Age = 60, DateOfBirth = new DateTime(1956, 12, 7), Savings = 45m * 1e6m };

                using (var db = GetDatabase(SourceDatabaseName))
                using (var transaction = db.GetTransaction())
                {
                    db.Insert(sourceUser);
                    db.Insert(sourceUser2);
                    sourceOrder.UserId = sourceUser.UserId;
                    db.Insert(sourceOrder);
                    sourceOrder.UserId = sourceUser2.UserId;
                    db.Update(sourceOrder);
                    transaction.Complete();
                }

                var synchronizer = new Synchronizer(TestConfig);
                var success = synchronizer.Sync();

                Assert.That(success, Is.True);

                using (var db = GetDatabase(DestinationDatabaseName))
                {
                    var orders = db.Fetch<Order>("select * from Orders");
                    Assert.That(orders.Count, Is.EqualTo(1));
                    Assert.That(orders[0], Is.EqualTo(sourceOrder));
                }
            }
            finally
            {
                DropTable("Orders");
                DropTable("Users");
            }
        }
    }
}
