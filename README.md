SyncChanges
===========

[![NuGet version](https://badge.fury.io/nu/SyncChanges.svg)](http://badge.fury.io/nu/SyncChanges)
[![Build status](https://ci.appveyor.com/api/projects/status/pn3y41ltb8tcq4kk?svg=true)](https://ci.appveyor.com/project/mganss/syncchanges/branch/master)
[![codecov.io](https://codecov.io/github/mganss/SyncChanges/coverage.svg?branch=master)](https://codecov.io/github/mganss/SyncChanges?branch=master)

A Windows service, console application, and library to synchronize/replicate database changes based on SQL Server [Change Tracking](https://msdn.microsoft.com/en-us/library/bb933875.aspx).

Motivation
----------

Microsoft SQL Server has a number of builtin synchronization features, such as Mirroring, Replication, and AlwaysOn Availability Groups. Unfortunately, all of these are only available from Standard Edition, and therefore not included in Web Edition or Express. Log shipping has the drawback that the secondary databases are not accessible during the restore. The solution provided by SyncChanges, on the other hand, builds upon Change Tracking, which is available in all editions, including Web and Express.

The use case SyncChanges was built for is a setup where you have a single database that all write operations go to (the source), and a number of other databases that are periodically kept in sync with the source (the destinations). All databases can be read from.

Usage
-----

SyncChanges can be used either as a console application that is typically invoked through a task scheduler every couple of minutes, as a Windows service, or as a library in your own applications. If you want to use the service or console application just grab a zip from [releases](https://github.com/mganss/SyncChanges/releases).

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

`Tables` is optional. If you don't specify it, all tables will be replicated.

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

SyncChanges does not use the column tracking feature, which means on an update to a row, all non-primary-key columns will be updated.

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

To overcome this problem, SyncChanges determines all occurrences of these kinds of deadlocks and disables (only) the corresponding foreign key constraints for the minimum amount of time possible.

Logging
-------

SyncChanges uses [NLog](https://github.com/NLog/NLog). If you're using the console application, you can customize the `NLog.config` to your needs. The default configuration logs to the console as well as a daily rolling file `log.txt` in the same folder as the executable and keeps a maximum of 10 archived log files.

Service
-------

In addition to the command line you can also run SyncChanges as a Windows service. The service periodically polls the value of [`CHANGE_TRACKING_CURRENT_VERSION`](https://docs.microsoft.com/en-us/sql/relational-databases/system-functions/change-tracking-current-version-transact-sql) in a configurable interval and starts replication if the version of the source has increased.

The service expects a `config.json` configuration file in the same folder as the service executable. The desired polling interval can be configured in the `SyncChanges.Service.exe.config` file.

To install the service, use the [InstallUtil.exe](https://msdn.microsoft.com/en-us/library/50614e95%28v=vs.110%29.aspx) tool that comes with the .NET Framework installation:

```
C:\Windows\Microsoft.NET\Framework64\v4.0.30319\InstallUtil.exe .\SyncChanges.Service.exe
```

During installation, you have to enter credentials for the user account the service will use. This has to be a fully qualified name, e.g. if it's a local account enter `.\UserName`. The user has to have the necessary database permissions to carry out the replication process.

To start the service:

```
net start SyncChangesService
```

Possible Improvements
----------------------------

- Use some change notification mechanism to trigger replication
- Use column change tracking
- Apply large amount of changes in batches (of configurable size)
- Parallelize replication to destinations

Feel free to grab one of these and make a PR.
