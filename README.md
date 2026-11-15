SyncChanges
===========

[![NuGet version](https://badge.fury.io/nu/SyncChanges.svg)](http://badge.fury.io/nu/SyncChanges)
[![Build status](https://github.com/mganss/SyncChanges/actions/workflows/build.yml/badge.svg?branch=master)](https://github.com/mganss/SyncChanges/actions/workflows/build.yml)
[![Coverage](https://sonarcloud.io/api/project_badges/measure?project=mganss_SyncChanges&metric=coverage)](https://sonarcloud.io/dashboard?id=mganss_SyncChanges)
[![Sonarcloud Quality Gate](https://sonarcloud.io/api/project_badges/measure?project=mganss_SyncChanges&metric=alert_status)](https://sonarcloud.io/dashboard?id=mganss_SyncChanges)

A Windows service, console application, and library to synchronize/replicate database changes based on SQL Server [Change Tracking](https://msdn.microsoft.com/en-us/library/bb933875.aspx).

Motivation
----------

Microsoft SQL Server has a number of builtin synchronization features, such as Mirroring, Replication, and AlwaysOn Availability Groups. Unfortunately, all of these are only available from Standard Edition, and therefore not included in Web Edition or Express. Log shipping has the drawback that the secondary databases are not accessible during the restore. The solution provided by SyncChanges, on the other hand, builds upon Change Tracking, which is available in all editions, including Web and Express.

The use case SyncChanges was built for is a setup where you have a single database that all write operations go to (the source), and a number of other databases that are periodically kept in sync with the source (the destinations). All databases can be read from.

Usage
-----

SyncChanges can be used either as a console application that is typically invoked through a task scheduler every couple of minutes, as a Windows service, or as a library in your own applications (NuGet package `SyncChanges`).

The console application and the service are [.NET tools](https://learn.microsoft.com/en-us/dotnet/core/tools/global-tools), which need the .NET SDK to install. The console application then runs as `SyncChanges`:

```
dotnet tool install -g SyncChanges.Console
```

Alternatively, grab a zip of either from [releases](https://github.com/mganss/SyncChanges/releases), which needs only the .NET runtime. For installing the service, see [Service](#service).

```
Usage: SyncChanges [OPTION]... CONFIGFILE...
Replicate database changes.

Options:
  -h, --help                 Show this message and exit
  -d, --dryrun               Do not alter target databases, only perform a test
                               run
  -t, --timeout=VALUE        Database command timeout in seconds
  -l, --loop                 Perform replication in a loop, periodically
                               checking for changes
  -i, --interval=VALUE       Replication interval in seconds (default is 30);
                               only relevant in loop mode
  -b, --batchsize=VALUE      Maximum number of statements sent to a destination
                               database in one round trip (default is 100)
```

A configuration file looks like this:

```json
{
  "ReplicationSets": [
    {
      "Name": "Test",
      "Source": {
        "Name": "Primary",
        "ConnectionString": "Data Source=primary.example.com;Initial Catalog=Test;Integrated Security=True;MultipleActiveResultSets=True"
      },
      "Destinations": [
        {
          "Name": "Secondary 1",
          "ConnectionString": "Data Source=secondary1.example.com;Initial Catalog=Test;Integrated Security=True;MultipleActiveResultSets=True"
        },
        {
          "Name": "Secondary 2",
          "ConnectionString": "Data Source=secondary2.example.com;Initial Catalog=Test;Integrated Security=True;MultipleActiveResultSets=True"
        }
      ],
      "Tables": [ "Table1", "Table2", "Table3" ]
    }
  ]
}
```

`Tables` is optional. If you don't specify it, all tables will be replicated. A table can be named with or without its schema and brackets (e.g. `Users`, `dbo.Users` or `[dbo].[Users]`), regardless of case.

The configuration file can also hold settings that apply to all replication sets. Each is optional; the console application's options and, for the service, a setting left out of the configuration file in `SyncChanges.Service.dll.config` apply instead:

```json
{
  "Timeout": 300,
  "Interval": 10,
  "BatchSize": 500,
  "DryRun": false,
  "ReplicationSets": [ ... ]
}
```

- `Timeout`: database command timeout in seconds (0 or not set: the database client's default of 30 seconds)
- `Interval`: minimum time in seconds between replications in loop mode and in the service (default 30)
- `BatchSize`: maximum number of statements sent to a destination database in one round trip (default 100)
- `DryRun`: only perform a test run, do not alter destination databases (default `false`)

The console application's options override the configuration file's settings.

Change Tracking
---------------

Change Tracking must be enabled in the source database and the tables you want to replicate. This can be done either through SSMS or the following SQL:

```sql
alter database Test
set change_tracking = on
(change_retention = 2 days, auto_cleanup = on)

alter table Users
enable change_tracking
with (track_columns_updated = off)
```

More at MSDN: [Enable and Disable Change Tracking](https://msdn.microsoft.com/en-us/library/bb964713.aspx)

SyncChanges does not use the column tracking feature, which means on an update to a row, all non-primary-key columns will be updated, except an identity column, whose value cannot change.

Columns whose values the destination generates itself are not replicated: computed columns, `rowversion` (`timestamp`) columns and `GENERATED ALWAYS` columns, such as the period columns of temporal tables.

Note that change tracking does not have to be enabled in the destination databases. Therefore, SyncChanges will likely work with destination databases lower than SQL Server 2008, though this has not been tested. The source database must be at least SQL Server 2008.

In order to keep track of the current version, SyncChanges automatically creates a table called `SyncInfo` with a singleton row in the destination databases.

Replication is a multi-step process that can be affected by concurrent changes to the source database. Therefore, to obtain consistent and correct results it is strongly recommended to enable snapshot isolation in the source databases. More about this at MSDN: [Work with Change Tracking](https://msdn.microsoft.com/en-us/library/bb933874.aspx#Obtaining-Consistent-and-Correct-Results)

If snapshot isolation is not enabled, SyncChanges will still work but ignore changes that occurred after the current version of the source database was fetched. These will be applied during the next run.

Change tracking only tracks inserts, updates, and deletes. If you make structural changes to the source database, these must be applied to all destinations as well.

Foreign Key Constraints
-----------------------------------------------

Change Tracking in SQL Server combines inserts and subsequent updates to a single row into one change. For example, if you insert a row into a `Users` table with the `Name` column set to `Joe` and then perform an update to set the name to `Joseph`, Change Tracking will return only a single change record of type Insert with the `Name` column set to `Joseph`, i.e. the fact that the `Name` column had a different value upon insert is lost. The only information we get is the version number when the row was inserted and when it was last updated.

This can become a problem when you're dealing with foreign key constraints, specifically if a foreign key column's value differs from insert to last update. There are two aspects to this problem:

1. If you try to insert the row at the version number it was originally inserted into the source database (with a different, unknown value) the row in the referenced table might not exist yet.
2. If you try to defer insertion of the row to the version number it was last updated in the source database, rows in other tables referencing the row might have been inserted before this point, violating foreign key constraints.

SyncChanges replicates each change at the version of its last update. To overcome this problem, it defers the insert of a row that references a row inserted later until just after that insert. This needs no foreign key constraint to be disabled, and it cannot violate a unique constraint: until the deferred row is inserted it holds no values, and the rows changed in between then hold their final values, which agree with the deferred row's values as they do in the source database.

An update is not deferred, as its row keeps its old values in the meantime. For an update that references a row inserted later, and for inserts that reference each other in a cycle, SyncChanges disables (only) the corresponding foreign key constraints. They are re-enabled with a check of the existing rows once all changes are in, within the same transaction, so a violation still fails the replication. Note that disabling a constraint takes a schema modification lock on the table until the transaction ends, which blocks all readers of that table in the destination database, even those reading uncommitted data. The log says how many inserts were deferred and how many changes needed a constraint disabled.

Deletes have the opposite problem: a row has to be deleted after the rows that referenced it were deleted or changed to reference another row, but those changes may come later, e.g. because the referencing row was updated again later, or because the changes of one transaction come in the order of their tables. As a deleted row's values are no longer known, SyncChanges defers the delete of a row that another table references until after the last update or delete in that table. Where this might violate a constraint (the table has unique constraints and later changes may take the deleted row's values, or the row may reference a row that is deleted later), it disables the referencing foreign key constraints for the delete instead, as described above.

Logging
-------

SyncChanges uses [NLog](https://github.com/NLog/NLog). The default configuration logs to the console as well as a daily rolling file `log.txt` and keeps a maximum of 10 archived log files. The log file is in the same folder as the executable, or, for the .NET tool, in a `SyncChanges` folder in the user's local application data (e.g. `C:\Users\UserName\AppData\Local\SyncChanges`; `SyncChanges --help` shows where). To customize logging of the console application, put an `NLog.config` in the folder you run it from, which then replaces the default one; for the zip, you can also edit the `NLog.config` next to the executable.

Service
-------

In addition to the command line you can also run SyncChanges as a Windows service. The service periodically polls the value of [`CHANGE_TRACKING_CURRENT_VERSION`](https://docs.microsoft.com/en-us/sql/relational-databases/system-functions/change-tracking-current-version-transact-sql) in a configurable interval and starts replication if the version of the source has increased.

The service runs on .NET 10 (or later) and needs its runtime installed. There are two ways to get it:

- **As a .NET tool**, which needs the .NET SDK. Install it into a folder that the service's account can read, rather than with `-g` into your user profile:

  ```
  dotnet tool install SyncChanges.Service --tool-path C:\SyncChanges
  ```

  The service then reads its configuration from `%ProgramData%\SyncChanges\config.json` (e.g. `C:\ProgramData\SyncChanges\config.json`) and writes its log there, as the tool's own folder is replaced on each update. Installing the service puts a `config.example.json` there to start from.

- **As a zip** from [releases](https://github.com/mganss/SyncChanges/releases), which needs only the runtime. Unzip it to a folder of its own, e.g. `C:\SyncChanges.Service`, and copy `config.example.json` to `config.json` there: the service reads its configuration from the folder of the executable, and writes its log there.

The desired polling interval, command timeout and batch size can be configured in `config.json` (see above). For the zip, they can also be configured in the `SyncChanges.Service.dll.config` file, which applies where `config.json` doesn't set them. An `NLog.config` next to `config.json` replaces the default logging configuration. Note that `config.json` holds connection strings, so restrict access to its folder if they contain passwords.

To install and start the service, run from an elevated command prompt, e.g. for the tool:

```
C:\SyncChanges\SyncChanges.Service.exe install --start
```

or for the zip:

```
C:\SyncChanges.Service\SyncChanges.Service.exe install --start
```

By default, the service is named `SyncChangesService` and runs as LocalSystem. Options:

- `--user=ACCOUNT`: the account the service runs as, e.g. `.\UserName` for a local account, `DOMAIN\UserName`, `NetworkService`, `NT SERVICE\SyncChangesService` (a virtual account) or a group managed service account `DOMAIN\Name$`. The account needs the database permissions to carry out the replication process, and a user account needs the "Log on as a service" right.
- `--password=PASSWORD`: the account's password. If the account needs one and it is not given, you are asked for it, which keeps it out of the command history.
- `--config=FILE`: the configuration file, instead of the default above; the log is then written to its folder. Together with `--name`, this lets you install several services, each with its own configuration.
- `--name=NAME`: the name of the service.
- `--start`: start the service once it is installed.

To uninstall the service (it is stopped first if it is running):

```
C:\SyncChanges\SyncChanges.Service.exe uninstall
```

`sc.exe` works as well, e.g. `sc.exe start SyncChangesService` and `sc.exe stop SyncChangesService`.

To upgrade, stop the service first, as its files are in use while it runs. For the tool, run `dotnet tool update SyncChanges.Service --tool-path C:\SyncChanges`; for the zip, replace its files except `config.json` and any changed `.config` files with the new ones. Then start the service again. Services installed with `sc.exe` or, by versions for the .NET Framework, with `InstallUtil.exe` can be upgraded with the zip this way, and keep reading `config.json` from the folder of the executable.

Possible Improvements
----------------------------

- Use some change notification mechanism to trigger replication
- Use column change tracking
- Parallelize replication to destinations

Feel free to grab one of these and make a PR.
