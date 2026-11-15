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
                    Assert.That(db.Fetch<User>("select * from Users"), Is.EquivalentTo(new[] { sourceUser, sourceUser2 }));
                    Assert.That(db.Fetch<Order>("select * from Orders"), Is.EquivalentTo(new[] { sourceOrder, sourceOrder2 }));
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
                    Assert.That(db.Fetch<User>("select * from Users"), Is.EquivalentTo(new[] { otherUser, sourceUser }));
                    Assert.That(db.Fetch<Order>("select * from Orders"), Is.EquivalentTo(new[] { sourceOrder }));
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
                    Assert.That(db.Fetch<User>("select * from Users"), Is.EquivalentTo(new[] { sourceUser, sourceUser2 }));
                    Assert.That(db.Fetch<Order>("select * from Orders"), Is.EquivalentTo(new[] { sourceOrder }));
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
                    Assert.That(users, Is.EqualTo(new[] { cycle ? "1:11:3" : "1:11:", "2:12:1", "3:3:2" }));
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

        [Test]
        public void NullConfigTest()
        {
            Assert.Throws<ArgumentException>(new Action(() => new Synchronizer(null)));
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
