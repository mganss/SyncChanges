using Mono.Options;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.ServiceProcess;
using System.Text;

namespace SyncChanges.Service
{
    /// <summary>
    /// Installs and uninstalls the service through the service control manager, as <c>sc.exe</c> does, but without
    /// a password on a command line that other processes can read.
    /// </summary>
    static class Installer
    {
        const string DefaultServiceName = "SyncChangesService";
        const string DisplayName = "SyncChanges";
        const string Description = "Replicates database changes using SQL Server Change Tracking.";

        public static bool IsCommand(string[] args) =>
            args.Length > 0 && args[0].ToLowerInvariant() is "install" or "uninstall" or "help" or "-h" or "--help" or "/?";

        public static int Run(string[] args)
        {
            var command = args[0].ToLowerInvariant();
            string name = DefaultServiceName, user = null, password = null, config = null;
            bool start = false, showHelp = false;

            var options = new OptionSet
            {
                { "n|name=", $"Name of the service (default is {DefaultServiceName})", v => name = v },
                { "u|user=", "Account the service runs as, e.g. .\\UserName, DOMAIN\\UserName, NetworkService or NT SERVICE\\SyncChangesService (default is LocalSystem)", v => user = v },
                { "p|password=", "Password of the account; asked for if the account needs one and it is not given", v => password = v },
                { "c|config=", $"Configuration file of the service (default is {ServicePaths.DefaultConfigFile}); the log is written to its folder", v => config = v },
                { "s|start", "Start the service once it is installed", v => start = v != null },
                { "h|help", "Show this message and exit", v => showHelp = v != null },
            };

            List<string> extra;

            try
            {
                extra = options.Parse(args[1..]);
            }
            catch (OptionException ex)
            {
                Console.Error.WriteLine(ex.Message);
                return 1;
            }

            if (showHelp || command is "help" or "-h" or "--help" or "/?")
            {
                ShowHelp(options);
                return 0;
            }

            if (extra.Count > 0)
            {
                Console.Error.WriteLine($"Unknown argument: {extra[0]}");
                return 1;
            }

            try
            {
                if (command == "install")
                    Install(name, user, password, config, start);
                else
                    Uninstall(name);
                return 0;
            }
            catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorAccessDenied)
            {
                Console.Error.WriteLine("Access denied. Run this from an elevated (administrator) command prompt.");
                return 1;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(ex.Message);
                return 1;
            }
        }

        public static void ShowHelp(OptionSet options = null)
        {
            Console.WriteLine("Usage: SyncChanges.Service install [OPTION]...");
            Console.WriteLine("       SyncChanges.Service uninstall [--name=NAME]");
            Console.WriteLine("Install or uninstall the SyncChanges Windows service. Requires an elevated command prompt.");
            Console.WriteLine($"The service reads its configuration from {ServicePaths.DefaultConfigFile}, unless --config is given.");
            if (options != null)
            {
                Console.WriteLine();
                Console.WriteLine("Options:");
                options.WriteOptionDescriptions(Console.Out);
            }
        }

        static void Install(string name, string user, string password, string config, bool start)
        {
            // Installed as a dotnet tool, this runs through the tool's launcher, whose path stays the same when the tool
            // is updated, unlike the folder of the program.
            var executable = ServicePaths.IsTool ? Environment.ProcessPath : Path.Combine(AppContext.BaseDirectory, "SyncChanges.Service.exe");
            if (executable == null || !executable.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || !File.Exists(executable))
                throw new InvalidOperationException($"SyncChanges.Service.exe not found{(executable == null ? "" : $" ({executable})")}. Run SyncChanges.Service.exe itself, not through dotnet.");

            var configFile = Path.GetFullPath(config ?? ServicePaths.DefaultConfigFile);
            PrepareConfig(configFile);

            var account = Account(user);
            if (account != null && password == null && NeedsPassword(account))
                password = ReadPassword(account);

            if (account != null && executable.StartsWith(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                Console.WriteLine($"Warning: {executable} is in your user profile, which {account} may not be able to read. "
                    + "Install the tool into a shared folder instead, e.g. dotnet tool install SyncChanges.Service --tool-path C:\\SyncChanges.");

            var manager = OpenSCManager(null, null, ScManagerCreateService);
            if (manager == IntPtr.Zero) throw new Win32Exception();

            try
            {
                var service = CreateService(manager, name, DisplayName, ServiceAllAccess, ServiceWin32OwnProcess,
                    ServiceAutoStart, ServiceErrorNormal, $"\"{executable}\" --config \"{configFile}\"", null, IntPtr.Zero, null, account, password);
                if (service == IntPtr.Zero)
                {
                    var error = Marshal.GetLastWin32Error();
                    if (error == ErrorServiceExists)
                        throw new InvalidOperationException($"A service named {name} already exists. Uninstall it first, or choose another name with --name.");
                    if (error == ErrorInvalidServiceAccount)
                        throw new InvalidOperationException($"The account {account} does not exist or cannot run a service.");
                    throw new Win32Exception(error);
                }

                try
                {
                    var description = new ServiceDescription { Description = Description };
                    ChangeServiceConfig2(service, ServiceConfigDescription, ref description);
                }
                finally
                {
                    CloseServiceHandle(service);
                }
            }
            finally
            {
                CloseServiceHandle(manager);
            }

            Console.WriteLine($"Installed service {name} running as {account ?? "LocalSystem"}, with configuration file {configFile}.");
            if (account != null && NeedsPassword(account))
                Console.WriteLine("The account needs the \"Log on as a service\" right and the database permissions for the replication.");

            if (start)
            {
                using var controller = new ServiceController(name);
                controller.Start();
                controller.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(30));
                Console.WriteLine($"Started service {name}.");
            }
        }

        static void Uninstall(string name)
        {
            var manager = OpenSCManager(null, null, ScManagerConnect);
            if (manager == IntPtr.Zero) throw new Win32Exception();

            try
            {
                var service = OpenService(manager, name, ServiceStop | ServiceQueryStatus | Delete);
                if (service == IntPtr.Zero)
                {
                    var error = Marshal.GetLastWin32Error();
                    if (error == ErrorServiceDoesNotExist)
                        throw new InvalidOperationException($"There is no service named {name}.");
                    throw new Win32Exception(error);
                }

                try
                {
                    using (var controller = new ServiceController(name))
                    {
                        if (controller.Status != ServiceControllerStatus.Stopped)
                        {
                            Console.WriteLine($"Stopping service {name}...");
                            if (controller.Status != ServiceControllerStatus.StopPending)
                                controller.Stop();
                            controller.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromMinutes(2));
                        }
                    }

                    if (!DeleteService(service)) throw new Win32Exception();
                }
                finally
                {
                    CloseServiceHandle(service);
                }
            }
            finally
            {
                CloseServiceHandle(manager);
            }

            Console.WriteLine($"Uninstalled service {name}.");
        }

        // Creates the configuration file's folder, puts the example configuration there if it has none, and warns if the
        // configuration file does not exist yet.
        static void PrepareConfig(string configFile)
        {
            var directory = Path.GetDirectoryName(configFile);
            Directory.CreateDirectory(directory);

            var example = Path.Combine(AppContext.BaseDirectory, "config.example.json");
            var exampleCopy = Path.Combine(directory, "config.example.json");
            if (!File.Exists(configFile) && File.Exists(example) && !File.Exists(exampleCopy))
                File.Copy(example, exampleCopy);

            if (!File.Exists(configFile))
                Console.WriteLine($"Warning: {configFile} does not exist yet; the service needs it to start. "
                    + $"You can start from {exampleCopy}.");
        }

        // The account name as the service control manager expects it; null for LocalSystem.
        static string Account(string user)
        {
            if (string.IsNullOrWhiteSpace(user)) return null;
            user = user.Trim();

            switch (user.ToLowerInvariant())
            {
                case "localsystem": return null;
                case "localservice": return @"NT AUTHORITY\LocalService";
                case "networkservice": return @"NT AUTHORITY\NetworkService";
            }

            // a local account has to be given as .\UserName
            return user.Contains('\\') || user.Contains('@') ? user : @".\" + user;
        }

        // The built-in and virtual service accounts, and group managed service accounts (DOMAIN\Name$), have no password.
        static bool NeedsPassword(string account) =>
            !account.StartsWith(@"NT AUTHORITY\", StringComparison.OrdinalIgnoreCase)
            && !account.StartsWith(@"NT SERVICE\", StringComparison.OrdinalIgnoreCase)
            && !account.EndsWith("$", StringComparison.Ordinal);

        static string ReadPassword(string account)
        {
            if (Console.IsInputRedirected)
                throw new InvalidOperationException($"The account {account} needs a password; give it with --password.");

            Console.Write($"Password for {account}: ");
            var password = new StringBuilder();

            while (true)
            {
                var key = Console.ReadKey(intercept: true);
                if (key.Key == ConsoleKey.Enter) break;
                if (key.Key == ConsoleKey.Backspace)
                {
                    if (password.Length > 0) password.Length--;
                }
                else if (key.KeyChar != '\0')
                    password.Append(key.KeyChar);
            }

            Console.WriteLine();
            return password.ToString();
        }

        const uint ScManagerConnect = 0x0001;
        const uint ScManagerCreateService = 0x0002;
        const uint ServiceAllAccess = 0xF01FF;
        const uint ServiceQueryStatus = 0x0004;
        const uint ServiceStop = 0x0020;
        const uint Delete = 0x10000;
        const uint ServiceWin32OwnProcess = 0x10;
        const uint ServiceAutoStart = 2;
        const uint ServiceErrorNormal = 1;
        const uint ServiceConfigDescription = 1;
        const int ErrorAccessDenied = 5;
        const int ErrorServiceDoesNotExist = 1060;
        const int ErrorServiceExists = 1073;
        const int ErrorInvalidServiceAccount = 1057;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct ServiceDescription
        {
            public string Description;
        }

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "OpenSCManagerW")]
        static extern IntPtr OpenSCManager(string machineName, string databaseName, uint desiredAccess);

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "CreateServiceW")]
        static extern IntPtr CreateService(IntPtr manager, string serviceName, string displayName, uint desiredAccess,
            uint serviceType, uint startType, uint errorControl, string binaryPathName, string loadOrderGroup,
            IntPtr tagId, string dependencies, string serviceStartName, string password);

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "OpenServiceW")]
        static extern IntPtr OpenService(IntPtr manager, string serviceName, uint desiredAccess);

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "ChangeServiceConfig2W")]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool ChangeServiceConfig2(IntPtr service, uint infoLevel, ref ServiceDescription info);

        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool DeleteService(IntPtr service);

        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool CloseServiceHandle(IntPtr handle);
    }
}
